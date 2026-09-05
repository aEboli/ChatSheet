// 常用名单与三态在选择器里的行为。
//
// 重点全在「开关不会把人锁在外面」这一条上：名单为空、名单全部不在目录里、
// 当前模型不在名单里，三种情形下筛选都不该把可用的模型收走。
//
// 假 DOM 照 capability-fallback.test.mjs：className 是 classes 集合的活访问器，
// append 摘走原父节点。做虚了断言会对着空节点通过
// （见 chatsheet-fake-dom-silently-passes），所以本文件末尾带一段变异自检。

const posted = [];

globalThis.window = {
  chrome: {
    webview: {
      postMessage: (message) => {
        posted.push(message);
        const id = message.id;
        queueMicrotask(() => {
          // 形状必须与 bridge.js 一致：kind: 'response'，正文在 data。
          // 写错的话 request() 拿不到内容，回调里的收尾也不会跑。
          const data = message.channel === 'models.favorites'
            ? { favorites: favoritesOnHost, availability: availabilityOnHost }
            : {};
          globalThis.window.dispatchResponse?.({ kind: 'response', id, ok: true, data });
        });
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

let favoritesOnHost = [];
let availabilityOnHost = {};

function makeNode(tag = 'div') {
  const node = {
    tag,
    textContent: '',
    title: '',
    value: '',
    type: '',
    hidden: true,
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
  'picker-manual', 'picker-manual-input', 'model-picker'];
const nodes = new Map(ids.map((id) => [id, makeNode()]));

globalThis.document = {
  getElementById: (id) => nodes.get(id) ?? null,
  querySelector: () => null,
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: (tag) => makeNode(tag),
};

const { initPicker, syncPicker, describePicker } = await import('../../src/web/scripts/picker.js');
const { putModelCatalog, invalidateModelCatalog } = await import('../../src/web/scripts/model-catalog.js');

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
const headToggle = nodes.get('picker-only-favorites');

function descend(node, out = []) {
  for (const kid of node.children ?? []) {
    if (!kid || typeof kid !== 'object') { continue; }
    out.push(kid);
    descend(kid, out);
  }
  return out;
}

/** 模型列里可见的模型 ID，按渲染顺序。 */
function shownModels() {
  return descend(list)
    .filter((n) => n.classes.has('picker-item-name'))
    .map((n) => n.textContent);
}

function rowFor(id) {
  const name = descend(list).find(
    (n) => n.classes.has('picker-item-name') && n.textContent === id,
  );
  // .picker-item-name → .picker-item-head → .picker-item → .picker-row
  return name?.parent?.parent?.parent ?? null;
}

function starFor(id) {
  return rowFor(id)?.children.find((n) => n.classes.has('picker-star')) ?? null;
}

function dotFor(id) {
  return descend(list).find(
    (n) => n.classes.has('picker-availability-dot') &&
      n.parent?.children.some((c) => c.classes.has('picker-item-name') && c.textContent === id),
  ) ?? null;
}

const connection = {
  mode: 'CustomApi',
  customProtocol: 'openai-chat-completions',
  customBaseUrl: 'https://gw.example.test/v1',
};

const catalog = ['alpha', 'beta', 'gamma', 'delta'];

initPicker(() => {});
putModelCatalog(connection, catalog);

console.log('检查常用名单与三态：');

// ---------- 排序无条件生效 ----------

favoritesOnHost = ['gamma'];
availabilityOnHost = { alpha: 'Available', beta: 'Unavailable' };
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: favoritesOnHost,
  availability: availabilityOnHost,
  onlyFavoriteModels: false,
});

check(
  '名单里的模型排在最前面',
  shownModels()[0] === 'gamma',
  shownModels().join('、'),
);

check(
  '其余模型保持后端给的顺序',
  shownModels().slice(1).join(',') === 'alpha,beta,delta',
  shownModels().join('、'),
);

check('开关关时不过滤', shownModels().length === 4, shownModels().join('、'));

// ---------- 三态标注 ----------

check(
  '判为可用的行带 is-ok 状态点',
  dotFor('alpha')?.classes.has('is-ok') === true,
  dotFor('alpha')?.className,
);

check(
  '判为不可用的行带 is-error 状态点',
  dotFor('beta')?.classes.has('is-error') === true,
  dotFor('beta')?.className,
);

check(
  '未确认的行状态点不带任何结论 class',
  dotFor('delta') !== null &&
    !dotFor('delta').classes.has('is-ok') &&
    !dotFor('delta').classes.has('is-error'),
  dotFor('delta')?.className,
);

check(
  '不可用的模型仍然列在选择器里',
  shownModels().includes('beta'),
  shownModels().join('、'),
);

check(
  '状态点不改变行的顺序',
  shownModels().join(',') === 'gamma,alpha,beta,delta',
  shownModels().join('、'),
);

check(
  '模型名的文字仍是纯模型 ID',
  descend(list).some((n) => n.classes.has('picker-item-name') && n.textContent === 'alpha'),
  '宿主靠这个全等匹配来选中',
);

// ---------- 星标 ----------

check('名单里的模型星标是实心', starFor('gamma')?.textContent === '★', starFor('gamma')?.textContent);
check('不在名单里的模型星标是空心', starFor('alpha')?.textContent === '☆', starFor('alpha')?.textContent);

// ---------- 开关：正常筛选 ----------

favoritesOnHost = ['gamma', 'delta'];
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: favoritesOnHost,
  availability: availabilityOnHost,
  onlyFavoriteModels: true,
});

check(
  '开关开时只显示名单里的模型（外加当前模型）',
  shownModels().join(',') === 'gamma,delta,alpha',
  shownModels().join('、'),
);

check(
  '当前模型不在名单里也可见',
  shownModels().includes('alpha'),
  shownModels().join('、'),
);

check(
  '被收起时报出数量并给出显示全部的出口',
  descend(list).some((n) => n.classes.has('picker-hidden-count') && n.textContent.includes('1')) &&
    descend(list).some((n) => n.classes.has('picker-hidden-show')),
  descend(list).filter((n) => n.classes.has('picker-hidden-count')).map((n) => n.textContent).join('|'),
);

check('列头开关标为已开', headToggle.classes.has('is-on'), headToggle.className);

// ---------- 阀门一：名单为空 ----------

favoritesOnHost = [];
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: [],
  availability: availabilityOnHost,
  onlyFavoriteModels: true,
});

