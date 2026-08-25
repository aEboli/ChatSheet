// 发送按钮的三态回归测试。
//
// 缺陷现场（合并按钮时）：发送与停止原是两个按钮，运行中把发送禁用、把停止显示出来。
// 合并成一个按钮后若照旧禁用，运行中就完全没有中断入口了——而中断恰恰只在运行中
// 才有意义。
//
// 加入输入排队后，这个按钮承担三种含义，本文件锁住它们各自的触发条件：
//
//   空闲            → 发送
//   处理中 + 有输入  → 加入队列（上一轮结束后自动接着跑）
//   处理中 + 输入为空 → 停止
//
// 以及三条不能被改坏的前提：
//   一、按钮在任何状态下都不能被禁用，否则点不动；
//   二、输入框在处理中也不能被禁用——处理中写下一步是常态，内容进队列；
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

/*
  假 DOM。三处刻意做实而不是留空：

  - className 与 classList 共用同一个集合。被测代码两种写法都用
    （新建节点写 className，改状态用 classList），两者若各记一份，
    断言就要猜某个类名是用哪种方式加的。
  - remove() 真的把自己从父节点摘掉。排队条是整条重画的，
    remove 若是空操作，已移除的节点仍留在 children 里，
    「取消后队列为空」这类断言就永远为真，测不出东西。
  - append 记住父节点，remove 才有得摘。
*/
function makeNode(tag = 'div') {
  const node = {
    tag,
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
    parent: null,
    listeners: new Map(),
    classes: new Set(),
    append: (...kids) => {
      for (const kid of kids) {
        if (kid && typeof kid === 'object') { kid.parent = node; }
        node.children.push(kid);
      }
    },
    remove: () => {
      const parent = node.parent;
      if (!parent) { return; }
      parent.children = parent.children.filter((n) => n !== node);
      node.parent = null;
    },
    replaceChildren: (...kids) => {
      for (const kid of node.children) {
        if (kid && typeof kid === 'object') { kid.parent = null; }
      }
      node.children = [];
      node.append(...kids);
    },
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

  Object.defineProperty(node, 'className', {
    get: () => [...node.classes].join(' '),
    set: (value) => {
      node.classes.clear();
      for (const name of String(value).split(/\s+/).filter(Boolean)) {
        node.classes.add(name);
      }
    },
  });

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
const transcript = nodeFor('transcript');
const strip = nodeFor('queue-strip');
const click = () => send.listeners.get('click')?.({});
const sent = () => posted.filter((m) => m.channel === 'chat.send');
const stops = () => posted.filter((m) => m.channel === 'chat.stop');
const tick = () => new Promise((resolve) => setImmediate(resolve));

/**
 * 通知输入框内容已变化。
 *
 * 按钮含义取决于输入框是否有内容，而真实浏览器里这由 input 事件驱动。
 * 直接改 value 不会触发，必须显式派发，否则测的就不是用户走的那条路。
 */
const notifyInput = () => composer.listeners.get('input')?.({});

/** 对话流里带某个类名的气泡。从 DOM 读才算用户看到的。 */
const bubblesWith = (name) => transcript.children.filter((n) => n.classes?.has(name));

/** 对话流里各条用户气泡的正文。取消掉的排队输入不该出现在这里面。 */
const bubbleTexts = () =>
  bubblesWith('msg-user').map((n) => n.children[0]?.children[0]?.textContent ?? '');

/** 排队条上的条目。排队内容显示在这里，不进对话流。 */
const chips = () => strip.children.filter((n) => n.classes?.has('queue-chip'));
const chipText = (chip) =>
  chip.children.find((n) => n.classes?.has('queue-chip-text'))?.textContent ?? '';

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
check('忙态输入框仍可输入', composer.disabled === false, `disabled=${composer.disabled}`);
check('输入框为空时 title 说的是停止', send.title.includes('停止'), send.title);
check('输入框为空时 aria-label 为停止', send.getAttribute('aria-label') === '停止', send.getAttribute('aria-label'));

// 三、忙态 + 有输入 → 含义转为「加入队列」。
// 输入框有字说明用户正打算安排下一步，此刻点按钮几乎不可能是想中断。
composer.value = '再把 B 列也排一下';
notifyInput();

check('有输入后 aria-label 变为加入队列', send.getAttribute('aria-label') === '加入队列', send.getAttribute('aria-label'));
check('有输入后 title 说明会入队', send.title.includes('队列') || send.title.includes('排到'), send.title);
check('有输入后加上 is-queueing（图形换回箭头）', send.classes.has('is-queueing'));

click();
await tick();

check('忙态点击不发出停止', stops().length === 0, JSON.stringify(stops()));
check('忙态点击不并发第二轮 chat.send', sent().length === 1, JSON.stringify(sent()));
check('入队后清空输入框', composer.value === '', composer.value);

// 排队内容显示在排队条上，不进对话流：对话流记录已经发生的事，
// 混进去会被当成已经处理过，而且对话一长就被顶出可视区。
check('排队内容出现在排队条上', chips().length === 1, `排队条 ${chips().length} 条`);
check('排队条已显示', strip.hidden === false, `hidden=${strip.hidden}`);
check('排队内容尚未进对话流', bubblesWith('msg-user').length === 1,
  `用户气泡 ${bubblesWith('msg-user').length} 个`);
check('排队条上写的是刚提交的那句', chipText(chips()[0]) === '再把 B 列也排一下',
  chipText(chips()[0]));

// 队列里再排一条，位次应当累加而不是互相覆盖。
composer.value = '顺便加一列毛利率';
notifyInput();
click();
await tick();

check('第二条也进队列', chips().length === 2, `排队条 ${chips().length} 条`);
check('两条按提交顺序排列',
  chips().map(chipText).join('|') === '再把 B 列也排一下|顺便加一列毛利率',
  chips().map(chipText).join('|'));
check('排队期间仍未并发发送', sent().length === 1, JSON.stringify(sent()));

// 四、清空输入框后按钮回到「停止」，这是排队态下唯一的中断入口。
notifyInput();
check('清空输入后 aria-label 回到停止', send.getAttribute('aria-label') === '停止', send.getAttribute('aria-label'));
check('清空输入后去掉 is-queueing', !send.classes.has('is-queueing'));

click();
await tick();

check('输入框为空时点击发出停止', stops().length === 1, JSON.stringify(stops()));
check('停止连带清空队列（排队条已空）', chips().length === 0, `排队条 ${chips().length} 条`);
check('队列空后排队条收起', strip.hidden === true, `hidden=${strip.hidden}`);
// 被取消的两条从未发出，对话流里不该留下它们——连划掉的气泡也不留。
// 此刻对话流里只该有最初真正发出去的那一条用户气泡。
check('对话流里仍只有真正发出去的那一条', bubbleTexts().length === 1,
  bubbleTexts().join('|'));
check('被取消的原文不出现在对话流里',
  !bubbleTexts().some((t) => ['再把 B 列也排一下', '顺便加一列毛利率'].includes(t)),
  bubbleTexts().join('|'));

// 连点停止只是重复请求，不该变成发送。
click();
await tick();
check('连点仍是停止', stops().length === 2 && sent().length === 1, `停止 ${stops().length} 发送 ${sent().length}`);

console.log('');
console.log(`=== 发送与停止：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
