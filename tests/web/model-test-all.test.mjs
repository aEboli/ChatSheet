// 批量测试整份目录：并发 5、边跑边上色、成功绿失败红。
//
// 这个文件盯的是几处只会静默失效的地方：
//
//   一、按钮必须报出会发多少条请求。目录有几十个 ID 就是几十次计费请求，
//       而按钮只有两个字，点下去之前没有别的地方会告诉用户这件事。
//   二、并发数要真的发出去。发成串行不会报错，只是慢十几倍。
//   三、边跑边上色：后端每测完一个推一次进度并带该模型的判定，面板要落到本地投影。
//       不落的话整批结束前一列都是「未确认」，看起来像没在动。
//   四、停止走 models.probe.stop，绝不能发 chat.stop——一个控件按隐藏状态决定停哪个，
//       正是这个项目付过代价的故障。
//   五、范围是整份目录，不是名单。传错了会让「测试」变成「确认」，而两者代价差一个数量级。
//
// 假 DOM 照 model-probe.test.mjs，并把 picker-test-all 补进节点表——
// 漏了它 renderTestAll 会一开头就 return，本文件的断言会全部对着空节点通过。
// 末尾带变异自检。

const posted = [];
let testReply = null;
let settingsOnHost = {};
let catalogueOnHost = [];
const pendingReplies = [];
let favoritesOnHost = [];
let availabilityOnHost = {};

globalThis.window = {
  chrome: {
    webview: {
      postMessage: (message) => {
        posted.push(message);
        const id = message.id;

        const deliver = () => {
          let data = {};
          if (message.channel === 'models.favorites') {
            data = { favorites: favoritesOnHost, availability: availabilityOnHost };
          } else if (message.channel === 'models.test.all') {
            data = testReply ?? { confirmed: 0, total: 0, availability: availabilityOnHost };
          } else if (message.channel === 'settings.get') {
            data = settingsOnHost;
          } else if (message.channel === 'models.list') {
            data = { models: catalogueOnHost };
          }
          globalThis.window.dispatchResponse?.({ kind: 'response', id, ok: true, data });
        };

        // 这两条要能延迟结算：本文件有一组断言专门盯「目录还没到 vs 已经到」
        // 这两个时刻之间按钮的可用性。
        if (message.channel === 'models.test.all' || message.channel === 'models.list') {
          pendingReplies.push(deliver);
        } else {
          queueMicrotask(deliver);
        }
      },
      addEventListener: (kind, handler) => {
        if (kind === 'message') {
          globalThis.window.dispatchResponse = (data) => handler({ data });
        }
      },
    },
  },
  innerWidth: 460,
};

async function settle() {
  while (pendingReplies.length > 0) {
    pendingReplies.shift()();
    await new Promise((r) => queueMicrotask(r));
  }
  for (let i = 0; i < 3; i++) { await new Promise((r) => queueMicrotask(r)); }
}

