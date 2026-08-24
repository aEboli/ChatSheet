// 附件分流的回归测试。
//
// 图片按钮去掉后，粘贴与拖入成了唯一入口，这段分流逻辑也就成了单点：
// 判错类型的后果是「拖进去没反应」或「拖进去变成乱码」，而面板里看不到
// 控制台，这类失败只能靠测试挡住。
//
// 重点覆盖三件事：
//   一、图片按 MIME 判断，文本按扩展名判断。混用会出错——Windows 把 .csv
//       报成 application/vnd.ms-excel、把 .md 报成空串，只看 MIME 会把
//       CSV 当成不支持的格式拒掉。
//   二、各条上限都能拦住，且拦的时候说得出原因。
//   三、二进制格式给的是「换个做法」而不是干巴巴的「不支持」。
//
// 运行：node tests/web/attachments.test.mjs

let notices = [];

globalThis.window = {
  chrome: {
    webview: {
      addEventListener: () => {},
      // image.limits / file.limits 的响应不会回来，模块会退回内置默认值。
      // 测试针对的是默认值，因此这样正合适。
      postMessage: () => {},
    },
  },
  innerWidth: 420,
};

function makeNode(tag = 'div') {
  const node = {
    tag,
    className: '',
    textContent: '',
    innerHTML: '',
    title: '',
    src: '',
    alt: '',
    type: '',
    hidden: false,
    children: [],
    listeners: new Map(),
    classes: new Set(),
    append: (...kids) => node.children.push(...kids),
    replaceChildren: (...kids) => { node.children = [...kids]; },
    setAttribute: () => {},
    addEventListener: (kind, handler) => node.listeners.set(kind, handler),
    classList: {
      add: (name) => node.classes.add(name),
      remove: (name) => node.classes.delete(name),
      contains: (name) => node.classes.has(name),
      toggle: (name, on) => (on ? node.classes.add(name) : node.classes.delete(name)),
    },
  };
  return node;
}

const box = makeNode();
const composer = makeNode('textarea');
const zone = makeNode();

globalThis.document = {
  getElementById: (id) => (id === 'attachments' ? box : id === 'composer' ? composer : null),
  querySelector: (selector) => (selector === '.chat-input' ? zone : null),
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: (tag) => makeNode(tag),
};

/**
 * 最小 FileReader：模块用 readAsDataURL 与 readAsArrayBuffer 两条路。
 *
 * 文件走字节而非文本，因为编码识别必须先拿到原始字节——这正是 GBK 那个
 * 缺陷的修法。因此存根也得给字节。
 */
globalThis.FileReader = class {
  readAsDataURL(blob) {
    this.result = `data:${blob.type};base64,ZmFrZQ==`;
    this.onload?.();
  }

  readAsArrayBuffer(blob) {
    const bytes = blob._bytes;
    this.result = bytes.buffer.slice(bytes.byteOffset, bytes.byteOffset + bytes.byteLength);
    this.onload?.();
  }
};

/** 最小 File：内容以字节保存，size 是原始字节数（与真实 File 一致）。 */
function makeFile(name, type, content) {
  const bytes = typeof content === 'string'
    ? new Uint8Array(Buffer.from(content, 'utf8'))
    : new Uint8Array(content ?? []);
  return { name, type, size: bytes.byteLength, _bytes: bytes };
}

/**
 * 真实的 GBK 字节序列，由 Python 的 gbk 编解码器生成后核对：
 *   '名称,数量\n铅笔,10' → 17 字节
 * 不手写猜测值——写错了测试就变成在验证一个错误的前提。
 */
const GBK_CSV = [
  0xc3, 0xfb, 0xb3, 0xc6, 0x2c, 0xca, 0xfd, 0xc1, 0xbf, 0x0a,
  0xc7, 0xa6, 0xb1, 0xca, 0x2c, 0x31, 0x30,
];

/** 同一段文字的 UTF-8 编码，23 字节——比 GBK 大 6 字节。 */
const UTF8_CSV = [
  0xe5, 0x90, 0x8d, 0xe7, 0xa7, 0xb0, 0x2c, 0xe6, 0x95, 0xb0, 0xe9, 0x87,
  0x8f, 0x0a, 0xe9, 0x93, 0x85, 0xe7, 0xac, 0x94, 0x2c, 0x31, 0x30,
];

/** 带 BOM 的 UTF-16LE，24 字节。Excel 另存「Unicode 文本」就是这个。 */
const UTF16LE_CSV = [
  0xff, 0xfe, 0x0d, 0x54, 0xf0, 0x79, 0x2c, 0x00, 0x70, 0x65, 0xcf, 0x91,
  0x0a, 0x00, 0xc5, 0x94, 0x14, 0x7b, 0x2c, 0x00, 0x31, 0x00, 0x30, 0x00,
];

