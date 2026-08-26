// 操作卡片按轮次成组，以及还原回原位。
//
// 为什么要锁住这件事：成组改的是对话流里节点的父子关系与顺序，而这两样没有
// 任何报错途径——收错了只是某几张卡片出现在不该出现的位置，收多了会把当前正在
// 跑的操作藏起来，还原错了则把整条对话流的顺序打乱。这些都只能靠「一轮轮跑完
// 再逐个节点看」才发现，肉眼过一遍盖不住。
//
// 断言五组：
//   一、当前轮的操作不成组——正在发生的事必须立刻可见（执行中状态、失败原因、
//       撤销按钮都在卡片上）；
//   二、下一轮开始时才收上一轮，且组落在上一轮内容之后，不搬到对话流底部；
//   三、摘要给出统计（几个操作、几改几读、失败、已撤销），失败要在组上留记号，
//       撤销发生在成组之后也要跟着改；
//   四、还原把卡片放回原位——按挂载序号重排，因此穿插在回复之间，
//       且还原过的不再被下一轮收回去；
//   五、手动操作（面板点「适配」）归入所在那一批，不另立一组。
//
// 运行：node tests/web/ops-group.test.mjs

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
  假 DOM。与 fit-card.test.mjs 的那份比，有两处必须做实，否则本文件测不到东西：

  一、append 要把节点从原父节点摘走。成组就是把卡片从对话流搬进组里，
      真实 DOM 会自动解除原来的父子关系；假 DOM 不解除的话卡片会同时出现在
      两处，「组落在哪」「组外还剩几张」全都失真。
  二、dataset 要能被读回。还原按挂载序号重排，序号存在 dataset.seq 上。
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
        if (!kid || typeof kid !== 'object') {
          node.children.push(kid);
          continue;
        }

        // 真实 DOM 的 append 会先把节点从原来的位置摘走。成组正是靠这一点：
        // 卡片搬进组里之后不该还留在对话流的子节点列表里。
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
    click: () => node.listeners.get('click')?.({ preventDefault: () => {}, stopPropagation: () => {} }),
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

/**
 * 认类名、标签名，以及 .tool-card[data-tool-id="…"] 这一种属性选择器。
 *
 * 属性那一支必须做实：工具结果是靠它把卡片找回来填的
 * （finishToolCard）。返回 null 的话结果永远填不上，卡片会一直停在
 * 「执行中…」，而撤销按钮、失败标记、自动展开全都不会出现——
 * 于是本文件后面那些断言测的就是一堆空卡片。
 */
