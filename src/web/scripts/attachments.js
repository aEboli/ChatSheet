import { request, logToHost } from './bridge.js';

// 附件管理。两类：图片与文本文件。
//
// 两个入口：粘贴、拖入。都不需要按钮——从 Excel 截个图直接 Ctrl+V 问
// 「这块怎么算」，或把一个 CSV 拖进来说「按这个格式排」，比先点按钮
// 再在文件对话框里找路径快得多。没有第三个入口是刻意的：一个仅为兜底
// 存在的「添加」按钮，会占掉操作栏本就不够的横向空间。
//
// 文件在面板侧就读成文本，不传二进制：加载项那边不落盘、也没有解析
// 二进制格式的能力，能送进模型的只有文字。因此这里只接受文本类扩展名，
// 二进制格式（xlsx、pdf 等）当场拒绝并说明原因。

let imageLimits = {
  maxCount: 6,
  maxBytes: 5 * 1024 * 1024,
  mediaTypes: ['image/png', 'image/jpeg', 'image/webp'],
};

let fileLimits = {
  maxCount: 4,
  maxBytes: 64 * 1024,
  maxTotalBytes: 128 * 1024,
  extensions: ['.txt', '.md', '.csv', '.tsv', '.json', '.xml', '.yaml', '.yml', '.log'],
};

/** 当前待发送的附件，图片与文件混在一个数组里，按加入顺序排列。 */
let items = [];
let nextId = 0;
let onNotice = null;

/**
 * 常见二进制格式的专门说明。
 *
 * 单说「不支持」不够：用户拖 xlsx 进来是完全合理的期待，得告诉他
 * 更好的做法（直接在 Excel 里打开，我能读当前工作簿）。
 */
const BINARY_HINTS = {
  '.xlsx': '直接在 Excel 里打开它，我能读你当前打开的工作簿',
  '.xls': '直接在 Excel 里打开它，我能读你当前打开的工作簿',
  '.xlsm': '直接在 Excel 里打开它，我能读你当前打开的工作簿',
  '.docx': '可以另存为 txt 或 md 后再拖进来',
  '.doc': '可以另存为 txt 或 md 后再拖进来',
  '.pdf': '可以复制其中的文字直接粘贴到输入框',
  '.zip': '解压后把其中的文本文件拖进来',
};

function container() {
  return document.getElementById('attachments');
}

function notify(message, variant = 'warn') {
  if (typeof onNotice === 'function') {
    onNotice(message, variant);
  }
}

/** 从后端取限制值，避免前后端各写一套上限。 */
async function loadLimits() {
  try {
    const result = await request('image.limits');
    if (result?.maxCount) { imageLimits = result; }
  } catch {
    // 取不到就用内置默认值，后端仍会做最终校验。
  }

  try {
    const result = await request('file.limits');
    if (result?.maxCount) { fileLimits = result; }
  } catch {
    // 同上。
  }
}

function formatSize(bytes) {
  if (bytes >= 1024 * 1024) {
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
  }

  // 不足 1 KB 直接报字节数。原先一律按 KB 取整，一个 23 字节的小 CSV
  // 会显示成「0 KB」——看着像读取失败，而它其实读得很好。
  // 这个下限对图片没意义（没有几百字节的图），但文件常有。
  if (bytes < 1024) {
    return `${bytes} B`;
  }

  return `${Math.round(bytes / 1024)} KB`;
}

/** 取小写扩展名，含点号；没有扩展名时返回空串。 */
function extensionOf(name) {
  const text = String(name ?? '');
  const dot = text.lastIndexOf('.');
  return dot > 0 ? text.slice(dot).toLowerCase() : '';
}

/** 把 Blob 读成 data URL。 */
function readAsDataUrl(blob) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result);
    reader.onerror = () => reject(new Error('读取图片失败'));
    reader.readAsDataURL(blob);
  });
}

/** 读成字节。解码交给 decodeBytes——先要拿到原始字节才能判断编码。 */
function readAsBytes(blob) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(new Uint8Array(reader.result));
    reader.onerror = () => reject(new Error('读取文件失败'));
    reader.readAsArrayBuffer(blob);
  });
}

