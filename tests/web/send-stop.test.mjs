// 发送按钮的双态回归测试。
//
// 缺陷现场（本次改动前）：发送与停止是两个按钮，运行中把发送禁用、把停止显示出来。
// 合并成一个按钮后若照旧禁用，运行中就完全没有中断入口了——而中断恰恰只在运行中
// 才有意义。这里锁住合并后必须成立的三件事：
//
//   一、空闲时点击发送，运行中点击停止，同一个按钮两种含义；
//   二、运行中按钮不能被禁用，否则点不动；
//   三、状态变化要同时反映在 class（决定显示哪层图形）与 title/aria-label
//       （图标没有文字标签，悬停说明是它唯一的自解释途径）。
//
// 运行：node tests/web/send-stop.test.mjs

const posted = [];

globalThis.window = {
  chrome: {
    webview: {
      addEventListener: () => {},
      // 响应一律不回来：chat.send 就此挂着，正好模拟「正在处理」这个状态。
      postMessage: (message) => posted.push(message),
    },
  },
  innerWidth: 420,
  location: { hash: '' },
};

function makeNode(tag = 'div') {
  const node = {
    tag,
    className: '',
    textContent: '',
    innerHTML: '',
    title: '',
    value: '',
    type: '',
    disabled: false,
    hidden: false,
    open: false,
    scrollTop: 0,
    scrollHeight: 40,
    clientHeight: 17,
    style: {},
    dataset: {},
    attributes: {},
    children: [],
    listeners: new Map(),
    classes: new Set(),
    append: (...kids) => node.children.push(...kids),
    remove: () => {},
    replaceChildren: (...kids) => { node.children = [...kids]; },
    setAttribute: (name, value) => { node.attributes[name] = value; },
    getAttribute: (name) => node.attributes[name],
    focus: () => {},
    querySelector: () => null,
    querySelectorAll: () => [],
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

// 未列出的 id 也返回节点：chat.js 与 picker.js 会取一批控件，
// 逐个列出对本测试没有意义，缺一个就抛异常反而掩盖了要验的东西。
const nodes = new Map();
function nodeFor(id) {
  if (!nodes.has(id)) { nodes.set(id, makeNode()); }
  return nodes.get(id);
}

globalThis.document = {
  getElementById: (id) => nodeFor(id),
  querySelector: () => makeNode(),
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: (tag) => makeNode(tag),
};

globalThis.getComputedStyle = () => ({ lineHeight: '17' });

const { initChat } = await import('../../src/web/scripts/chat.js');

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

initChat();

const send = nodeFor('send');
const composer = nodeFor('composer');
const click = () => send.listeners.get('click')?.({});
const sent = () => posted.filter((m) => m.channel === 'chat.send');
const stops = () => posted.filter((m) => m.channel === 'chat.stop');

console.log('检查发送与停止的双态：');

check('发送按钮已绑定点击', typeof send.listeners.get('click') === 'function');
check('初始不是忙态', !send.classes.has('is-busy'));
check('初始 title 说的是发送', send.title.includes('发送'), send.title);

// 空文本且无附件时不该发出去：否则会拿一条空消息去占一轮。
click();
await new Promise((resolve) => setImmediate(resolve));
check('空输入不发送', sent().length === 0, JSON.stringify(sent()));

// 一、空闲态点击 → 发送。
composer.value = '把 A 列排一下';
click();
await new Promise((resolve) => setImmediate(resolve));

check('有内容时点击会发送', sent().length === 1, JSON.stringify(sent()));
check('发出的文本已去掉首尾空白', sent()[0]?.payload.text === '把 A 列排一下', JSON.stringify(sent()[0]?.payload));
check(
  '载荷同时带图片与文件两个字段',
  Array.isArray(sent()[0]?.payload.images) && Array.isArray(sent()[0]?.payload.files),
  JSON.stringify(Object.keys(sent()[0]?.payload ?? {})),
);
check('发送后清空输入框', composer.value === '', composer.value);

// 二、进入忙态后的外观与可点性。
check('忙态加上 is-busy', send.classes.has('is-busy'));
check('忙态按钮不被禁用', send.disabled === false, `disabled=${send.disabled}`);
check('忙态 title 说的是停止', send.title.includes('停止'), send.title);
check('忙态 aria-label 为停止', send.getAttribute('aria-label') === '停止', send.getAttribute('aria-label'));
check('忙态输入框禁用', composer.disabled === true);

// 三、忙态点击 → 停止，且不会再发一轮。
composer.value = '又输入了一句';
click();
await new Promise((resolve) => setImmediate(resolve));

check('忙态点击发出停止', stops().length === 1, JSON.stringify(stops()));
check('忙态点击不再发送', sent().length === 1, JSON.stringify(sent()));
check('忙态点击不清空输入框', composer.value === '又输入了一句', composer.value);

// 连点停止只是重复请求，不该变成发送。
click();
await new Promise((resolve) => setImmediate(resolve));
check('连点仍是停止', stops().length === 2 && sent().length === 1, `停止 ${stops().length} 发送 ${sent().length}`);

console.log('');
console.log(`=== 发送与停止：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
