// 面板「适配」的呈现方式：与模型发起的操作同结构，只在来源上区分。
//
// 为什么要锁住这件事：适配原先呈现为一条提示胶囊，而模型发起的写入是一张可折叠
// 的操作卡片。两者要回答的问题完全一样——改了哪个范围、影响多少格、能不能撤销——
// 却要在两种样式里找同一种信息。改成同一种卡片后，区别只剩「谁发起的」，
// 由摘要行上的「手动」标记和边条颜色承担。
//
// 因此这里断言的是结构一致 + 来源可读：
//   一、适配落在对话流里，是一张 .tool-card，不再是提示胶囊；
//   二、卡片带 is-manual 与「手动」标记——只靠颜色说不出区别在哪，
//       色觉障碍下也可能根本看不出来；
//   三、撤销按钮挂在卡片上，用的是宿主回传的记录标识，不是开跑时的临时标识；
//   四、没有撤销记录时把原因写进卡片，而不是静悄悄少一个按钮；
//   五、失败也走同一张卡片，不会留下一张永远停在「执行中…」的卡。
//
// 运行：node tests/web/fit-card.test.mjs

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
  假 DOM。与其他面板单测的区别在于 querySelector 是真的：卡片的填充全靠
  `card.querySelector('.tool-state')` 这类调用，返回 null 的话被测代码直接抛异常，
  什么也验不到。这里按类名走子树，够用且不必引入整个 DOM 实现。
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

  // className 与 classList 共用一个集合：被测代码新建节点写 className、
  // 改状态用 classList，各记一份就要猜某个类名是哪种方式加的。
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

/** 只认类名与标签名。被测代码在卡片上用的就这两种。 */
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

// 适配浮层里的三个对齐选项。initFit 按 .fit-item 找它们并逐个绑定点击，
// 少了这些就没有入口能触发一次适配。
const fitPop = nodeFor('fit-pop');
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
const tick = () => new Promise((resolve) => setImmediate(resolve));

/** 点某个对齐选项，等同用户点开浮层选一项。 */
const clickFit = (align) => {
  const item = fitPop.children.find((n) => n.dataset.align === align);
  item.listeners.get('click')?.({});
};

/** 回应加载项调用。不回应会让 5 分钟的超时定时器把进程挂住。 */
function reply(channel, data, ok = true) {
  const message = [...posted].reverse().find((m) => m.channel === channel);
  if (!message) { throw new Error(`没有发出 ${channel}`); }
  hostHandler({
    data: { kind: 'response', id: message.id, ok, data, error: ok ? undefined : '宿主拒绝' },
  });
  return message;
}

const cards = () => transcript.children.filter((n) => n.classes?.has('tool-card'));
const lastCard = () => cards()[cards().length - 1];
const textIn = (card, cls) =>
  descendants(card).find((n) => n.classes?.has(cls))?.textContent ?? '';
const undoIn = (card) => descendants(card).find((n) => n.classes?.has('tool-undo'));
const notices = () => transcript.children.filter((n) => n.classes?.has('notice'));

console.log('检查适配的呈现方式：');

// ---- 一、开跑就上屏，结构与模型发起的操作一致 ----

clickFit('center');
await tick();

const request = [...posted].reverse().find((m) => m.channel === 'sheet.fit');
check('点对齐选项会发起适配', Boolean(request), JSON.stringify(posted.slice(-1)));
check('适配请求带上所选对齐', request?.payload?.horizontalAlignment === 'center',
  JSON.stringify(request?.payload));

check('适配在对话流里落成一张操作卡片', cards().length === 1, `卡片 ${cards().length} 张`);
check('适配不再呈现为提示胶囊', notices().length === 0, `提示 ${notices().length} 条`);

const running = lastCard();
check('卡片带 is-manual', running.classes.has('is-manual'), running.className);
check('卡片名称用中文标签「适配」', textIn(running, 'tool-name') === '适配',
  textIn(running, 'tool-name'));
check('摘要行带「手动」标记', textIn(running, 'tool-origin') === '手动',
  textIn(running, 'tool-origin'));
check('标记有悬停说明', Boolean(descendants(running).find((n) => n.classes?.has('tool-origin'))?.title));
check('等待期间显示执行中', textIn(running, 'tool-state') === '执行中…',
  textIn(running, 'tool-state'));