/**
 * 无 BOM 时的尝试顺序。
 *
 * 顺序不能反。GB18030 的编码空间几乎铺满了双字节区间，拿它去解 UTF-8 文件
 * 不会报错，只会解出「鍚嶃О」这类乱码；而 UTF-8 的多字节结构有严格约束，
 * 拿它去解 GBK 一定失败。所以先 UTF-8、后 GB18030，第一个成功的就是答案。
 *
 * 用 gb18030 而不是 gbk：前者是后者的超集，多覆盖一批生僻字，对已是 GBK 的
 * 文件结果相同。对用户仍显示为「GBK」——中文 Windows 上大家认的是这个名字。
 */
const FALLBACK_ENCODINGS = [
  { decoder: 'utf-8', label: 'UTF-8' },
  { decoder: 'gb18030', label: 'GBK' },
];

/** BOM 到编码的映射。有 BOM 就不必猜，它是明确声明。 */
const BOM_ENCODINGS = [
  { bytes: [0xef, 0xbb, 0xbf], decoder: 'utf-8', label: 'UTF-8' },
  { bytes: [0xff, 0xfe], decoder: 'utf-16le', label: 'UTF-16LE' },
  { bytes: [0xfe, 0xff], decoder: 'utf-16be', label: 'UTF-16BE' },
];

function startsWith(bytes, prefix) {
  if (bytes.length < prefix.length) { return false; }
  return prefix.every((b, i) => bytes[i] === b);
}

/**
 * 按编码解码字节，返回 { text, encoding }；识别不出时返回 null。
 *
 * 为什么必须做编码识别：中文 Windows 上 Excel 导出 CSV 默认是 GBK。
 * 固定按 UTF-8 宽松解码不会报错，只会把汉字变成替换字符——用户看到文件条
 * 一切正常，模型收到的却是乱码，全程没有任何提示。这是最难发现的一类失败。
 *
 * 用 fatal 模式逐个试：解不动就抛异常，换下一个。宽松模式永远「成功」，
 * 就没法用成败来判断编码了。
 */
function decodeBytes(bytes) {
  for (const candidate of BOM_ENCODINGS) {
    if (!startsWith(bytes, candidate.bytes)) { continue; }
    try {
      // BOM 已明确声明编码，此处不再用 fatal——声明了 UTF-16 却有个别坏字节时，
      // 拒收整个文件不如按声明解出来，让后面的替换字符检查去判断。
      const text = new TextDecoder(candidate.decoder).decode(bytes);
      return { text, encoding: candidate.label };
    } catch {
      return null;
    }
  }

  for (const candidate of FALLBACK_ENCODINGS) {
    try {
      const text = new TextDecoder(candidate.decoder, { fatal: true }).decode(bytes);
      return { text, encoding: candidate.label };
    } catch {
      // 解不动，试下一个。
    }
  }

  return null;
}

/** UTF-8 字节数。上限按它算，不按原始文件大小——见 acceptFile。 */
function utf8Length(text) {
  return new TextEncoder().encode(text).length;
}

function countOf(kind) {
  return items.filter((i) => i.kind === kind).length;
}

function totalFileBytes() {
  return items.filter((i) => i.kind === 'file').reduce((sum, i) => sum + i.size, 0);
}

/**
 * 收下一张图片。返回是否收下。
 * 不合规的逐个说明原因，不静默丢弃——用户以为图片发出去了、模型却看不到，
 * 比明确报错难查得多。
 */
async function acceptImage(file, staged) {
  if (file.size > imageLimits.maxBytes) {
    notify(`${file.name || '图片'} 为 ${formatSize(file.size)}，超过 ${formatSize(imageLimits.maxBytes)} 上限。`);
    return null;
  }

  if (countOf('image') + staged >= imageLimits.maxCount) {
    notify(`一次最多附带 ${imageLimits.maxCount} 张图片，其余已忽略。`);
    return null;
  }

  try {
    const dataUrl = await readAsDataUrl(file);
    return {
      kind: 'image',
      id: `img${nextId++}`,
      // 粘贴来的 Blob 没有文件名，给个可辨识的默认值。
      name: file.name || `粘贴的图片-${nextId}.png`,
      type: file.type,
      size: file.size,
      dataUrl,
    };
  } catch (error) {
    notify(`${file.name || '图片'} 读取失败：${error.message}`);
    return null;
  }
}