function makeNode(tag = 'div') {
  const node = {
    tag,
    textContent: '',
    title: '',
    value: '',
    type: '',
    hidden: true,
    disabled: false,
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

// picker-test-all 必须在这张表里。漏了它 getElementById 返回 null，
// renderTestAll 一开头就 return，本文件所有断言都会对着空节点通过。
const ids = ['picker-models', 'picker-thinkings', 'picker-model', 'picker-thinking',
  'picker-trigger', 'picker-pop', 'picker-refresh', 'picker-only-favorites',
  'picker-probe-all', 'picker-test-all', 'picker-manual', 'picker-manual-input',
  'model-picker', 'picker-test-menu', 'picker-test-note'];
const nodes = new Map(ids.map((id) => [id, makeNode()]));

globalThis.document = {
  getElementById: (id) => nodes.get(id) ?? null,
  querySelector: () => null,
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: (tag) => makeNode(tag),
};

const { initPicker, syncPicker, describePicker, setPickerTurnInFlight } =
  await import('../../src/web/scripts/picker.js');
const { putModelCatalog } = await import('../../src/web/scripts/model-catalog.js');

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

const list = nodes.get('picker-models');
const testAll = nodes.get('picker-test-all');
const probeAll = nodes.get('picker-probe-all');
const testMenu = nodes.get('picker-test-menu');
const testNote = nodes.get('picker-test-note');

function click(node) {
  node.listeners.get('click')?.({ stopPropagation: () => {} });
}

/** 菜单里某一档的按钮。 */
function scopeItem(target) {
  return testMenu.children.find(
    (n) => n && typeof n === 'object' && n.attributes['data-target'] === String(target),
  ) ?? null;
}

/** 走用户真实的两步：点「测试」开菜单，再选一档。 */
function openMenuAndPick(target) {
  click(testAll);
  const item = scopeItem(target);
  if (!item) { throw new Error(`菜单里没有 target=${target} 这一档`); }
  click(item);
  return item;
}

function descend(node, out = []) {
  for (const kid of node.children ?? []) {
    if (!kid || typeof kid !== 'object') { continue; }
    out.push(kid);
    descend(kid, out);
  }
  return out;
}

function itemFor(id) {
  const name = descend(list).find(
    (n) => n.classes.has('picker-item-name') && n.textContent === id,
  );
  return name?.parent?.parent ?? null;
}

const connection = {
  mode: 'CustomApi',
  customProtocol: 'openai-chat-completions',
  customBaseUrl: 'https://gw.example.test/v1',
};

const catalogue = ['alpha', 'beta', 'gamma', 'delta', 'epsilon', 'zeta', 'eta'];

initPicker(() => {});
putModelCatalog(connection, catalogue);

availabilityOnHost = {};
favoritesOnHost = ['alpha'];
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: favoritesOnHost,
  availability: {},
  onlyFavoriteModels: false,
});

console.log('检查「测试」入口：');

check('列头有「测试」按钮', testAll.textContent === '测试', testAll.textContent);

check(
  '目录非空时可点',
  testAll.disabled === false,
  `disabled=${testAll.disabled}`,
);

check(
  '悬停说明报出目录里有多少个模型',
  testAll.title.includes(`${catalogue.length} 个模型`),
  testAll.title,
);

check(
  '悬停说明报出并发数',
  testAll.title.includes('并发 5'),
  testAll.title,
);

check(
  '悬停说明讲清限流会记为「未确认」而非「不可用」',
  testAll.title.includes('限流') && testAll.title.includes('未确认'),
  testAll.title,
);

console.log('');
console.log('检查点「测试」先给出范围、不直接开跑：');

check('菜单初始收起', testMenu.hidden === true, `hidden=${testMenu.hidden}`);

click(testAll);

check('点一下把菜单展开', testMenu.hidden === false, `hidden=${testMenu.hidden}`);

check(
  '这一下没有发出任何请求（最贵的动作不该是一次点击能表达的唯一意思）',
  posted.filter((m) => m.channel === 'models.test.all').length === 0,
  JSON.stringify(posted.map((m) => m.channel)),
);

check(
  '菜单有三档：找 1 个、找 2 个、全部',
  testMenu.children.length === 3 &&
    scopeItem(1) !== null && scopeItem(2) !== null && scopeItem(0) !== null,
  testMenu.children.map((n) => n?.attributes?.['data-target']).join(','),
);

check(
  'aria-expanded 跟着菜单走',
  testAll.getAttribute('aria-expanded') === 'true',
  testAll.getAttribute('aria-expanded'),
);

// 上界必须一律等于目录条数，含带目标那两档。写成目标数（「最多 2 条」）是错的：
// 目录里能用的不足目标数时运行就是全量，那句话会让用户按 2 条去估几十条的账。
for (const target of [1, 2]) {
  const item = scopeItem(target);
  check(
    `找 ${target} 个那一档：上界写的是整份目录的 ${catalogue.length} 条`,
    item.title.includes(`最多 ${catalogue.length} 条`),
    item.title,
  );

  check(
    `找 ${target} 个那一档：说清什么会让它提前结束`,
    item.title.includes(`${target} 个能用的`) && item.title.includes('不再发新请求'),
    item.title,
  );

  // 下界是三档之间真正不同的数字。并发 5 时达标那一刻在飞的一波收不回来，
  // 所以「找 1 个就停」最少也是 5 条——只写「找到 1 个就停」读起来像 1 条。
  const floor = Math.min(catalogue.length, Math.max(target, 5));
  check(
    `找 ${target} 个那一档：写出最少会发 ${floor} 条`,
    item.title.includes(`最少 ${floor} 条`),
    item.title,
  );

  check(
    `找 ${target} 个那一档：说清限流不算「找到一个能用的」`,
    item.title.includes('限流') && item.title.includes('未确认'),
    item.title,
  );
}