check('参数里写明了对齐', textIn(running, 'tool-args').includes('center'),
  textIn(running, 'tool-args'));

// ---- 二、成功后填结果，撤销按钮用宿主回传的标识 ----

reply('sheet.fit', {
  ok: true,
  undoId: 'fit-abc123',
  address: '$A$1:$D$6',
  sheet: 'Sheet1',
  rows: 6,
  columns: 4,
  horizontalAlignment: 'center',
});
await tick();

const done = lastCard();
check('成功后不再新增卡片', cards().length === 1, `卡片 ${cards().length} 张`);
check('状态换成影响面说明', textIn(done, 'tool-state').includes('24'),
  textIn(done, 'tool-state'));
check('状态标为成功', descendants(done).some((n) => n.classes?.has('is-ok')));
check('卡片标识改成宿主登记的记录标识', done.dataset.toolId === 'fit-abc123',
  done.dataset.toolId);
check('卡片上出现撤销按钮', Boolean(undoIn(done)));
check('撤销说明带上行列位置', (undoIn(done)?.title ?? '').includes('行'),
  undoIn(done)?.title);

// 撤销必须按记录标识发出去，而不是开跑时的临时标识——
// 这正是这条路径上曾经出过的缺陷（点下去报「找不到该操作记录」）。
undoIn(done).listeners.get('click')?.({ preventDefault: () => {}, stopPropagation: () => {} });
await tick();

const undoCall = [...posted].reverse().find((m) => m.channel === 'undo.apply');
check('撤销请求用的是记录标识', undoCall?.payload?.id === 'fit-abc123',
  JSON.stringify(undoCall?.payload));

reply('undo.apply', { ok: true, undone: true });
await tick();

check('撤销后卡片整体淡化', done.classes.has('is-undone'), done.className);
check('撤销后按钮原地变为恢复', undoIn(done)?.textContent === '恢复',
  undoIn(done)?.textContent);
check('撤销后状态写明已撤销', textIn(done, 'tool-state') === '已撤销',
  textIn(done, 'tool-state'));
check('淡化不覆盖手动来源', done.classes.has('is-manual'), done.className);

// ---- 三、没有撤销记录时把原因写进卡片 ----

clickFit('left');
await tick();
reply('sheet.fit', {
  ok: true,
  undoId: null,
  undoUnavailableReason: '这次适配不能撤销：范围太大，保不住足以完整还原的排版快照。',
  address: '$A$1:$XFD$1048576',
  sheet: 'Sheet1',
  rows: 1048576,
  columns: 16384,
  horizontalAlignment: 'left',
});
await tick();

const noUndo = lastCard();
check('第二次适配落成第二张卡片', cards().length === 2, `卡片 ${cards().length} 张`);
check('没有撤销记录时不给撤销按钮', !undoIn(noUndo));
check('把没有撤销入口的原因写进卡片',
  textIn(noUndo, 'tool-note').includes('不能撤销'), textIn(noUndo, 'tool-note'));
check('第二张卡片同样标为手动', noUndo.classes.has('is-manual'), noUndo.className);

// ---- 四、失败走同一张卡片 ----

clickFit('right');
await tick();
const pendingCard = lastCard();
check('失败前卡片先处于执行中', textIn(pendingCard, 'tool-state') === '执行中…',
  textIn(pendingCard, 'tool-state'));

reply('sheet.fit', { ok: false, message: '当前工作表没有可适配的内容。' });
await tick();

const failedCard = lastCard();
check('失败不新增卡片', cards().length === 3, `卡片 ${cards().length} 张`);
check('失败的卡片标为错误', failedCard.classes.has('is-error'), failedCard.className);
check('失败原因写在状态上', textIn(failedCard, 'tool-state').includes('没有可适配的内容'),
  textIn(failedCard, 'tool-state'));
check('失败时自动展开', failedCard.open === true, `open=${failedCard.open}`);
check('失败的卡片不停在执行中', textIn(failedCard, 'tool-state') !== '执行中…',
  textIn(failedCard, 'tool-state'));
check('失败也不退回提示胶囊', notices().length === 0, `提示 ${notices().length} 条`);

console.log('');
console.log(`=== 适配操作卡片：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
