// 一轮正常收尾的「已完成」标记。
//
// 为什么值得单独锁：这条标记的用处主要在它**不出现**的时候。加载项有四条终止
// 路径——被停止、达步数上限、反复截断、出错——它们各自留下一条胶囊，而正常结束
// 原先什么都不留。于是「模型说完了」与「中途断了，但最后那段话看起来像结论」在
// 屏幕上长得一模一样，只能翻日志区分。补上这条之后，没有它就是没正常收完。
//
// 所以断言分两半：
//   一、正常收尾要有，且与错误、停止同处一处（都是对话流中间的居中胶囊）；
//   二、四条异常路径都不能有——冒出一个「已完成」紧跟着「已停止」，
//       比什么都不显示更糟：两条自相矛盾的收尾，用户不知道该信哪个。
//
// 顺带锁住它与轮次操作组的先后：标记是这一轮的收尾，组不该排在它下面。
//
// 运行：node tests/web/turn-complete-mark.test.mjs

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

// 假 DOM。append 要把节点从原父节点摘走：本文件既验成组（卡片搬进组里），
// 也验「已完成」被重新挂到末尾——两者都靠这个行为，做虚了顺序断言全部失真。
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
        if (!kid || typeof kid !== 'object') { node.children.push(kid); continue; }
        if (kid.parent && kid.parent !== node) {
          kid.parent.children = kid.parent.children.filter((n) => n !== kid);
        } else if (kid.parent === node) {
          node.children = node.children.filter((n) => n !== kid);
        }
        kid.parent = node;
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
    getBoundingClientRect: () => ({ width: 400, height: 200 }),
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

function descendants(node) {
  const out = [];
  for (const kid of node.children) {
    if (!kid || typeof kid !== 'object') { continue; }
    out.push(kid);
    if (Array.isArray(kid.children)) { out.push(...descendants(kid)); }
  }
  return out;
}