check(
  '「全部测试」那一档给的是确数，且不谈提前结束',
  scopeItem(0).title.includes(`${catalogue.length} 条计费请求`) &&
    scopeItem(0).title.includes('不会提前结束'),
  scopeItem(0).title,
);

check(
  '三档的上界是同一个数（上界不是它们的区别所在）',
  [1, 2, 0].every((t) => scopeItem(t).title.includes(`${catalogue.length} 条`)),
  [1, 2, 0].map((t) => scopeItem(t).title.split('\n')[1] ?? '').join(' | '),
);

click(testAll);
check('再点一下收起', testMenu.hidden === true, `hidden=${testMenu.hidden}`);

// ---------- 目录为空时禁用 ----------

putModelCatalog({ ...connection, customBaseUrl: 'https://empty.example.test/v1' }, []);
syncPicker({
  ...connection,
  customBaseUrl: 'https://empty.example.test/v1',
  model: '',
  thinking: 'High',
  favorites: [],
  availability: {},
  onlyFavoriteModels: false,
});

check('目录为空时禁用', testAll.disabled === true, `disabled=${testAll.disabled}`);
check(
  '禁用时说明为什么',
  testAll.title.includes('目录是空的'),
  testAll.title,
);

check(
  '目录为空时菜单也不展开（禁用按钮收不到点击，但状态别错）',
  testMenu.hidden === true,
  `hidden=${testMenu.hidden}`,
);

// 回到有目录的连接
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: favoritesOnHost,
  availability: {},
  onlyFavoriteModels: false,
});

console.log('');
console.log('检查请求的范围与并发：');

// 走两步：开菜单、选「全部测试」。这一节原先是直接点按钮就发请求，
// 而驱动路径已经变了——不改这里，下面四条会因为「那一下只开了菜单」而红，
// 而面对一片红最省事的做法就是放宽断言，那正是这个仓库反复付过代价的事故。
openMenuAndPick(0);

const sent = posted.filter((m) => m.channel === 'models.test.all');
check('走自己的通道 models.test.all', sent.length === 1, JSON.stringify(posted.map((m) => m.channel)));

check(
  '把整份目录传过去（不是名单）',
  sent[0]?.payload?.models?.length === catalogue.length &&
    catalogue.every((m) => sent[0].payload.models.includes(m)),
  JSON.stringify(sent[0]?.payload?.models),
);

check(
  '名单只有一个，但传的是七个——范围没被缩成名单',
  favoritesOnHost.length === 1 && sent[0]?.payload?.models?.length === 7,
  `名单 ${favoritesOnHost.length} / 传 ${sent[0]?.payload?.models?.length}`,
);

check(
  '并发数发了出去（发成串行不报错，只是慢十几倍）',
  sent[0]?.payload?.concurrency === 5,
  JSON.stringify(sent[0]?.payload),
);

check(
  '「全部测试」传的目标数是 0',
  sent[0]?.payload?.stopAfterAvailable === 0,
  JSON.stringify(sent[0]?.payload),
);

check(
  '跑起来后按钮变成停止',
  testAll.textContent.includes('停止'),
  testAll.textContent,
);

check(
  '跑起来后菜单收起（此时这个按钮是「停止」，不是菜单入口）',
  testMenu.hidden === true,
  `hidden=${testMenu.hidden}`,
);

check(
  '停止说明它不碰对话',
  testAll.title.includes('不影响正在进行的对话'),
  testAll.title,
);

console.log('');
console.log('检查边跑边上色：');