/** 收下一个文本文件。返回条目或 null。 */
async function acceptFile(file, staged, stagedBytes) {
  const name = file.name || '文件';
  const extension = extensionOf(name);

  if (BINARY_HINTS[extension]) {
    notify(`${name} 是二进制格式，读不出文字。${BINARY_HINTS[extension]}。`);
    return null;
  }

  if (!fileLimits.extensions.includes(extension)) {
    notify(
      `${name} 的类型${extension ? ` ${extension}` : ''}不在支持范围内。` +
        `可附带的是文本文件：${fileLimits.extensions.join('、')}。`,
    );
    return null;
  }

  if (countOf('file') + staged >= fileLimits.maxCount) {
    notify(`一次最多附带 ${fileLimits.maxCount} 个文件，其余已忽略。`);
    return null;
  }

  // 粗筛，避免为一个明显过大的文件白读一遍字节。
  // 真正的上限判断在解码之后——上限按 UTF-8 字节算，而原始字节数不等于它。
  if (file.size > fileLimits.maxBytes * 4) {
    notify(`${name} 为 ${formatSize(file.size)}，远超单个文件 ${formatSize(fileLimits.maxBytes)} 上限。`);
    return null;
  }

  let bytes;
  try {
    bytes = await readAsBytes(file);
  } catch (error) {
    notify(`${name} 读取失败：${error.message}`);
    return null;
  }

  // NUL 字节是二进制的可靠特征：扩展名可以被改，内容不会骗人。
  // 在解码前按字节判断。UTF-16 文本里半数字节本就是 0x00，因此先排除带
  // UTF-16 BOM 的情况——那种由 BOM 分支正常解码，不该被当成二进制。
  const isUtf16 = startsWith(bytes, [0xff, 0xfe]) || startsWith(bytes, [0xfe, 0xff]);
  if (!isUtf16 && bytes.includes(0)) {
    notify(`${name} 看起来是二进制文件，读不出文字，已跳过。`);
    return null;
  }

  const decoded = decodeBytes(bytes);
  if (decoded === null) {
    notify(
      `${name} 的文字编码无法识别（已试过 UTF-8 与 GBK）。` +
        `请用记事本或 Excel 另存为 UTF-8 后再拖进来。`,
    );
    return null;
  }

  let text = decoded.text;

  // 去掉 BOM：留着会成为正文第一个字符，在代码或 JSON 里都是干扰。
  if (text.charCodeAt(0) === 0xfeff) {
    text = text.slice(1);
  }

  // 替换字符是「编码猜错了」的信号。BOM 分支用的是宽松解码，声明与实际
  // 不符时会解出一片 U+FFFD——那样的内容送进模型只是白占预算。
  if (text.includes('�')) {
    notify(`${name} 按 ${decoded.encoding} 解码后出现乱码，已跳过。请另存为 UTF-8 后再试。`);
    return null;
  }

  // 上限按 UTF-8 字节算，不按原始文件大小。
  //
  // 两者不等：GBK 的汉字是 2 字节、UTF-8 是 3 字节，一个 44 KB 的 GBK 中文
  // 文件解出来是 66 KB。若面板按原始大小放行、加载项按 UTF-8 字节判超限，
  // 就会出现「拖进来没报错、一发送就被退回」。
  const bytesUtf8 = utf8Length(text);

  if (bytesUtf8 > fileLimits.maxBytes) {
    const grew = decoded.encoding === 'UTF-8'
      ? ''
      : `（${decoded.encoding} 原文 ${formatSize(file.size)}，转 UTF-8 后变大）`;
    notify(
      `${name} 为 ${formatSize(bytesUtf8)}${grew}，` +
        `超过单个文件 ${formatSize(fileLimits.maxBytes)} 上限。`,
    );
    return null;
  }

  if (totalFileBytes() + stagedBytes + bytesUtf8 > fileLimits.maxTotalBytes) {
    notify(
      `文件合计将超过 ${formatSize(fileLimits.maxTotalBytes)} 上限，${name} 未添加。` +
        `文件内容会整段进入上下文，因此总量也有限制。`,
    );
    return null;
  }

  return {
    kind: 'file',
    id: `file${nextId++}`,
    name,
    type: file.type,
    // 存 UTF-8 字节数：合计上限与加载项侧的判断都按它算。
    size: bytesUtf8,
    encoding: decoded.encoding,
    text,
    lines: text.split('\n').length,
  };
}