function matches(node, selector) {
  for (const part of String(selector).split(',')) {
    const one = part.trim();
    const attr = one.match(/^\.([\w-]+)\[([\w-]+)="(.*)"\]$/);
    if (attr) {
      const [, cls, name, value] = attr;
      const key = name.startsWith('data-')
        ? name.slice(5).replace(/-(\w)/g, (_, c) => c.toUpperCase())
        : name;
      if (node.classes?.has(cls) && node.dataset?.[key] === value) { return true; }
      continue;
    }
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
const composer = nodeFor('composer');
const sendButton = nodeFor('send');
const tick = () => new Promise((resolve) => setImmediate(resolve));

const topLevel = (cls) => transcript.children.filter((n) => n.classes?.has(cls));
const marks = () => topLevel('notice-complete');
const notices = () => topLevel('notice');

function reply(channel, data, ok = true) {
  const message = [...posted].reverse().find((m) => m.channel === channel);
  if (!message) { throw new Error(`没有发出 ${channel}`); }
  hostHandler({
    data: { kind: 'response', id: message.id, ok, data, error: ok ? undefined : '宿主拒绝' },
  });
  return message;
}

const agent = (payload) => hostHandler({ data: { kind: 'agent', ...payload } });

async function startTurn(text) {
  composer.value = text;
  sendButton.listeners.get('click')?.({});
  await tick();
}

/** 一轮以某个终止路径结束。normal 走 turn-complete，其余走各自那条。 */
async function endTurn(kind) {
  if (kind === 'normal') {
    agent({ stage: 'turn-complete' });
  } else if (kind === 'stopped') {
    agent({ stage: 'stopped', text: '已停止生成。' });
  } else if (kind === 'error') {
    agent({ stage: 'error', text: '接口返回 401。' });
  } else if (kind === 'step-limit') {
    agent({ stage: 'step-limit', text: '已达到单轮步数上限（20 步）。' });
  } else if (kind === 'stalled') {
    agent({ stage: 'stalled', text: '模型的输出连续 3 次被长度上限截断，已停止。' });
  }
  reply('chat.send', { ok: true });
  await tick();
}

console.log('检查一轮收尾的「已完成」标记：');
console.log('');

// ---- 一、正常收尾要有，且是居中胶囊 ----

await startTurn('把 B 列改成日期格式');
agent({ stage: 'text', text: '改好了。' });
await tick();

check('收尾之前没有完成标记', marks().length === 0, `${marks().length} 个`);

await endTurn('normal');

check('正常收尾插一条完成标记', marks().length === 1, `${marks().length} 个`);

// 拿不到标记时后面这些断言全都读不到属性。不加保护的话第一条就抛，
// 剩下的断言一条也跑不到——真出回归时看到的是堆栈而不是「哪几件事坏了」。
const mark = marks()[0] ?? makeNode();
check('文字就是「已完成」', mark.textContent === '已完成', mark.textContent);
check('与错误、停止同处一处：都是 .notice 居中胶囊',
  mark.classes.has('notice'), mark.className);
check('用主色那一档，不是错误档',
  mark.classes.has('notice-ok') && !mark.classes.has('notice-error'), mark.className);
check('带悬停说明，讲清没有它意味着什么',
  (mark.title ?? '').includes('中途断了'), mark.title);
check('标记落在对话流末尾',
  transcript.children[transcript.children.length - 1] === mark,
  `末位是 ${transcript.children[transcript.children.length - 1]?.className}`);

// ---- 二、四条异常路径都不能有 ----

for (const [kind, label, expectText] of [
  ['stopped', '被停止', '已停止生成'],
  ['error', '出错', '401'],
  ['step-limit', '达步数上限', '步数上限'],
  ['stalled', '反复截断', '截断'],
]) {
  const before = marks().length;
  const noticesBefore = notices().length;

  await startTurn(`触发${label}的一轮`);
  agent({ stage: 'text', text: '这一轮不会正常收尾。' });
  await tick();
  await endTurn(kind);

  check(`${label}不插完成标记`, marks().length === before,
    `完成标记从 ${before} 变成 ${marks().length}`);
  // 但那一条说明本身必须在——否则这一轮就什么收尾都没有了。
  const last = notices()[notices().length - 1];
  check(`${label}留下的是对应的说明`, (last?.textContent ?? '').includes(expectText),
    last?.textContent);
  check(`${label}这一轮确实多了一条胶囊`, notices().length === noticesBefore + 1,
    `${noticesBefore} → ${notices().length}`);
}

// ---- 三、与轮次操作组的先后 ----

// 跑一轮带操作的，正常收尾，然后开下一轮让它成组。
await startTurn('按销售额降序排列');
agent({ stage: 'tool-start', payload: { id: 's1', name: 'sort_range', risk: 'Write', args: {} } });
await tick();
agent({ stage: 'tool-result', payload: { id: 's1', name: 'sort_range', ok: true, data: { cells_affected: 80 } } });
await tick();
agent({ stage: 'text', text: '排好了。' });
await tick();
await endTurn('normal');

const markBeforeSeal = marks()[marks().length - 1] ?? makeNode();
check('带操作的一轮也有完成标记', Boolean(markBeforeSeal));

await startTurn('下一句话');

const group = topLevel('ops-group')[topLevel('ops-group').length - 1] ?? makeNode();
check('下一轮开始时上一轮成组', Boolean(group));

const order = transcript.children;
check('操作组排在完成标记之前（标记仍是这一轮的收尾）',
  order.indexOf(group) < order.indexOf(markBeforeSeal),
  `组在 ${order.indexOf(group)}，标记在 ${order.indexOf(markBeforeSeal)}`);

// 序号必须与 DOM 顺序一致，否则还原一次就会把两者的先后倒过来。
const seqs = order.map((n) => Number(n.dataset?.seq ?? 0));
check('挂载序号与当前顺序一致',
  seqs.every((v, i) => i === 0 || seqs[i - 1] <= v), JSON.stringify(seqs));

// 还原之后先后关系还要成立：这正是序号没跟着刷新时会坏掉的地方。
const restore = descendants(group).find((n) => n.classes?.has('ops-restore'));
restore?.listeners.get('click')?.({ preventDefault: () => {}, stopPropagation: () => {} });
await tick();

const afterOrder = transcript.children;
const restoredCard = afterOrder.find((n) => n.classes?.has('tool-card'));
check('还原后完成标记仍排在那张卡片之后',
  afterOrder.indexOf(restoredCard) < afterOrder.indexOf(markBeforeSeal),
  `卡片在 ${afterOrder.indexOf(restoredCard)}，标记在 ${afterOrder.indexOf(markBeforeSeal)}`);

const afterSeqs = afterOrder.map((n) => Number(n.dataset?.seq ?? 0));
check('还原后序号仍严格不降', afterSeqs.every((v, i) => i === 0 || afterSeqs[i - 1] <= v),
  JSON.stringify(afterSeqs));

// ---- 四、新会话清掉标记 ----

nodeFor('reset').listeners.get('click')?.({});
await tick();
reply('chat.reset', { ok: true });
await tick();
reply('settings.get', {
  ready: true, readyDetail: '已就绪', approval: 'PerWrite', approvalOptions: [],
});
await tick();

check('新会话后不再有旧的完成标记', marks().length === 0, `${marks().length} 个`);

console.log('');
console.log(`=== 完成标记：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
