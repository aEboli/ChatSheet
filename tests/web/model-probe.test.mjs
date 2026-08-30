// 按需确认在选择器里的行为。
//
// 重点：「正在确认」必须是能看见的独立态（否则慢网关与「点了没反应」分不开），
// 「试一下」只对没有判定的模型出现，批量的停止不能和对话的停止混在一起。
//
// 假 DOM 照 capability-fallback.test.mjs：className 是 classes 集合的活访问器。
// 末尾带变异自检。

const posted = [];
let probeReply = null;
let bulkReply = null;

globalThis.window = {
  chrome: {
    webview: {
      postMessage: (message) => {
        posted.push(message);
        const id = message.id;

        // 回复的形状必须与 bridge.js 一致：kind: 'response'，正文在 data。
        // 写成 payload 的话 request() 永远拿不到内容，而 finally 里的收尾
        // 也就不会跑——那样「正在确认」会一直挂着，断言反而看起来像代码错了。
        //
        // 载荷惰性求值：测试会在 postMessage 之后才设置本次要回什么。
        const deliver = () => {
          let data = {};
          if (message.channel === 'models.favorites') {
            data = { favorites: favoritesOnHost, availability: availabilityOnHost };
          } else if (message.channel === 'models.probe') {
            data = probeReply ?? { availability: availabilityOnHost };
          } else if (message.channel === 'models.probe.bulk') {
            data = bulkReply ?? { confirmed: 0, total: 0, availability: availabilityOnHost };
          }

          globalThis.window.dispatchResponse?.({ kind: 'response', id, ok: true, data });
        };

        // 探测的回复要能延迟结算：断言「正在确认」这个中间态需要一个
        // 「请求已发、回复未到」的窗口。
        if (message.channel === 'models.probe' || message.channel === 'models.probe.bulk') {
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
  innerWidth: 420,
};

const pendingReplies = [];
let favoritesOnHost = [];
let availabilityOnHost = {};

/** 结算所有挂起的探测回复。 */
async function settle() {
  while (pendingReplies.length > 0) {
    pendingReplies.shift()();
    await new Promise((r) => queueMicrotask(r));
  }
  await new Promise((r) => queueMicrotask(r));
  await new Promise((r) => queueMicrotask(r));
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

const ids = ['picker-models', 'picker-thinkings', 'picker-model', 'picker-thinking',
  'picker-trigger', 'picker-pop', 'picker-refresh', 'picker-only-favorites',
  'picker-probe-all', 'picker-manual', 'picker-manual-input', 'model-picker'];
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
const probeAll = nodes.get('picker-probe-all');

function descend(node, out = []) {
  for (const kid of node.children ?? []) {
    if (!kid || typeof kid !== 'object') { continue; }
    out.push(kid);
    descend(kid, out);
  }
  return out;
}

function rowFor(id) {
  const name = descend(list).find(
    (n) => n.classes.has('picker-item-name') && n.textContent === id,
  );
  return name?.parent?.parent?.parent ?? null;
}

function probeButtonFor(id) {
  return rowFor(id)?.children.find((n) => n.classes.has('picker-probe')) ?? null;
}

function dotFor(id) {
  return descend(list).find(
    (n) => n.classes.has('picker-availability-dot') &&
      n.parent?.children.some((c) => c.classes.has('picker-item-name') && c.textContent === id),
  ) ?? null;
}

function hintFor(id) {
  const row = rowFor(id);
  const item = row?.children.find((n) => n.classes.has('picker-item'));
  return item?.children.find((n) => n.classes.has('picker-item-hint'))?.textContent ?? '';
}

const connection = {
  mode: 'CustomApi',
  customProtocol: 'openai-chat-completions',
  customBaseUrl: 'https://gw.example.test/v1',
};

initPicker(() => {});
putModelCatalog(connection, ['alpha', 'beta', 'gamma']);

console.log('检查按需确认：');

availabilityOnHost = { alpha: 'Available', beta: 'Unavailable' };
favoritesOnHost = ['gamma'];
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: favoritesOnHost,
  availability: availabilityOnHost,
  onlyFavoriteModels: false,
});

// ---------- 「试一下」只对没有判定的模型出现 ----------

check('已判为可用的行没有「试一下」', probeButtonFor('alpha') === null, '');
check('已判为不可用的行没有「试一下」', probeButtonFor('beta') === null, '');
check(
  '未确认的行有「试一下」',
  probeButtonFor('gamma') !== null && probeButtonFor('gamma').textContent === '试一下',
  probeButtonFor('gamma')?.textContent,
);

// ---------- 打开选择器不发探测 ----------

check(
  '渲染与同步都不发探测请求',
  posted.filter((m) => m.channel === 'models.probe').length === 0,
  JSON.stringify(posted.map((m) => m.channel)),
);

// ---------- 正在确认是可见的独立态 ----------

probeButtonFor('gamma').listeners.get('click')({ stopPropagation: () => {} });

check(
  '点了之后状态点进入正在确认态',
  dotFor('gamma')?.classes.has('is-probing') === true,
  dotFor('gamma')?.className,
);

check(
  '正在确认时行上有说明文字',
  hintFor('gamma').includes('正在确认'),
  hintFor('gamma'),
);

check(
  '正在确认时不带任何结论 class',
  dotFor('gamma') !== null &&
    !dotFor('gamma').classes.has('is-ok') &&
    !dotFor('gamma').classes.has('is-error'),
  dotFor('gamma')?.className,
);

check(
  '正在确认时该行不再显示「试一下」',
  probeButtonFor('gamma') === null,
  '否则会被点第二次',
);

check(
  'describePicker 报出正在确认的数量',
  describePicker().includes('正在确认=1'),
  describePicker(),
);

check(
  '确认只发了一条请求',
  posted.filter((m) => m.channel === 'models.probe').length === 1,
  JSON.stringify(posted.filter((m) => m.channel === 'models.probe')),
);

// ---------- 结论到达后替换掉正在确认 ----------

availabilityOnHost = { alpha: 'Available', beta: 'Unavailable', gamma: 'Available' };
probeReply = { model: 'gamma', verdict: 'Available', availability: availabilityOnHost };
await settle();

check(
  '结论到达后离开正在确认态',
  dotFor('gamma')?.classes.has('is-probing') === false,
  dotFor('gamma')?.className,
);

check(
  '结论到达后显示为可用',
  dotFor('gamma')?.classes.has('is-ok') === true,
  dotFor('gamma')?.className,
);

check(
  '有结论后不再显示「试一下」',
  probeButtonFor('gamma') === null,
  '',
);

check(
  'describePicker 的正在确认归零',
  describePicker().includes('正在确认=0'),
  describePicker(),
);

// ---------- 批量：名单为空时禁用 ----------

favoritesOnHost = [];
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: [],
  availability: availabilityOnHost,
  onlyFavoriteModels: false,
});

check('名单为空时「全部确认」禁用', probeAll.disabled === true, '');
check(
  '禁用时说明为什么',
  probeAll.title.includes('名单是空的'),
  probeAll.title,
);

// ---------- 批量：有名单时可用，跑起来变成停止 ----------

favoritesOnHost = ['gamma', 'beta'];
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: favoritesOnHost,
  availability: availabilityOnHost,
  onlyFavoriteModels: false,
});