/**
 * 添加一批文件，按类型分流。
 *
 * 分类不能只看 MIME：Windows 会把 .csv 报成 application/vnd.ms-excel，
 * 把 .md 报成空串。因此图片按 MIME 判断（浏览器对图片的判断可靠），
 * 其余一律按扩展名走文本路径。
 */
async function addFiles(files) {
  const accepted = [];
  let stagedImages = 0;
  let stagedFiles = 0;
  let stagedBytes = 0;

  for (const file of files) {
    if (!file) { continue; }

    const isImageType = (file.type ?? '').startsWith('image/');

    if (isImageType && !imageLimits.mediaTypes.includes(file.type)) {
      notify(`${file.name || '图片'} 的格式 ${file.type} 不受支持，请用 PNG、JPEG 或 WebP。`);
      continue;
    }

    if (isImageType) {
      const item = await acceptImage(file, stagedImages);
      if (item) {
        accepted.push(item);
        stagedImages += 1;
      }
      continue;
    }

    const item = await acceptFile(file, stagedFiles, stagedBytes);
    if (item) {
      accepted.push(item);
      stagedFiles += 1;
      stagedBytes += item.size;
    }
  }

  if (accepted.length > 0) {
    items = items.concat(accepted);
    render();
    void logToHost(
      `已添加附件 ${accepted.length} 项，当前共 ${countOf('image')} 张图片、` +
        `${countOf('file')} 个文件（合计 ${Math.round(totalFileBytes() / 1024)} KB）`,
    );
  }
}

function removeItem(id) {
  items = items.filter((i) => i.id !== id);
  render();
}

/**
 * 文档图标：右上角折角的纸。
 *
 * 导出而非各处自己画：这个图标要出现在两处——输入区的附件条与发出后的
 * 气泡文件条。此前气泡那处漏了图标，同一个文件发送前有图标、发送后变成
 * 纯文字胶囊，看着像两种不同的东西。共用一个工厂函数后改一处两处都生效。
 *
 * 尺寸不写在这里，由各处的 class 用 em 给——两处字号不同，写死 px 会有一处
 * 比例失调。
 */
export function createFileGlyph(className) {
  const glyph = document.createElement('span');
  glyph.className = className;
  glyph.setAttribute('aria-hidden', 'true');
  // innerHTML 写死的静态图形，不含任何外部输入。
  glyph.innerHTML =
    '<svg viewBox="0 0 16 16"><path d="M9.2 1.8H4.2v12.4h7.6V4.4z" />' +
    '<path d="M9.2 1.8v2.6h2.6" /></svg>';
  return glyph;
}

function buildRemoveButton(item, className) {
  const remove = document.createElement('button');
  remove.type = 'button';
  remove.className = className;
  remove.textContent = '×';
  remove.title = `移除 ${item.name}`;
  remove.setAttribute('aria-label', `移除 ${item.name}`);
  remove.addEventListener('click', () => removeItem(item.id));
  return remove;
}

function buildImageCard(item) {
  const card = document.createElement('div');
  card.className = 'attachment';

  const thumb = document.createElement('img');
  thumb.className = 'attachment-thumb';
  thumb.src = item.dataUrl;
  thumb.alt = item.name;

  card.title = `${item.name}（${formatSize(item.size)}）`;
  card.append(thumb, buildRemoveButton(item, 'attachment-remove'));
  return card;
}

