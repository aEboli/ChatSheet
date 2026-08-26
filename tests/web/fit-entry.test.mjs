// 面板「适配」的入口：点图标就按当前对齐适配，浮层只用来换对齐方式。
//
// 为什么要锁住这件事：原先点图标只是展开浮层，必须再点一次「居中 / 靠左 / 靠右」
// 才真正动手。默认对齐因此形同不存在——按默认排一次表要点两下，连续排几张表
// 就是连续的两下，而按钮上写的分明是「适配」。
//
// 断言：
//   一、点图标就发出适配请求，用的是当前记住的对齐，默认居中；
//   二、点图标不再只是开关浮层：点完浮层是收起的，不留一张待选菜单；
//   三、在浮层里选过之后再点图标沿用那一种，不是又退回居中；
//   四、方向键展开浮层且不适配——点击被适配占用了，键盘要另有入口换对齐；
//   五、悬停展开着的浮层，点图标照样适配并收起浮层；
//   六、按钮的悬停说明与无障碍名写出当前对齐：点下去按哪种排版，看图标是看不出的。
//
// 运行：node tests/web/fit-entry.test.mjs

const posted = [];
let hostHandler = null;
let focused = null;

globalThis.window = {
  chrome: {
    webview: {
      addEventListener: (kind, handler) => {
        if (kind === 'message') { hostHandler = handler; }
      },
      postMessage: (message) => posted.push(message),
    },
  },
  innerWidth: 420,
  location: { hash: '' },
};

/*
  假 DOM。这里必须做实的是三处，否则本文件测不到东西：
  一、querySelector 走子树，操作卡片的填充全靠它；
  二、focus 记下落点，方向键展开后焦点该在当前项上；
  三、按钮的多种事件各存一份，点击与方向键是两条不同的入口。
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
    focus: () => { focused = node; },
    querySelector: (selector) => descendants(node).find((n) => matches(n, selector)) ?? null,
    querySelectorAll: (selector) => descendants(node).filter((n) => matches(n, selector)),
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

/** 子树里的全部节点，不含自身。 */
function descendants(node) {
  const out = [];
  for (const kid of node.children) {
    if (!kid || typeof kid !== 'object') { continue; }
    out.push(kid);
    if (Array.isArray(kid.children)) { out.push(...descendants(kid)); }
  }
  return out;
}

/** 只认类名与标签名，被测代码用的就这两种。 */
function matches(node, selector) {
  for (const part of String(selector).split(',')) {
    const one = part.trim();
    if (one.startsWith('.')) {
      if (node.classes?.has(one.slice(1))) { return true; }
    } else if (one && node.tag === one) {
      return true;
    }
  }
  return false;
}

const nodes = new Map();
function nodeFor(id) {
  if (!nodes.has(id)) { nodes.set(id, makeNode()); }
  return nodes.get(id);
}