check('有名单时「全部确认」可用', probeAll.disabled === false, probeAll.title);

probeAll.listeners.get('click')({ stopPropagation: () => {} });

check(
  '批量跑起来后按钮变成停止',
  probeAll.textContent.includes('停止'),
  probeAll.textContent,
);

check(
  '停止按钮说明它不碰对话',
  probeAll.title.includes('不影响正在进行的对话'),
  probeAll.title,
);

check(
  '批量走自己的通道',
  posted.some((m) => m.channel === 'models.probe.bulk'),
  JSON.stringify(posted.map((m) => m.channel)),
);

// 进度推送。走的是 on('probe-progress') 那条订阅，不是请求回复。
globalThis.window.dispatchResponse({
  kind: 'probe-progress', model: 'gamma', index: 1, total: 2,
});

check(
  '进度显示在按钮上',
  probeAll.textContent.includes('1/2'),
  probeAll.textContent,
);

check(
  'describePicker 报出批量进度',
  describePicker().includes('批量=1/2'),
  describePicker(),
);

// 点停止：走停止通道，且绝不是 chat.stop。
probeAll.listeners.get('click')({ stopPropagation: () => {} });

check(
  '停止走 models.probe.stop',
  posted.some((m) => m.channel === 'models.probe.stop'),
  JSON.stringify(posted.map((m) => m.channel)),
);

check(
  '停批量绝不发 chat.stop',
  !posted.some((m) => m.channel === 'chat.stop'),
  '一个控件按隐藏状态决定停哪个，正是这个项目付过代价的故障',
);

bulkReply = { confirmed: 1, total: 2, stopped: true, availability: availabilityOnHost };
await settle();

check(
  '批量结束后按钮回到待发起态',
  probeAll.textContent === '确认',
  probeAll.textContent,
);
// 文字缩短了，作用范围不能跟着丢——它移到了悬停说明里。
check(
  '悬停说明讲清作用范围是名单里的模型',
  probeAll.title.includes('名单里的'),
  probeAll.title,
);

// ---------- 变异自检 ----------

list.replaceChildren();
const blind = probeButtonFor('gamma') !== null || dotFor('alpha') !== null;
check(
  '清空模型列后断言会失败（说明断言真的在看渲染结果）',
  !blind,
  '断言对着空节点也通过，是假绿',
);

console.log('');
console.log(`=== 按需确认：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