const { readFileSync } = await import('node:fs');

const {
  initAttachments,
  getImages,
  getFiles,
  hasAttachments,
  clearAttachments,
  describeAttachments,
  createFileGlyph,
} = await import('../../src/web/scripts/attachments.js');

let passed = 0;
let failed = 0;

function check(label, condition, detail = '') {
  if (condition) {
    passed += 1;
    console.log(`  通过  ${label}`);
    return;
  }
  failed += 1;
  console.log(`  失败  ${label}${detail ? `：${detail}` : ''}`);
}

initAttachments((message) => notices.push(message));

/** 走 drop 事件投入一批文件，与真实路径一致。 */
async function drop(...files) {
  notices = [];
  await zone.listeners.get('drop')?.({
    dataTransfer: { files },
    preventDefault: () => {},
  });
  // addFiles 内部有 await，事件处理函数不返回 Promise，让微任务先跑完。
  await new Promise((resolve) => setImmediate(resolve));
}

const lastNotice = () => notices[notices.length - 1] ?? '';

console.log('检查附件分流：');

check('drop 已被绑定', typeof zone.listeners.get('drop') === 'function');
check('paste 已被绑定', typeof composer.listeners.get('paste') === 'function');

// 一、图片走图片路径。
await drop(makeFile('shot.png', 'image/png', 'binary-ish'));
check('PNG 进图片列表', getImages().length === 1 && getFiles().length === 0, describeAttachments());
check('图片带 dataUrl', getImages()[0].dataUrl.startsWith('data:image/png;base64,'), '');

// 二、文本文件走文件路径，且不因 MIME 古怪而被拒。
// Windows 把 .csv 报成 application/vnd.ms-excel——这是真实会遇到的值。
await drop(makeFile('data.csv', 'application/vnd.ms-excel', '名称,数量\n铅笔,10'));
check('CSV 进文件列表', getFiles().length === 1, describeAttachments());
check('CSV 未被 MIME 误判拒绝', notices.length === 0, lastNotice());
check('文件带解码后的文本', getFiles()[0].text.includes('铅笔'), getFiles()[0].text);

// .md 的 MIME 常是空串，同样要按扩展名放行。
await drop(makeFile('note.md', '', '# 标题'));
check('空 MIME 的 md 按扩展名放行', getFiles().length === 2, describeAttachments());

// 三、二进制格式要给出替代做法，而不只是「不支持」。
await drop(makeFile('book.xlsx', 'application/vnd.openxmlformats', 'zip-bytes'));
check('xlsx 被拒绝', getFiles().length === 2, describeAttachments());
check(
  'xlsx 的说明指向「直接在 Excel 里打开」',
  lastNotice().includes('直接在 Excel 里打开'),
  lastNotice(),
);

await drop(makeFile('scan.pdf', 'application/pdf', '%PDF-1.4'));
check('pdf 的说明指向「复制文字粘贴」', lastNotice().includes('复制其中的文字'), lastNotice());

// 四、不支持的图片格式按图片报错，而不是掉进文本路径。
await drop(makeFile('old.bmp', 'image/bmp', 'bmp-bytes'));
check('BMP 被拒绝', getImages().length === 1, describeAttachments());
check('BMP 按图片格式报错', lastNotice().includes('PNG、JPEG 或 WebP'), lastNotice());

// 五、改了扩展名的二进制文件靠内容识破。
await drop(makeFile('fake.txt', 'text/plain', 'abc\u0000def'));
check('含 NUL 的文件被拒绝', getFiles().length === 2, describeAttachments());
check('说明指出是二进制', lastNotice().includes('二进制'), lastNotice());

// 六、BOM 要去掉：留着会成为正文第一个字符，在 JSON 里直接导致解析失败。
await drop(makeFile('bom.json', 'application/json', '\ufeff{"k":1}'));
check('BOM 已去掉', getFiles()[2]?.text.startsWith('{'), JSON.stringify(getFiles()[2]?.text));

// 七、未知扩展名拒绝，并列出可用类型。
await drop(makeFile('mystery.dat', '', 'whatever'));
check('未知扩展名被拒绝', getFiles().length === 3, describeAttachments());
check('说明列出了可附带的类型', lastNotice().includes('.txt'), lastNotice());

// 八、文件数量上限。默认 4 个，已有 3 个，再投 2 个只该收下 1 个。
await drop(makeFile('e.txt', 'text/plain', 'e'), makeFile('f.txt', 'text/plain', 'f'));
check('文件数量上限拦住多余的', getFiles().length === 4, describeAttachments());
check('说明给出数量上限', lastNotice().includes('最多附带 4 个文件'), lastNotice());