function buildFileCard(item) {
  const card = document.createElement('div');
  card.className = 'attachment-file';

  const name = document.createElement('span');
  name.className = 'attachment-file-name';
  name.textContent = item.name;

  const size = document.createElement('span');
  size.className = 'attachment-file-size';
  size.textContent = formatSize(item.size);

  card.append(createFileGlyph('attachment-file-glyph'), name, size);

  // 非 UTF-8 的编码要标出来。编码终究是猜的，猜错的后果是「界面正常、
  // 模型收到乱码」——把猜测结果摆在明面上，用户才有机会发现不对。
  // UTF-8 不标：那是绝大多数情况，标了只是噪声。
  if (item.encoding && item.encoding !== 'UTF-8') {
    const encoding = document.createElement('span');
    encoding.className = 'attachment-file-encoding';
    encoding.textContent = item.encoding;
    card.append(encoding);
  }

  // 悬停给出完整信息：名字在条上会被截断，行数说明内容有多长，
  // 编码说明这份文字是按什么解出来的。
  card.title =
    `${item.name}（${item.lines} 行，${formatSize(item.size)}）\n` +
    `编码：${item.encoding ?? '未知'}\n整段内容会随消息发给模型`;

  card.append(buildRemoveButton(item, 'attachment-file-remove'));
  return card;
}

function render() {
  const box = container();
  if (!box) { return; }

  box.replaceChildren();
  box.hidden = items.length === 0;

  if (items.length === 0) { return; }

  for (const item of items) {
    box.append(item.kind === 'image' ? buildImageCard(item) : buildFileCard(item));
  }

  // 只报有内容的那一类：两类都写死会长期显示「文件 0/4」这种无用信息。
  const parts = [];
  if (countOf('image') > 0) { parts.push(`图片 ${countOf('image')}/${imageLimits.maxCount}`); }
  if (countOf('file') > 0) { parts.push(`文件 ${countOf('file')}/${fileLimits.maxCount}`); }

  const summary = document.createElement('span');
  summary.className = 'attachment-summary';
  summary.textContent = parts.join(' · ');
  box.append(summary);
}

/** 取出待发送的图片载荷。 */
export function getImages() {
  return items.filter((i) => i.kind === 'image').map((i) => ({ name: i.name, dataUrl: i.dataUrl }));
}

/** 取出待发送的文件载荷。文本已在面板侧解码，加载项直接拼进消息。 */
export function getFiles() {
  return items.filter((i) => i.kind === 'file').map((i) => ({ name: i.name, text: i.text }));
}

export function hasAttachments() {
  return items.length > 0;
}

export function clearAttachments() {
  items = [];
  render();
}

export function describeAttachments() {
  return `附件：图片 ${countOf('image')}/${imageLimits.maxCount}、` +
    `文件 ${countOf('file')}/${fileLimits.maxCount}（${Math.round(totalFileBytes() / 1024)} KB）`;
}

/**
 * 绑定两个入口。
 *
 * composer 上监听 paste；整个输入区监听拖放。
 * 拖放绑在输入区而非仅输入框，是为了让拖到附件区也能落下。
 */
export function initAttachments(noticeHandler) {
  onNotice = noticeHandler;
  void loadLimits();

  const composer = document.getElementById('composer');
  const zone = document.querySelector('.chat-input');

  composer?.addEventListener('paste', (event) => {
    const files = Array.from(event.clipboardData?.files ?? []);
    // 剪贴板里同时有文字和文件时，只有文件存在才拦截默认行为，
    // 否则会把正常的文字粘贴也吃掉。
    if (files.length === 0) { return; }

    event.preventDefault();
    void addFiles(files);
  });

  if (zone) {
    // 必须同时阻止 dragover 的默认行为，否则 drop 不会触发。
    zone.addEventListener('dragover', (event) => {
      if (!event.dataTransfer?.types?.includes('Files')) { return; }
      event.preventDefault();
      zone.classList.add('is-dragover');
    });

    zone.addEventListener('dragleave', (event) => {
      // 只在真正离开区域时移除高亮，子元素间移动会连续触发 dragleave。
      if (event.target === zone) {
        zone.classList.remove('is-dragover');
      }
    });

    zone.addEventListener('drop', (event) => {
      const files = Array.from(event.dataTransfer?.files ?? []);
      if (files.length === 0) { return; }

      event.preventDefault();
      zone.classList.remove('is-dragover');
      void addFiles(files);
    });
  }
}