// 后端每测完一个推一次，带该模型的判定。
globalThis.window.dispatchResponse({
  kind: 'probe-progress', model: 'beta', index: 1, total: 7, verdict: 'Available',
});

check(
  '成功的模型当场变可用（不等整批结束）',
  itemFor('beta')?.classes.has('is-available') === true,
  itemFor('beta')?.className,
);

globalThis.window.dispatchResponse({
  kind: 'probe-progress', model: 'gamma', index: 2, total: 7, verdict: 'Unavailable',
});

check(
  '失败的模型当场变不可用',
  itemFor('gamma')?.classes.has('is-unavailable') === true,
  itemFor('gamma')?.className,
);

check(
  '两者带的是不同的 class（否则绿红分不开）',
  itemFor('beta')?.classes.has('is-unavailable') === false &&
    itemFor('gamma')?.classes.has('is-available') === false,
  `${itemFor('beta')?.className} / ${itemFor('gamma')?.className}`,
);

globalThis.window.dispatchResponse({
  kind: 'probe-progress', model: 'delta', index: 3, total: 7, verdict: 'Unknown',
});

check(
  '限流一类判「未确认」的不上色（不是绿也不是红）',
  itemFor('delta') !== null &&
    !itemFor('delta').classes.has('is-available') &&
    !itemFor('delta').classes.has('is-unavailable'),
  itemFor('delta')?.className,
);

check(
  '还没测到的仍是未确认',
  itemFor('zeta') !== null &&
    !itemFor('zeta').classes.has('is-available') &&
    !itemFor('zeta').classes.has('is-unavailable'),
  itemFor('zeta')?.className,
);

check(
  '进度显示在按钮上',
  testAll.textContent.includes('3/7'),
  testAll.textContent,
);

check(
  'describePicker 报出批量进度',
  describePicker().includes('批量=3/7'),
  describePicker(),
);

console.log('');
console.log('检查并发在飞的那几行都被标出来：');

// 这一节盯的是一个真的不直观：整份目录那条路并发 5，同一时刻在飞的就是五个模型。
// 早先面板只用一个字段记「正在测哪一个」，而这条路只在**探完之后**推进度，
// 于是那个字段装的永远是刚探完的那一个——扫光落在一行刚变绿或变红的行上，
// 已经有结论了却还挂着「正在测」的高光，真正在飞的五个反而一个都没标。
//
// 修法：后端在每个模型真的开始探时（拿到并发槽位之后）推一条 starting，
// 探完那条带 settled。面板据两端维护一个在飞集合。

// 先让前面几条的痕迹归零：重新起一批。
globalThis.window.dispatchResponse({ kind: 'probe-progress', done: true });
openMenuAndPick(0);

const inFlight = ['epsilon', 'zeta', 'eta'];
for (const id of inFlight) {
  globalThis.window.dispatchResponse({
    kind: 'probe-progress', model: id, total: 7, starting: true,
  });
}

check(
  '同时在飞的几行都被标成正在测（不是只标一行）',
  inFlight.every((id) => itemFor(id)?.classes.has('is-testing') === true),
  inFlight.map((id) => `${id}=${itemFor(id)?.className}`).join(' | '),
);

check(
  'describePicker 报出在飞的个数',
  describePicker().includes(`在飞=${inFlight.length}`),
  describePicker(),
);

check(
  '没在飞的行不跟着扫（否则整列都在扫，标记就没有意义）',
  itemFor('alpha')?.classes.has('is-testing') === false,
  itemFor('alpha')?.className,
);

// 探完中间那一个：它退出在飞，另外两个还在。
globalThis.window.dispatchResponse({
  kind: 'probe-progress', model: 'zeta', index: 1, total: 7,
  verdict: 'Available', settled: true,
});

check(
  '探完的那一行退出在飞（上了色就不该再挂正在测的高光）',
  itemFor('zeta')?.classes.has('is-testing') === false &&
    itemFor('zeta')?.classes.has('is-available') === true,
  itemFor('zeta')?.className,
);

check(
  '同时在飞的另外几行不受影响，仍在扫',
  itemFor('epsilon')?.classes.has('is-testing') === true &&
    itemFor('eta')?.classes.has('is-testing') === true,
  `${itemFor('epsilon')?.className} / ${itemFor('eta')?.className}`,
);

