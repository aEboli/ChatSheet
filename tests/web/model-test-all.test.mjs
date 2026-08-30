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
  'model-picker'];
const nodes = new Map(ids.map((id) => [id, makeNode()]));

globalThis.document = {
  getElementById: (id) => nodes.get(id) ?? null,
  querySelector: () => null,
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: (tag) => makeNode(tag),
};

const { initPicker, syncPicker, describePicker } = await import('../../src/web/scripts/picker.js');
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
  '悬停说明报出会发多少条请求',
  testAll.title.includes(`${catalogue.length} 个模型`) &&
    testAll.title.includes(`${catalogue.length} 条计费请求`),
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

testAll.listeners.get('click')({ stopPropagation: () => {} });

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
  '跑起来后按钮变成停止',
  testAll.textContent.includes('停止'),
  testAll.textContent,
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
console.log('检查停止：');

testAll.listeners.get('click')({ stopPropagation: () => {} });

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