// 九、单文件大小上限。默认 64 KiB。
clearAttachments();
check('清空后没有附件', !hasAttachments() && getImages().length === 0 && getFiles().length === 0);

await drop(makeFile('big.txt', 'text/plain', 'a'.repeat(64 * 1024 + 1)));
check('超过单文件上限被拒绝', getFiles().length === 0, describeAttachments());
check('说明给出单文件上限', lastNotice().includes('64 KB'), lastNotice());

// 十、合计上限。默认 128 KiB，三个 50 KiB 的文件只该收下两个。
await drop(
  makeFile('a.txt', 'text/plain', 'a'.repeat(50 * 1024)),
  makeFile('b.txt', 'text/plain', 'b'.repeat(50 * 1024)),
  makeFile('c.txt', 'text/plain', 'c'.repeat(50 * 1024)),
);
check('合计上限拦住第三个', getFiles().length === 2, describeAttachments());
check('说明给出合计上限', lastNotice().includes('128 KB'), lastNotice());

// 十一、图片与文件各自计数，互不挤占。
clearAttachments();
await drop(
  makeFile('1.png', 'image/png', 'x'),
  makeFile('1.csv', 'text/csv', 'a,b'),
  makeFile('2.png', 'image/jpeg', 'x'),
);
check('图片与文件分别计数', getImages().length === 2 && getFiles().length === 1, describeAttachments());
check(
  '布局自检的说明同时报两类',
  describeAttachments().includes('图片 2/6') && describeAttachments().includes('文件 1/4'),
  describeAttachments(),
);

// 十二、编码识别。这是本组最重要的一段。
//
// 缺陷现场：初版固定 `readAsText(blob, 'utf-8')`。中文 Windows 上 Excel 导出
// CSV 默认是 GBK，按 UTF-8 宽松解码不报错，只把汉字变成替换字符——
// 文件条显示一切正常，模型收到的却是「����,����」，全程无任何提示。
clearAttachments();

await drop(makeFile('gbk.csv', 'application/vnd.ms-excel', GBK_CSV));
check('GBK 文件被收下', getFiles().length === 1, describeAttachments());
check(
  'GBK 汉字正确解出，不是替换字符',
  getFiles()[0]?.text === '名称,数量\n铅笔,10',
  JSON.stringify(getFiles()[0]?.text),
);
check('GBK 文件不含替换字符', !getFiles()[0]?.text.includes('�'), '');

// UTF-8 不能被误判成 GBK。顺序反了就会出现这个错——GB18030 解 UTF-8
// 不报错，只解出乱码。
clearAttachments();
await drop(makeFile('utf8.csv', 'text/csv', UTF8_CSV));
check(
  'UTF-8 文件仍按 UTF-8 解（未被 GBK 抢走）',
  getFiles()[0]?.text === '名称,数量\n铅笔,10',
  JSON.stringify(getFiles()[0]?.text),
);

// 带 BOM 的 UTF-16LE：Excel 另存「Unicode 文本」就是这个格式。
// 它半数字节是 0x00，绝不能被 NUL 检查当成二进制拒掉。
clearAttachments();
await drop(makeFile('u16.csv', 'text/csv', UTF16LE_CSV));
check('UTF-16LE 未被当成二进制拒绝', getFiles().length === 1, lastNotice());
check(
  'UTF-16LE 正确解码',
  getFiles()[0]?.text === '名称,数量\n铅笔,10',
  JSON.stringify(getFiles()[0]?.text),
);
check('UTF-16 的 BOM 已去掉', !getFiles()[0]?.text.startsWith('﻿'), '');

// 编码要记在条目上，供界面标注——猜测必须可见。
clearAttachments();
await drop(
  makeFile('a.csv', 'text/csv', GBK_CSV),
  makeFile('b.csv', 'text/csv', UTF8_CSV),
);
check('识别出的编码记在条目上', describeAttachments().includes('文件 2/4'), describeAttachments());

// 识别不出的编码要明确拒绝，而不是塞一段乱码进上下文。
// Shift_JIS 的日文字节：既非合法 UTF-8，用 GB18030 解则是另一批汉字。
clearAttachments();
await drop(makeFile('sjis.txt', 'text/plain', [0x82, 0xa0, 0x82, 0xa2, 0x83, 0x41]));
const sjisAccepted = getFiles().length === 1;
check(
  'Shift_JIS 要么被拒绝、要么不含替换字符（不静默输出乱码）',
  !sjisAccepted || !getFiles()[0].text.includes('�'),
  sjisAccepted ? `被收下：${JSON.stringify(getFiles()[0].text)}` : `被拒绝：${lastNotice()}`,
);