check(
  '开关开而名单为空时显示完整目录',
  shownModels().length === 4,
  shownModels().join('、'),
);

check(
  '名单为空时不显示「已收起」说明',
  !descend(list).some((n) => n.classes.has('picker-hidden-count')),
  '空列表会让人以为网关掉了模型',
);

// ---------- 阀门二：名单全部不在目录里 ----------

favoritesOnHost = ['已下架的模型', '打错的-id'];
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: favoritesOnHost,
  availability: availabilityOnHost,
  onlyFavoriteModels: true,
});

check(
  '开关开而名单里没有一个出现在目录里时显示完整目录',
  shownModels().length === 4,
  shownModels().join('、'),
);

// ---------- 大小写 ----------

favoritesOnHost = ['ALPHA'];
syncPicker({
  ...connection,
  model: 'beta',
  thinking: 'High',
  favorites: favoritesOnHost,
  availability: { ALPHA: 'Available' },
  onlyFavoriteModels: true,
});

check(
  '名单项与目录大小写不同也认得出',
  shownModels()[0] === 'alpha',
  shownModels().join('、'),
);

check(
  '大小写不同的判定也落在同一行',
  dotFor('alpha')?.classes.has('is-ok') === true,
  dotFor('alpha')?.className,
);

check(
  '大小写不同的名单项星标是实心',
  starFor('alpha')?.textContent === '★',
  starFor('alpha')?.textContent,
);

// ---------- 第一次标星不当场收起 ----------

favoritesOnHost = [];
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: [],
  availability: {},
  onlyFavoriteModels: true,
});

starFor('beta')?.listeners.get('click')?.({ stopPropagation: () => {} });

check(
  '名单刚从空变成一项时仍显示完整目录',
  shownModels().length === 4,
  shownModels().join('、'),
);

// 「开着但本次没在筛」这件事仍然必须让用户看见，只是载体换了：
// 此前靠加长按钮文字（「只看名单（本次先不收起）」），那有一百三十来像素，
// 会把列头挤到折行；现在文字恒为「名单」，这一态由 class 与悬停说明承担。
check(
  '此时列头把「本次没有收起」标出来（class）',
  headToggle.classes.has('is-suspended'),
  headToggle.className,
);
check(
  '此时列头的悬停说明讲清为什么没收起',
  headToggle.title.includes('本次先不收起'),
  headToggle.title,
);
check(
  '按钮文字不因此变长（列头四个元素挤在一行）',
  headToggle.textContent === '名单',
  headToggle.textContent,
);