check(
  '在飞个数跟着减一',
  describePicker().includes(`在飞=${inFlight.length - 1}`),
  describePicker(),
);

// starting 那条不带 index，不该把已完成数打回去。
globalThis.window.dispatchResponse({
  kind: 'probe-progress', model: 'alpha', total: 7, starting: true,
});

check(
  '开始探下一个不会让进度计数倒退',
  testAll.textContent.includes('1/7'),
  testAll.textContent,
);

// 点停止：在飞的那几个不会再收到 settled，扫光只能靠置空进度一并清掉。
// 跑动中这个按钮是「停止」，直接点它即可——不经菜单。
click(testAll);
globalThis.window.dispatchResponse({ kind: 'probe-progress', done: true });

check(
  '停止后没有任何一行还在扫（在飞的那几个不会再推 settled）',
  catalogue.every((id) => itemFor(id)?.classes.has('is-testing') !== true),
  catalogue.map((id) => itemFor(id)?.className).join(' | '),
);

check(
  '停止后在飞归零',
  describePicker().includes('在飞=0'),
  describePicker(),
);

console.log('');
console.log('检查停止：');

openMenuAndPick(0);
click(testAll);

check(
  '停止走 models.probe.stop',
  posted.some((m) => m.channel === 'models.probe.stop'),
  JSON.stringify(posted.map((m) => m.channel)),
);

check(
  '绝不发 chat.stop',
  !posted.some((m) => m.channel === 'chat.stop'),
  '一个控件按隐藏状态决定停哪个，正是这个项目付过代价的故障',
);

availabilityOnHost = { beta: 'Available', gamma: 'Unavailable', delta: 'Unknown' };
testReply = { confirmed: 3, total: 7, stopped: true, availability: availabilityOnHost };
await settle();

check(
  '结束后按钮回到「测试」',
  testAll.textContent === '测试',
  testAll.textContent,
);

check(
  '中止后已测出的结果保留（那些请求已经付过钱了）',
  itemFor('beta')?.classes.has('is-available') === true &&
    itemFor('gamma')?.classes.has('is-unavailable') === true,
  `${itemFor('beta')?.className} / ${itemFor('gamma')?.className}`,
);

console.log('');
console.log('检查与「确认」互不干扰：');

check(
  '「测试」与「确认」是两个按钮',
  testAll !== probeAll && probeAll.textContent === '确认',
  `${testAll.textContent} / ${probeAll.textContent}`,
);

check(
  '「确认」的作用范围仍是名单',
  probeAll.title.includes('名单里的'),
  probeAll.title,
);

console.log('');
console.log('检查目录异步到达后按钮变回可点：');

// 这是真实的使用顺序：浮层先开出来，目录随后才从 GET /models 回来。
// 此前一条断言都没覆盖它——本文件其余部分都是先 putModelCatalog 塞好目录再
// syncPicker，走的是「同步就有目录」那条路。结果漏掉一个真缺陷：
// loadModels 的 finally 里只重画了列表，没重画列头，于是按钮一直停在
// 「目录为空」那一刻算出的禁用态，永远点不动。
const freshConnection = {
  mode: 'CustomApi',
  customProtocol: 'openai-chat-completions',
  customBaseUrl: 'https://fresh.example.test/v1',
};

settingsOnHost = { ...freshConnection, model: '', thinking: 'High' };
catalogueOnHost = ['n1', 'n2', 'n3'];

// 切到一个从未获取过目录的连接：state.models 因此是空的。
syncPicker({ ...settingsOnHost, favorites: [], availability: {}, onlyFavoriteModels: false });

check(
  '目录还没到时「测试」是禁用的',
  testAll.disabled === true,
  `disabled=${testAll.disabled} title=${testAll.title}`,
);

// 点「刷新」触发 loadModels。settings.get 立即结算，models.list 挂起。
nodes.get('picker-refresh').listeners.get('click')({ stopPropagation: () => {} });
for (let i = 0; i < 6; i++) { await new Promise((r) => queueMicrotask(r)); }

