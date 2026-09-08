// 功能区「撤销」只撤功能区点出来的操作。
//
// 为什么必须行为测试：这个按钮旁边没有任何上下文说明它将要撤掉什么，
// 一旦它连面板里点的、模型改的一起撤，用户就得到一个盲撤按钮——
// 而那两类在对话流里各有卡片与审批记录，那才是撤它们的地方。
// 静态检查只能看到「代码里写了 data-source」，看不到「挑中的是哪一张卡」。
//
// 断言：
//   一、功能区适配的卡片带来源标记，面板里点的不带；
//   二、状态只数功能区那些卡，面板的不计入；
//   三、撤销点中的是功能区那张卡的按钮，面板那张不动；
//   四、撤过的卡不再被选中，撤到没有为止；
//   五、重叠警告要二次确认：第一次报 clicked 但没撤，第二次才是 forced；
//   六、上一次还没结束时不重复投递。
//
// 运行：node tests/web/ribbon-undo.test.mjs

const posted = [];
let hostHandler = null;

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
  一、querySelector / querySelectorAll 走子树，卡片填充与撤销按钮定位全靠它们；
  二、dataset 是普通对象，来源标记与撤销态都存在上面；
  三、按钮的 click 监听要能被直接调用——功能区撤销就是去点它。
*/
function makeNode(tag = 'div') {
  const node = {
    tag,
    _text: '',
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
        let child = kid;
        if (typeof kid === 'string' || typeof kid === 'number') {
          child = makeNode('#text');
          child.textContent = String(kid);
        }
        if (child && typeof child === 'object') { child.parent = node; }
        node.children.push(child);
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
    // 真 DOM 里禁用按钮的 click() 不触发任何事件，照这个语义来——
    // 「正忙时点了不算」这条断言全靠它才成立。
    click: () => {
      if (node.disabled) { return; }
      node.listeners.get('click')?.({
        preventDefault: () => {},
        stopPropagation: () => {},
      });
    },
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

  Object.defineProperty(node, 'textContent', {
    get: () => {
      if (node.children.length === 0) { return node._text ?? ''; }
      return [node._text ?? '', ...node.children.map((c) => c.textContent || '')].join('');
    },
    set: (value) => {
      node._text = value ?? '';
      for (const kid of node.children) {
        if (kid && typeof kid === 'object') { kid.parent = null; }
      }
      node.children = [];
    },
  });

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

const transcript = nodeFor('transcript');
const fitButton = nodeFor('fit');
const tick = () => new Promise((resolve) => setImmediate(resolve));

const cards = () => transcript.children.filter((n) => n.classes?.has('tool-card'));
const cardSources = () => cards().map((c) => c.dataset.source ?? '');
const undoOf = (card) => card.querySelector('.tool-undo');

/** 回应加载项调用。不回应会让 5 分钟的超时定时器把进程挂住。 */
function reply(channel, data, ok = true, errorCode = undefined) {
  const message = [...posted].reverse().find((m) => m.channel === channel);
  if (!message) { throw new Error(`没有发出 ${channel}`); }
  hostHandler({
    data: {
      kind: 'response',
      id: message.id,
      ok,
      data,
      error: ok ? undefined : '宿主拒绝',
      errorCode,
    },
  });
  return message;
}

const okFit = (seq) => ({
  ok: true,
  undoId: `fit-${seq}`,
  address: '$A$1:$D$6',
  sheet: 'Sheet1',
  rows: 6,
  columns: 4,
  horizontalAlignment: 'center',
});

const parseState = () => {
  const raw = window.__chatsheetRibbonUndoState();
  const out = {};
  for (const part of raw.split('|')) {
    const at = part.indexOf('=');
    out[part.slice(0, at)] = part.slice(at + 1);
  }
  return out;
};

console.log('检查功能区撤销的作用范围：');

// ---- 一、两个来源各产生一张卡，标记不同 ----

check('页面挂出了功能区的三个入口',
  typeof window.__chatsheetRibbonFit === 'function' &&
    typeof window.__chatsheetRibbonUndo === 'function' &&
    typeof window.__chatsheetRibbonUndoState === 'function');

check('起始没有可撤销的功能区操作', parseState().count === '0', JSON.stringify(parseState()));

// 面板里点一次。
fitButton.listeners.get('click')?.({});
await tick();
reply('sheet.fit', okFit(1));
await tick();

check('面板点击产生一张卡', cards().length === 1, `卡片 ${cards().length} 张`);
check('面板的卡片来源是 panel', cardSources()[0] === 'panel', cardSources().join('、'));
check('面板点击不计入功能区可撤数', parseState().count === '0', JSON.stringify(parseState()));

// 功能区点一次。
const fitResult = window.__chatsheetRibbonFit();
await tick();
check('功能区入口回报已点击', fitResult === 'clicked', String(fitResult));
reply('sheet.fit', okFit(2));
await tick();

check('功能区点击产生第二张卡', cards().length === 2, `卡片 ${cards().length} 张`);
check('功能区的卡片来源是 ribbon', cardSources()[1] === 'ribbon', cardSources().join('、'));

// ---- 二、状态只数功能区那些卡 ----

let state = parseState();
check('可撤数只算功能区那一张', state.count === '1', JSON.stringify(state));
check('摘要写出撤销的是什么', state.summary.includes('适配'), state.summary);
check('起始不处于重叠警告态', state.warned === 'false', state.warned);

// ---- 三、撤销点中的是功能区那张卡 ----

const panelUndo = undoOf(cards()[0]);
const ribbonUndo = undoOf(cards()[1]);
check('两张卡都有撤销按钮', Boolean(panelUndo) && Boolean(ribbonUndo));

const undoResult = window.__chatsheetRibbonUndo();
await tick();

check('撤销回报已点击', undoResult === 'clicked', String(undoResult));

const undoCall = [...posted].reverse().find((m) => m.channel === 'undo.apply');
check('发出了撤销请求', Boolean(undoCall));
check('撤销的是功能区那条记录，不是面板那条',
  undoCall?.payload?.id === 'fit-2', JSON.stringify(undoCall?.payload));
check('第一次撤销不带 force', undoCall?.payload?.force === false,
  JSON.stringify(undoCall?.payload));

reply('undo.apply', { ok: true, undone: true });
await tick();

check('功能区那张卡标成已撤销', ribbonUndo.dataset.undone === 'true', ribbonUndo.dataset.undone);
check('面板那张卡没被碰', panelUndo.dataset.undone === 'false', panelUndo.dataset.undone);

// ---- 四、撤过的不再被选中 ----

state = parseState();
check('撤完之后没有可撤的功能区操作', state.count === '0', JSON.stringify(state));
check('再点撤销回报无事可做', window.__chatsheetRibbonUndo() === 'nothing');

// ---- 五、重叠警告要二次确认 ----

window.__chatsheetRibbonFit();
await tick();
reply('sheet.fit', okFit(3));
await tick();

check('又有一张功能区卡片', parseState().count === '1', JSON.stringify(parseState()));

const first = window.__chatsheetRibbonUndo();
await tick();
check('重叠前这次回报的是普通点击', first === 'clicked', String(first));

// 重叠不是通道级失败：加载项把它放在数据里回传（信封仍是成功），
// 面板据此把按钮改成「仍然撤销」而不是弹一条错误。
reply('undo.apply', {
  ok: false,
  errorCode: 'OVERLAP_WARNING',
  message: '这次撤销会覆盖之后的一次改动。',
});
await tick();

const warnedCard = cards()[2];
const warnedButton = undoOf(warnedCard);
check('重叠时没有真的撤销', warnedButton.dataset.undone === 'false', warnedButton.dataset.undone);
check('按钮改成「仍然撤销」', warnedButton.textContent === '仍然撤销', warnedButton.textContent);

state = parseState();
check('状态报出处于重叠警告态', state.warned === 'true', JSON.stringify(state));
check('重叠期间这一步仍算可撤', state.count === '1', JSON.stringify(state));

const second = window.__chatsheetRibbonUndo();
await tick();
check('第二次回报 forced，说明是确认后的执行', second === 'forced', String(second));

const forcedCall = [...posted].reverse().find((m) => m.channel === 'undo.apply');
check('第二次带上 force', forcedCall?.payload?.force === true,
  JSON.stringify(forcedCall?.payload));

reply('undo.apply', { ok: true, undone: true });
await tick();
check('确认后确实撤销了', warnedButton.dataset.undone === 'true', warnedButton.dataset.undone);
check('撤销后重叠标记清掉', parseState().warned === 'false', JSON.stringify(parseState()));

// ---- 六、上一次还没结束时不重复投递 ----

window.__chatsheetRibbonFit();
await tick();
check('适配在飞时功能区入口回报正忙',
  window.__chatsheetRibbonFit() === 'busy');
reply('sheet.fit', okFit(4));
await tick();

console.log('');
console.log(`=== 功能区撤销作用范围：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