function matches(node, selector) {
  for (const part of String(selector).split(',')) {
    const one = part.trim();

    const attr = one.match(/^\.([\w-]+)\[([\w-]+)="(.*)"\]$/);
    if (attr) {
      const [, cls, name, value] = attr;
      // data-tool-id 在 dataset 上是 toolId。
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

// 适配浮层里的三个对齐选项，initFit 靠它们绑定点击。
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
const composer = nodeFor('composer');
const sendButton = nodeFor('send');
const tick = () => new Promise((resolve) => setImmediate(resolve));

/** 取对话流直接子节点里带某个类的。成组后卡片不再是直接子节点。 */
const topLevel = (cls) => transcript.children.filter((n) => n.classes?.has(cls));
const groups = () => topLevel('ops-group');
const inGroup = (group, cls) => descendants(group).filter((n) => n.classes?.has(cls));
const textIn = (root, cls) =>
  descendants(root).find((n) => n.classes?.has(cls))?.textContent ?? '';

/** 回应加载项调用。不回应会让超时定时器把进程挂住。 */
function reply(channel, data, ok = true) {
  const message = [...posted].reverse().find((m) => m.channel === channel);
  if (!message) { throw new Error(`没有发出 ${channel}`); }
  hostHandler({
    data: { kind: 'response', id: message.id, ok, data, error: ok ? undefined : '宿主拒绝' },
  });
  return message;
}

/** 推一条 agent 消息，等同加载项在一轮里推送进展。 */
const agent = (payload) => hostHandler({ data: { kind: 'agent', ...payload } });

/** 走真实提交路径开一轮：填输入框、点发送。 */
async function startTurn(text) {
  composer.value = text;
  sendButton.listeners.get('click')?.({});
  await tick();
}

/** 让当前轮结束：回应 chat.send 并推 turn-complete。 */
async function finishTurn() {
  agent({ stage: 'turn-complete' });
  reply('chat.send', { ok: true });
  await tick();
}

/** 跑一个工具调用：开始、结束。 */
async function runTool({ id, name, risk, ok = true, canUndo = false, data = {} }) {
  agent({ stage: 'tool-start', payload: { id, name, risk, args: { range: 'A1:B2' } } });
  await tick();
  agent({
    stage: 'tool-result',
    payload: { id, name, ok, data, canUndo, error: ok ? undefined : '范围无效', undoSummary: '写入 A1:B2' },
  });
  await tick();
}

const clickFit = (align) => {
  fitPop.children.find((n) => n.dataset.align === align).listeners.get('click')?.({});
};

console.log('检查操作按轮次成组：');
console.log('');

// ---- 一、当前轮的操作平铺，不成组 ----

await startTurn('把 B 列改成日期格式');
agent({ stage: 'text', text: '好，我来改。' });
await tick();
await runTool({ id: 't1', name: 'read_range', risk: 'Read' });
await runTool({ id: 't2', name: 'set_number_format', risk: 'Write', canUndo: true, data: { address: '$B$1:$B$20', cells_affected: 20 } });

check('当前轮的卡片平铺在对话流里', topLevel('tool-card').length === 2,
  `直接子节点里的卡片 ${topLevel('tool-card').length} 张`);
check('当前轮还没有成组', groups().length === 0, `组 ${groups().length} 个`);

await finishTurn();

check('一轮结束后仍不成组', groups().length === 0,
  `组 ${groups().length} 个——刚跑完正要看结果，此刻收起来等于把东西收走`);
check('结束后卡片仍平铺', topLevel('tool-card').length === 2,
  `${topLevel('tool-card').length} 张`);

// ---- 二、下一轮开始时才收上一轮，且落在上一轮内容之后 ----

// 记下第二轮开始前对话流的样子，用来验证组插在哪。
const beforeSecond = [...transcript.children];

await startTurn('再按销售额降序排列');

check('第二轮开始时上一轮收成一组', groups().length === 1, `组 ${groups().length} 个`);
check('上一轮的卡片已不在对话流顶层', topLevel('tool-card').length === 0,
  `顶层还剩 ${topLevel('tool-card').length} 张`);
check('两张卡片都进了这一组', inGroup(groups()[0], 'tool-card').length === 2,
  `组里 ${inGroup(groups()[0], 'tool-card').length} 张`);

// 组必须落在上一轮内容之后、新用户气泡之前：这是「跟着那轮走」的含义。
// 搬到对话流最底部的话，第二轮的气泡会排在组前面。
//
// 「上一轮内容」这里不算那条「已完成」：它是这一轮的收尾，成组时会被移到组的
// 后面，好让每轮读下来都以它结束（见 turn-complete-mark.test.mjs 里的先后断言）。
// 把它算进来的话，「组在上一轮之后」就永远不成立，而那不是这条断言要说的事。
const order = transcript.children;
const groupIndex = order.indexOf(groups()[0]);
const lastOldIndex = Math.max(
  ...beforeSecond
    .filter((n) => order.includes(n) && !n.classes?.has('notice-complete'))
    .map((n) => order.indexOf(n)),
);
const newBubbleIndex = order.findIndex(
  (n) => n.classes?.has('msg-user') && !beforeSecond.includes(n),
);

check('组排在上一轮内容之后', groupIndex > lastOldIndex,
  `组在 ${groupIndex}，上一轮末尾在 ${lastOldIndex}`);
check('组排在新一轮的用户气泡之前（没搬到对话流底部）',
  newBubbleIndex > groupIndex, `新气泡在 ${newBubbleIndex}，组在 ${groupIndex}`);
check('组默认收起', groups()[0].open === false, `open=${groups()[0].open}`);
check('组上有还原入口', Boolean(descendants(groups()[0]).find((n) => n.classes?.has('ops-restore'))));

// ---- 三、摘要给出统计 ----

const firstLabel = () => textIn(groups()[0], 'ops-label');

check('摘要写明是第几轮', firstLabel().includes('第 1 轮'), firstLabel());
check('摘要给出操作总数', firstLabel().includes('2 个操作'), firstLabel());
check('摘要按改/读分类计数', firstLabel().includes('1 改') && firstLabel().includes('1 读'),
  firstLabel());
check('摘要不提失败（本组无失败）', !firstLabel().includes('失败'), firstLabel());
check('无失败的组不标错误', !groups()[0].classes.has('is-error'), groups()[0].className);

const head = descendants(groups()[0]).find((n) => n.classes?.has('ops-head'));
check('悬停说明逐条列出组里的操作',
  (head?.title ?? '').includes('读取范围') && (head?.title ?? '').includes('设置数字格式'),
  head?.title);

// 撤销发生在成组之后，摘要要跟着改——收起时那是唯一可见的说法。
const undoButton = descendants(groups()[0]).find((n) => n.classes?.has('tool-undo'));
check('组里的卡片仍带撤销按钮', Boolean(undoButton));

undoButton.listeners.get('click')?.({ preventDefault: () => {}, stopPropagation: () => {} });
await tick();
reply('undo.apply', { ok: true, undone: true });
await tick();

check('成组之后撤销，摘要跟着改', firstLabel().includes('1 已撤销'), firstLabel());
check('撤销后总数不变', firstLabel().includes('2 个操作'), firstLabel());

// ---- 三续、失败要在组上留记号 ----

await runTool({ id: 't3', name: 'sort_range', risk: 'Write', ok: false });
await finishTurn();
await startTurn('第三句话');

check('第二轮也收成组', groups().length === 2, `组 ${groups().length} 个`);

const second = groups()[1];
const secondLabel = () => textIn(second, 'ops-label');
check('有失败的组在摘要里说明', secondLabel().includes('1 失败'), secondLabel());
check('有失败的组整体标错误', second.classes.has('is-error'), second.className);
check('失败的卡片本身仍是展开的',
  inGroup(second, 'tool-card')[0].open === true,
  `open=${inGroup(second, 'tool-card')[0].open}`);

// ---- 四、还原把卡片放回原位 ----

// 还原第一组。它的两张卡片当初穿插在助手回复与用户气泡之间，
// 还原后必须回到那些位置，而不是挤在一起或跑到末尾。
const firstGroup = groups()[0];
const restore = descendants(firstGroup).find((n) => n.classes?.has('ops-restore'));
const cardsInFirst = inGroup(firstGroup, 'tool-card');

restore.listeners.get('click')?.({ preventDefault: () => {}, stopPropagation: () => {} });
await tick();

check('还原后这一组消失', !transcript.children.includes(firstGroup),
  '组还在对话流里');
check('还原后只剩另一组', groups().length === 1, `组 ${groups().length} 个`);
check('还原的卡片回到对话流顶层',
  cardsInFirst.every((c) => transcript.children.includes(c)),
  `回到顶层 ${cardsInFirst.filter((c) => transcript.children.includes(c)).length}/2 张`);

// 按挂载序号重排的效果：整条对话流的顺序与当初上屏的顺序一致。
const seqs = transcript.children.map((n) => Number(n.dataset?.seq ?? 0));
const ascending = seqs.every((v, i) => i === 0 || seqs[i - 1] <= v);
check('还原后对话流按上屏顺序排列', ascending, JSON.stringify(seqs));

// 卡片穿插在回复之间，而不是被堆到末尾：这一组的卡片当初发生在第一轮里，
// 因此它们前后都还应当有别的东西——前面是第一轮的气泡，后面是后来那几轮。
const firstCardIndex = transcript.children.indexOf(cardsInFirst[0]);
const firstUserIndex = transcript.children.findIndex((n) => n.classes?.has('msg-user'));
const lastCardIndex = transcript.children.indexOf(cardsInFirst[cardsInFirst.length - 1]);

check('还原的卡片排在第一轮的气泡之后', firstCardIndex > firstUserIndex,
  `卡片在 ${firstCardIndex}，第一条用户气泡在 ${firstUserIndex}`);
check('还原的卡片没有被堆到对话流末尾',
  lastCardIndex < transcript.children.length - 1,
  `末张卡片在 ${lastCardIndex}，对话流共 ${transcript.children.length} 项`);
check('另一组不受还原影响', inGroup(groups()[0], 'tool-card').length === 1,
  `${inGroup(groups()[0], 'tool-card').length} 张`);

// 还原过的不该被下一轮收回去：还原是明确的「我要看原位」。
await finishTurn();
await startTurn('第四句话');

check('还原过的卡片不被下一轮收回去',
  cardsInFirst.every((c) => transcript.children.includes(c)),
  `被收走 ${cardsInFirst.filter((c) => !transcript.children.includes(c)).length} 张`);
check('新一轮不为已还原的卡片另建组', groups().length === 1, `组 ${groups().length} 个`);

// ---- 五、手动操作归入所在那一批 ----

// 第四轮里点一次「适配」，它与这一轮的模型操作应当进同一组。
await runTool({ id: 't4', name: 'write_values', risk: 'Write', canUndo: true, data: { cells_written: 6 } });

clickFit('center');
await tick();
reply('sheet.fit', {
  ok: true,
  undoId: 'fit-1',
  address: '$A$1:$D$6',
  rows: 6,
  columns: 4,
});
await tick();

check('手动操作平铺上屏', topLevel('tool-card').length >= 2,
  `顶层 ${topLevel('tool-card').length} 张`);

await finishTurn();
await startTurn('第五句话');

check('本轮收成新的一组', groups().length === 2, `组 ${groups().length} 个`);

const withManual = groups()[1];
check('手动操作与模型操作同组', inGroup(withManual, 'tool-card').length === 2,
  `组里 ${inGroup(withManual, 'tool-card').length} 张`);
check('组里仍能分辨出手动来源',
  inGroup(withManual, 'tool-card').some((c) => c.classes.has('is-manual')),
  '没有一张带 is-manual');
check('手动标记在组里仍可读',
  descendants(withManual).some((n) => n.classes?.has('tool-origin') && n.textContent === '手动'));

const manualLabel = textIn(withManual, 'ops-label');
check('适配计入「改」而不是「读」', manualLabel.includes('2 改'), manualLabel);
check('摘要不出现 0 计数的分类', !manualLabel.includes('0 '), manualLabel);

// ---- 六、纯对话的一轮不留空组 ----

// 第五轮一个工具都不调，只有来回说话。下一轮开始时不该为它建一个空组：
// 「0 个操作」这一行既没有信息，又占掉一行位置。
const groupsBefore = groups().length;
agent({ stage: 'text', text: '这一轮我不需要动表。' });
await tick();
await finishTurn();
await startTurn('第六句话');

check('没有操作的一轮不建组', groups().length === groupsBefore,
  `组从 ${groupsBefore} 变成 ${groups().length}`);

console.log('');
console.log(`=== 操作按轮次成组：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