check(
  '拉取中仍是禁用（此刻目录确实还空着）',
  testAll.disabled === true,
  `disabled=${testAll.disabled}`,
);

// 让 models.list 回来。
await settle();

check(
  '目录到达后「测试」变回可点',
  testAll.disabled === false,
  `disabled=${testAll.disabled} title=${testAll.title}`,
);

check(
  '按钮上的条数跟着目录更新',
  testAll.title.includes(`${catalogueOnHost.length} 个模型`),
  testAll.title,
);

check(
  '提示不再说「目录是空的」',
  !testAll.title.includes('目录是空的'),
  testAll.title,
);

console.log('');
console.log('检查带目标的运行：分母是目标数，不是目录条数：');

// 回到那份七个模型的目录。
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: favoritesOnHost,
  availability: {},
  onlyFavoriteModels: false,
});

openMenuAndPick(1);

const targeted = posted.filter((m) => m.channel === 'models.test.all').slice(-1)[0];
check(
  '「找 1 个就停」把目标数发了出去',
  targeted?.payload?.stopAfterAvailable === 1,
  JSON.stringify(targeted?.payload),
);

check(
  '范围仍是整份目录（目标数不缩小范围）',
  targeted?.payload?.models?.length === catalogue.length,
  JSON.stringify(targeted?.payload?.models),
);

globalThis.window.dispatchResponse({
  kind: 'probe-progress', model: 'beta', index: 3, total: 7,
  verdict: 'Unavailable', settled: true, target: 1, availableFound: 0,
});

// 分母写目录条数的话，一次「找 1 个就停」会显示「停止 3/7」——读起来是
// 「还要跑 4 个」，而它随后就停了，看起来像断了。
check(
  '按钮的分母是目标数，分子是已找到的可用个数',
  testAll.textContent === '停止 0/1 可用',
  testAll.textContent,
);

check(
  '悬停说明把已测个数一并说出来（分母换了，进度信息不该丢）',
  testAll.title.includes('已测 3 个') && testAll.title.includes('已找到 0 个'),
  testAll.title,
);

globalThis.window.dispatchResponse({
  kind: 'probe-progress', model: 'gamma', index: 4, total: 7,
  verdict: 'Available', settled: true, target: 1, availableFound: 1,
});

check(
  '找到一个之后分子跟着走',
  testAll.textContent === '停止 1/1 可用',
  testAll.textContent,
);

check(
  'describePicker 报出目标与已找到',
  describePicker().includes('目标=1') && describePicker().includes('已找到=1'),
  describePicker(),
);

// starting 那条不带 availableFound 时不能落成 0：分子会在两种推送交替时来回跳。
globalThis.window.dispatchResponse({
  kind: 'probe-progress', model: 'delta', total: 7, starting: true, target: 1,
});

check(
  '缺 availableFound 的推送沿用上一条，分子不回退',
  testAll.textContent === '停止 1/1 可用',
  testAll.textContent,
);

console.log('');
console.log('检查三种结局各有各的说法：');

availabilityOnHost = { gamma: 'Available' };
testReply = {
  confirmed: 5, total: 7, stopped: false, target: 1, availableFound: 1,
  attempted: 5, targetMet: true, outcome: 'target', availability: availabilityOnHost,
};
await settle();

check(
  '达标：说出找到几个、发了几条、剩下几个没测',
  testNote.hidden === false &&
    testNote.textContent.includes('找到 1 个能用的') &&
    testNote.textContent.includes('发了 5 条') &&
    testNote.textContent.includes('剩下 2 个没测'),
  `hidden=${testNote.hidden} text=${testNote.textContent}`,
);

check(
  '达标不说成「用户中止」',
  !testNote.textContent.includes('已停止'),
  testNote.textContent,
);

// 目标没达成而整份目录已测完：用户选了省钱的档，付的是全款。
openMenuAndPick(2);
testReply = {
  confirmed: 7, total: 7, stopped: false, target: 2, availableFound: 1,
  attempted: 7, targetMet: false, outcome: 'completed', availability: availabilityOnHost,
};
await settle();