// 上限按 UTF-8 字节算，不按原始文件大小。
//
// GBK 汉字 2 字节、UTF-8 3 字节。构造一个原始 44 KB 的 GBK 文件，
// 解出来约 66 KB —— 原始大小在 64 KB 上限内，UTF-8 字节数却超了。
// 若按原始大小放行，加载项会在发送时退回，表现为「拖进来没事、一发就报错」。
clearAttachments();
const gbkHan = [];
for (let i = 0; i < 22 * 1024; i++) { gbkHan.push(0xc3, 0xfb); }  // 44 KiB
await drop(makeFile('big-gbk.csv', 'text/csv', gbkHan));
check(
  '按 UTF-8 字节判超限（GBK 原文 44 KB 未放行）',
  getFiles().length === 0,
  describeAttachments(),
);
check(
  '超限说明指出是转 UTF-8 后变大',
  lastNotice().includes('转 UTF-8 后变大'),
  lastNotice(),
);

// 反向：原始 40 KB 的 UTF-8 纯 ASCII，解出来还是 40 KB，应当放行。
clearAttachments();
await drop(makeFile('ok.txt', 'text/plain', 'a'.repeat(40 * 1024)));
check('UTF-8 未超限的正常放行', getFiles().length === 1, lastNotice());
check('记录的是 UTF-8 字节数', describeAttachments().includes('40 KB'), describeAttachments());

// 十三、渲染出的附件条必须带图标，且小文件不显示成「0 KB」。
//
// 这两条都是真实路径核对时才暴露的：手写 HTML 去看样式，图标是自己写上去的，
// 证明不了代码会画它；而 23 字节的文件按 KB 取整就成了「0 KB」，看着像读失败。
clearAttachments();
await drop(makeFile('tiny.csv', 'text/csv', GBK_CSV));

const card = box.children.find((c) => c.className === 'attachment-file');
check('渲染出文件条', card !== undefined, JSON.stringify(box.children.map((c) => c.className)));
check(
  '文件条带图标容器',
  card?.children.some((c) => c.className === 'attachment-file-glyph'),
  JSON.stringify(card?.children.map((c) => c.className)),
);
check(
  '图标容器里确实写入了 SVG',
  card?.children.find((c) => c.className === 'attachment-file-glyph')?.innerHTML.includes('<svg'),
  card?.children.find((c) => c.className === 'attachment-file-glyph')?.innerHTML ?? '（空）',
);
check(
  '非 UTF-8 编码在条上标出',
  card?.children.some((c) => c.className === 'attachment-file-encoding'),
  JSON.stringify(card?.children.map((c) => c.className)),
);
check(
  '小文件按字节显示而非 0 KB',
  card?.children.find((c) => c.className === 'attachment-file-size')?.textContent === '23 B',
  card?.children.find((c) => c.className === 'attachment-file-size')?.textContent ?? '（无）',
);
check('悬停说明含编码', (card?.title ?? '').includes('编码：GBK'), card?.title ?? '');

// 气泡那处必须用同一个工厂，不能各画一份——否则改一处另一处会漏。
check(
  'chat.js 用共享的 createFileGlyph 而非自绘',
  readFileSync(new URL('../../src/web/scripts/chat.js', import.meta.url), 'utf8')
    .includes("createFileGlyph('file-glyph')"),
  '',
);
check(
  'createFileGlyph 已导出',
  typeof createFileGlyph === 'function',
  typeof createFileGlyph,
);

// 十四、载荷形状：加载项按 name/dataUrl 与 name/text 两组字段读取，
// 多带或少带字段都会在那边解析出空值。
check(
  '图片载荷只含 name 与 dataUrl',
  getImages().every((i) => Object.keys(i).sort().join(',') === 'dataUrl,name'),
  JSON.stringify(Object.keys(getImages()[0] ?? {})),
);
check(
  '文件载荷只含 name 与 text',
  getFiles().every((f) => Object.keys(f).sort().join(',') === 'name,text'),
  JSON.stringify(Object.keys(getFiles()[0] ?? {})),
);

// 十三、粘贴入口与拖入走同一条分流。
notices = [];
composer.listeners.get('paste')?.({
  clipboardData: { files: [makeFile('pasted.png', 'image/png', 'x')] },
  preventDefault: () => {},
});
await new Promise((resolve) => setImmediate(resolve));
// 前面的编码测试清过附件，此刻只有那个 40 KB 的 ok.txt，图片数为 0。
check('粘贴的图片同样收下', getImages().length === 1, describeAttachments());

// 剪贴板里只有文字时不能拦默认行为，否则正常的文字粘贴会被吃掉。
let prevented = 0;
composer.listeners.get('paste')?.({
  clipboardData: { files: [] },
  preventDefault: () => { prevented += 1; },
});
check('纯文字粘贴不被拦截', prevented === 0, `preventDefault 被调用 ${prevented} 次`);

console.log('');
console.log(`=== 附件分流：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
