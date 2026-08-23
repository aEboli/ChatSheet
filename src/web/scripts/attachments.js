import { request, logToHost } from './bridge.js';

// 图片附件管理。三种入口：粘贴、拖入、文件选择。
//
// 粘贴是主要入口——从 Excel 截个图直接 Ctrl+V 问「这块怎么算」，
// 比存成文件再选择快得多。

let limits = {
  maxCount: 6,
  maxBytes: 5 * 1024 * 1024,
  mediaTypes: ['image/png', 'image/jpeg', 'image/webp'],
};

/** 当前待发送的附件。发送后由 clearAttachments 清空。 */
let items = [];
let nextId = 0;
let onNotice = null;

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
    if (result?.maxCount) { limits = result; }
  } catch {
    // 取不到就用内置默认值，后端仍会做最终校验。
  }
}

function formatSize(bytes) {
  return bytes >= 1024 * 1024
    ? `${(bytes / 1024 / 1024).toFixed(1)} MB`
    : `${Math.round(bytes / 1024)} KB`;
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

/**
 * 添加一批文件。
 * 逐个校验并给出具体原因，不合规的跳过但明确告知是哪一张为什么。
 */
async function addFiles(files) {
  const accepted = [];

  for (const file of files) {
    if (!file) { continue; }

    if (!limits.mediaTypes.includes(file.type)) {
      notify(`${file.name || '图片'} 的格式 ${file.type || '未知'} 不受支持，请用 PNG、JPEG 或 WebP。`);
      continue;
    }

    if (file.size > limits.maxBytes) {
      notify(`${file.name || '图片'} 为 ${formatSize(file.size)}，超过 ${formatSize(limits.maxBytes)} 上限。`);
      continue;
    }

    if (items.length + accepted.length >= limits.maxCount) {
      notify(`一次最多附带 ${limits.maxCount} 张图片，其余已忽略。`);
      break;
    }

    try {
      const dataUrl = await readAsDataUrl(file);
      accepted.push({
        id: `img${nextId++}`,
        // 粘贴来的 Blob 没有文件名，给个可辨识的默认值。
        name: file.name || `粘贴的图片-${nextId}.png`,
        type: file.type,
        size: file.size,
        dataUrl,
      });
    } catch (error) {
      notify(`${file.name || '图片'} 读取失败：${error.message}`);
    }
  }

  if (accepted.length > 0) {
    items = items.concat(accepted);
    render();
    void logToHost(`已添加 ${accepted.length} 张图片，当前共 ${items.length} 张`);
  }
}

function removeItem(id) {
  items = items.filter((i) => i.id !== id);
  render();
}

function render() {
  const box = container();
  if (!box) { return; }

  box.replaceChildren();
  box.hidden = items.length === 0;

  if (items.length === 0) { return; }

  for (const item of items) {
    const card = document.createElement('div');
    card.className = 'attachment';

    const thumb = document.createElement('img');
    thumb.className = 'attachment-thumb';
    thumb.src = item.dataUrl;
    thumb.alt = item.name;

    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'attachment-remove';
    remove.textContent = '×';
    remove.title = `移除 ${item.name}`;
    remove.setAttribute('aria-label', `移除 ${item.name}`);
    remove.addEventListener('click', () => removeItem(item.id));

    card.title = `${item.name}（${formatSize(item.size)}）`;
    card.append(thumb, remove);
    box.append(card);
  }

  const summary = document.createElement('span');
  summary.className = 'attachment-summary';
  summary.textContent = `${items.length}/${limits.maxCount} 张`;
  box.append(summary);
}

/** 取出待发送的附件载荷。 */
export function getAttachments() {
  return items.map((i) => ({ name: i.name, dataUrl: i.dataUrl }));
}

export function hasAttachments() {
  return items.length > 0;
}

export function clearAttachments() {
  items = [];
  render();
}

export function describeAttachments() {
  return `附件：${items.length} 张（上限 ${limits.maxCount}）`;
}

/**
 * 绑定三种入口。
 *
 * composer 上监听 paste；整个输入区监听拖放；按钮触发文件选择。
 * 拖放绑在输入区而非仅输入框，是为了让拖到附件区也能落下。
 */
export function initAttachments(noticeHandler) {
  onNotice = noticeHandler;
  void loadLimits();

  const composer = document.getElementById('composer');
  const zone = document.querySelector('.chat-input');
  const input = document.getElementById('image-input');

  composer?.addEventListener('paste', (event) => {
    const files = Array.from(event.clipboardData?.files ?? []);
    // 剪贴板里同时有文字和图片时，只有图片存在才拦截默认行为，
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

  document.getElementById('attach')?.addEventListener('click', () => input?.click());

  input?.addEventListener('change', () => {
    const files = Array.from(input.files ?? []);
    void addFiles(files);
    // 清空以便再次选择同一文件时仍能触发 change。
    input.value = '';
  });
}