// ---------- 切连接清理本次浮层的临时状态 ----------

const otherConnection = { ...connection, customBaseUrl: 'https://other.example.test/v1' };
invalidateModelCatalog(otherConnection);
putModelCatalog(otherConnection, ['solo-a', 'solo-b']);
favoritesOnHost = ['solo-a'];
syncPicker({
  ...otherConnection,
  model: 'solo-a',
  thinking: 'High',
  favorites: favoritesOnHost,
  availability: {},
  onlyFavoriteModels: true,
});

check(
  '换连接后按新连接的名单筛选',
  shownModels().join(',') === 'solo-a',
  shownModels().join('、'),
);

check(
  '换连接后上一个连接的三态不残留',
  describePicker().includes('当前判定=Unknown'),
  describePicker(),
);

// ---------- describePicker 报出新状态 ----------

check(
  'describePicker 报出名单、筛选与判定',
  ['只看名单=', '名单项=', '可见=', '收起=', '当前判定='].every((k) => describePicker().includes(k)),
  describePicker(),
);

// ---------- 批量途中落判定：Unknown 不覆盖已有结论 ----------
//
// 这条规则必须与加载项侧 ModelAvailability.Record 一致（那边遇 Unknown 直接 return）。
// 两侧不一致的后果是可见的：上次测出能用的模型这次被限流判 Unknown，
// 面板当场把绿抹掉，而整批结束时权威快照仍说「能用」，绿又回来——
// 一行在批量途中掉了色又找回来，比从头到尾不变色更难读。
//
// 直接测这个函数而不经过 DOM：它是唯一的落点，而颜色由 verdictOf 读同一份 Map。

console.log('');
console.log('检查批量途中落判定的覆盖规则：');

const favoritesModule = await import('../../src/web/scripts/model-favorites.js');
const { recordVerdictLocally, verdictOf, adoptFavorites, AVAILABILITY } = favoritesModule;

adoptFavorites('verdict-rule-key', {
  favorites: [],
  availability: { 'had-green': 'Available', 'had-red': 'Unavailable' },
  onlyFavoriteModels: false,
});

recordVerdictLocally('had-green', AVAILABILITY.unknown);
check(
  '「未确认」不抹掉已经测出的「能用」',
  verdictOf('had-green') === AVAILABILITY.available,
  verdictOf('had-green'),
);

recordVerdictLocally('had-red', AVAILABILITY.unknown);
check(
  '「未确认」也不抹掉已经测出的「不可用」',
  verdictOf('had-red') === AVAILABILITY.unavailable,
  verdictOf('had-red'),
);

recordVerdictLocally('never-probed', AVAILABILITY.unknown);
check(
  '本来就没有判定的，落 Unknown 之后仍是未确认（不是第四个状态）',
  verdictOf('never-probed') === AVAILABILITY.unknown,
  verdictOf('never-probed'),
);

recordVerdictLocally('had-green', AVAILABILITY.unavailable);
check(
  '真结论照旧覆盖：能用改判不可用要生效（判定不得比证据活得久）',
  verdictOf('had-green') === AVAILABILITY.unavailable,
  verdictOf('had-green'),
);

recordVerdictLocally('had-red', AVAILABILITY.available);
check(
  '反向也生效：不可用改判能用',
  verdictOf('had-red') === AVAILABILITY.available,
  verdictOf('had-red'),
);

// 复原本文件其余部分的前提：上面换了投影键。
syncPicker({
  ...connection,
  model: 'alpha',
  thinking: 'High',
  favorites: [],
  availability: {},
  onlyFavoriteModels: false,
});

// ---------- 变异自检 ----------
//
// 断言必须真的在看渲染结果。这里故意清空模型列，若断言仍然通过，
// 说明它们对着空节点也成立——那样整份文件都是假绿。
list.replaceChildren();
const blind = shownModels().length === 4 || starFor('alpha') !== null || dotFor('alpha') !== null;
check(
  '清空模型列后断言会失败（说明断言真的在看渲染结果）',
  !blind,
  '断言对着空节点也通过，是假绿',
);

console.log('');
console.log(`=== 常用名单与三态：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