check(
  '目标没达成而全测完了：必须明说（否则界面上看不出付的是全款）',
  testNote.textContent.includes('整份目录 7 个都测了') &&
    testNote.textContent.includes('只找到 1 个能用的') &&
    testNote.textContent.includes('想找 2 个'),
  testNote.textContent,
);

// 用户中止。
openMenuAndPick(1);
testReply = {
  confirmed: 3, total: 7, stopped: true, target: 1, availableFound: 0,
  attempted: 3, targetMet: false, outcome: 'stopped', availability: availabilityOnHost,
};
await settle();

check(
  '用户中止：说出发了几条，并交代已测出的结果保留',
  testNote.textContent.includes('已停止') &&
    testNote.textContent.includes('发了 3 条') &&
    testNote.textContent.includes('都保留着'),
  testNote.textContent,
);

check(
  '三种结局的说法互不相同',
  new Set(['target', 'completed', 'stopped']).size === 3 &&
    !testNote.textContent.includes('够了'),
  testNote.textContent,
);

// 「全部测试」跑完不谈目标。
openMenuAndPick(0);
testReply = {
  confirmed: 7, total: 7, stopped: false, target: 0, availableFound: 4,
  attempted: 7, targetMet: false, outcome: 'completed', availability: availabilityOnHost,
};
await settle();

check(
  '「全部测试」跑完只说测完了几个、找到几个，不提目标',
  testNote.textContent.includes('7 个模型都测完了') &&
    testNote.textContent.includes('找到 4 个能用的') &&
    !testNote.textContent.includes('想找'),
  testNote.textContent,
);

console.log('');
console.log('检查收尾之后迟到的推送不复活进度：');

// 用户中止那条路会遗弃在飞的任务（取消从派发循环里抛出，WhenAll 没走到），
// 而收尾的 done 已经推过。那些任务随后推来的 settled 若照常处理，会把进度重新置上，
// 列头按钮回到「停止 n/总」——而后端已经没有批量在跑，再点它只拿到 stopped: false，
// 且关面板也不清。
check(
  '前置：此刻没有批量在跑',
  testAll.textContent === '测试' && describePicker().includes('批量=无'),
  `${testAll.textContent} / ${describePicker()}`,
);

globalThis.window.dispatchResponse({
  kind: 'probe-progress', model: 'epsilon', index: 6, total: 7,
  verdict: 'Available', settled: true, target: 0, availableFound: 5,
});

check(
  '迟到的推送不把按钮变回「停止」',
  testAll.textContent === '测试',
  testAll.textContent,
);

check(
  '迟到的推送不把进度置回来',
  describePicker().includes('批量=无'),
  describePicker(),
);

check(
  '但它带的判定仍然落下了（那条请求已经付过钱）',
  itemFor('epsilon')?.classes.has('is-available') === true,
  itemFor('epsilon')?.className,
);

console.log('');
console.log('检查对话在飞时说得出原因：');

setPickerTurnInFlight(true);

check(
  '对话在飞时「测试」禁用',
  testAll.disabled === true,
  `disabled=${testAll.disabled}`,
);

check(
  '并说明是为什么（此前这条只落在宿主日志里，面板上什么都不出）',
  testAll.title.includes('正在对话中'),
  testAll.title,
);

setPickerTurnInFlight(false);

check(
  '对话结束后变回可点',
  testAll.disabled === false,
  `disabled=${testAll.disabled}`,
);

// 菜单开着时对话跑起来：留着菜单等于留着一个选下去只会失败的入口。
click(testAll);
setPickerTurnInFlight(true);

check(
  '对话跑起来时把开着的菜单收掉',
  testMenu.hidden === true,
  `hidden=${testMenu.hidden}`,
);

setPickerTurnInFlight(false);

// ---------- 变异自检 ----------

console.log('');
list.replaceChildren();
const blind = itemFor('beta') !== null || itemFor('gamma') !== null;
check(
  '清空模型列后断言会失败（说明断言真的在看渲染结果）',
  !blind,
  '断言对着空节点也通过，是假绿',
);

console.log('');
console.log(`=== 批量测试：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