// 浮层与三个对齐选项。真实标记里浮层带 hidden，初值要跟着写死——
// 「点图标之后浮层是收起的」这条断言全靠它才有意义。
const fitPop = nodeFor('fit-pop');
fitPop.hidden = true;
for (const align of ['left', 'center', 'right']) {
  const item = makeNode('button');
  item.className = 'fit-item';
  item.dataset.align = align;
  fitPop.append(item);
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

const button = nodeFor('fit');
const wrap = nodeFor('fit-wrap');
const tick = () => new Promise((resolve) => setImmediate(resolve));

/** 点适配图标。禁用期间真实按钮不会触发点击，这里照此处理。 */
const clickButton = () => {
  if (button.disabled) { return false; }
  button.listeners.get('click')?.({});
  return true;
};

/** 在按钮上按键，回报有没有拦下默认行为。 */
const pressKey = (key) => {
  let prevented = false;
  button.listeners.get('keydown')?.({ key, preventDefault: () => { prevented = true; } });
  return prevented;
};

const hover = () => wrap.listeners.get('mouseenter')?.({});
const chooseAlign = (align) =>
  fitPop.children.find((n) => n.dataset.align === align)?.listeners.get('click')?.({});

const fitCalls = () => posted.filter((m) => m.channel === 'sheet.fit');
const lastFit = () => fitCalls()[fitCalls().length - 1];
const activeAlign = () =>
  fitPop.children.find((n) => n.classes.has('is-active'))?.dataset.align ?? '';

/** 回应加载项调用。不回应会让 5 分钟的超时定时器把进程挂住。 */
function reply(channel, data, ok = true) {
  const message = [...posted].reverse().find((m) => m.channel === channel);
  if (!message) { throw new Error(`没有发出 ${channel}`); }
  hostHandler({
    data: { kind: 'response', id: message.id, ok, data, error: ok ? undefined : '宿主拒绝' },
  });
  return message;
}

/** 一次成功的适配结果，撤销标识按序号区分。 */
const okResult = (alignment, seq) => ({
  ok: true,
  undoId: `fit-${seq}`,
  address: '$A$1:$D$6',
  sheet: 'Sheet1',
  rows: 6,
  columns: 4,
  horizontalAlignment: alignment,
});

console.log('检查适配的入口行为：');

// ---- 一、点图标直接适配，默认居中 ----

check('起始说明写出默认对齐是居中', button.title.includes('居中'), button.title);
check('无障碍名也写出当前对齐',
  (button.getAttribute('aria-label') ?? '').includes('居中'),
  button.getAttribute('aria-label'));
check('起始高亮落在居中上', activeAlign() === 'center', activeAlign());

clickButton();
await tick();

check('点图标就发出适配请求', fitCalls().length === 1, `发出 ${fitCalls().length} 次`);
check('用的是当前记住的对齐（默认居中）',
  lastFit()?.payload?.horizontalAlignment === 'center', JSON.stringify(lastFit()?.payload));
check('点图标不再只是展开浮层', fitPop.hidden === true, `hidden=${fitPop.hidden}`);
check('浮层收起时 aria-expanded 为 false',
  button.getAttribute('aria-expanded') === 'false', button.getAttribute('aria-expanded'));

const transcript = nodeFor('transcript');
const cards = () => transcript.children.filter((n) => n.classes?.has('tool-card'));
check('适配落成一张操作卡片', cards().length === 1, `卡片 ${cards().length} 张`);
check('执行期间按钮禁用，避免连点排两次', button.disabled === true, `disabled=${button.disabled}`);

reply('sheet.fit', okResult('center', 1));
await tick();
check('执行完按钮恢复可点', button.disabled === false, `disabled=${button.disabled}`);

// ---- 二、浮层换对齐，之后点图标沿用这一种 ----

chooseAlign('right');
await tick();

check('选浮层里的一项照旧立刻适配', fitCalls().length === 2, `发出 ${fitCalls().length} 次`);
check('这一次按所选的靠右',
  lastFit()?.payload?.horizontalAlignment === 'right', JSON.stringify(lastFit()?.payload));
check('高亮跟着换到靠右', activeAlign() === 'right', activeAlign());
check('按钮说明改写成靠右', button.title.includes('靠右'), button.title);
check('无障碍名改写成靠右',
  (button.getAttribute('aria-label') ?? '').includes('靠右'),
  button.getAttribute('aria-label'));

reply('sheet.fit', okResult('right', 2));
await tick();

clickButton();
await tick();

check('再点图标沿用上次选的对齐', fitCalls().length === 3, `发出 ${fitCalls().length} 次`);
check('沿用的是靠右，不是退回居中',
  lastFit()?.payload?.horizontalAlignment === 'right', JSON.stringify(lastFit()?.payload));

reply('sheet.fit', okResult('right', 3));
await tick();

// ---- 三、方向键展开浮层，且不适配 ----

focused = null;
const prevented = pressKey('ArrowDown');

check('方向键展开浮层', fitPop.hidden === false, `hidden=${fitPop.hidden}`);
check('展开后 aria-expanded 为 true',
  button.getAttribute('aria-expanded') === 'true', button.getAttribute('aria-expanded'));
check('方向键拦下默认滚动', prevented === true, `prevented=${prevented}`);
check('方向键不适配', fitCalls().length === 3, `发出 ${fitCalls().length} 次`);
check('焦点落在当前对齐那一项上', focused?.dataset?.align === 'right',
  focused?.dataset?.align ?? '没有落点');

// 无关按键不该动浮层。这一条防的是把 keydown 写成「按什么都展开」。
fitPop.hidden = true;
pressKey('a');
check('无关按键不展开浮层', fitPop.hidden === true, `hidden=${fitPop.hidden}`);

// ---- 四、悬停展开着的浮层，点图标照样适配并收起 ----

hover();
check('悬停仍然展开浮层', fitPop.hidden === false, `hidden=${fitPop.hidden}`);

clickButton();
await tick();

check('浮层开着时点图标也适配', fitCalls().length === 4, `发出 ${fitCalls().length} 次`);
check('适配后浮层收起，不留待选菜单', fitPop.hidden === true, `hidden=${fitPop.hidden}`);

reply('sheet.fit', okResult('right', 4));
await tick();

console.log('');
console.log(`=== 适配入口行为：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
