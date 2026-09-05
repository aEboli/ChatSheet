// 常用名单与可用性三态的面板侧投影。
//
// 一律以后端下发的 payload 为权威：名单落在加载项那边的文件里，三态记在加载项的
// 内存里，面板只是把它们画出来。本地键只用来判断「这份投影属于哪个连接」，
// 不参与判断内容对不对。
//
// 为什么需要键：切换连接后旧投影必须立即失效，否则会拿上一个网关的名单去筛
// 当前网关的目录——那正是筛选最容易把人锁在外面的方式。

/** 三态。缺席即「未确认」，不是第四个状态，只是还没有证据。 */
export const AVAILABILITY = {
  available: 'Available',
  unavailable: 'Unavailable',
  unknown: 'Unknown',
};

let state = {
  key: null,
  favorites: [],
  availability: new Map(),
  onlyFavorites: false,
  // 正在确认的模型（折叠后的 ID）。这是第四个显示态，不是三态之一：
  // 没有它，慢网关和「点了没反应」分不开。
  probing: new Set(),
  // 批量确认的进度。null 表示没在跑。
  bulk: null,
  // 批量此刻真的在飞的模型（折叠后的 ID）。
  //
  // 必须是集合，不能是单个模型：批量测试并发 5，同一时刻在飞的就是五个。
  // 早先这里是 bulk.model 一个字符串，而整份目录那条路只在「探完之后」推进度，
  // 于是那个字段装的永远是刚探完的那一个——扫光落在一行刚变绿或变红的行上，
  // 真正在飞的五个反而一个都没标。
  testing: new Set(),
};

function fold(id) {
  // 目录侧用 OrdinalIgnoreCase 去重，名单与三态都必须同口径，
  // 否则手填的 GPT-4O 与目录里的 gpt-4o 会各占一行、各查一份判定。
  return String(id ?? '').trim().toLowerCase();
}

/**
 * 采纳一次后端下发。
 *
 * key 由调用方按当前连接算出（复用 modelCatalogKey）。键不同即换了连接，
 * 整份投影替换而不是合并——合并会让上一个连接的三态残留在当前视图里。
 */
export function adoptFavorites(key, settings = {}) {
  const favorites = [];
  const seen = new Set();
  for (const id of settings.favorites ?? []) {
    const trimmed = String(id ?? '').trim();
    const folded = fold(trimmed);
    if (trimmed && !seen.has(folded)) {
      seen.add(folded);
      favorites.push(trimmed);
    }
  }

  const availability = new Map();
  for (const [id, verdict] of Object.entries(settings.availability ?? {})) {
    const folded = fold(id);
    if (folded) { availability.set(folded, verdict); }
  }

  // 换连接时清掉正在确认与批量进度：那些是上一个连接的事。
  // 同一个连接的重复下发不清，否则每次切页都会把「正在确认」抹掉。
  const switched = state.key !== null && state.key !== key;

  state = {
    key,
    favorites,
    availability,
    onlyFavorites: Boolean(settings.onlyFavoriteModels),
    probing: switched ? new Set() : state.probing,
    bulk: switched ? null : state.bulk,
    testing: switched ? new Set() : state.testing,
  };
}

/** 某个模型是否正在确认。 */
export function isProbing(id) {
  return state.probing.has(fold(id));
}

export function markProbing(id, on) {
  const folded = fold(id);
  if (on) { state.probing.add(folded); } else { state.probing.delete(folded); }
}

export function anyProbing() {
  return state.probing.size > 0;
}

/** 批量进度。null 表示没在跑。 */
export function bulkProgress() {
  return state.bulk;
}

/**
 * 批量此刻是不是真的在探这个模型。
 *
 * 判定放在这里而不是调用方自己比对：模型 ID 的大小写折叠规则（fold）是本模块的
 * 私有约定，`isProbing` 也走它。调用方直接拿推送里的 ID 跟目录里的比字符串，
 * 会在网关回报的大小写与目录不一致时静默失配——那时行上什么标记都不出现，
 * 而批量看起来就像没在动。
 *
 * 与 isProbing 是两回事：那个是用户点「试一下」逐个确认时置上的，批量整批只
 * 占一次闸门、不逐个置 probing。所以「正在探这一个」只有这里能答。
 */
export function isBulkTesting(id) {
  if (!id) { return false; }
  return state.testing.has(fold(id));
}

/** 此刻在飞的个数。并发 5 时批量测试正常应当是 5，串行批量确认是 1。 */
export function bulkTestingCount() {
  return state.testing.size;
}

/**
 * 标记某个模型开始探 / 探完了。
 *
 * 两端都由后端推送驱动：开始探时推一条 starting，探完推一条 settled。
 * 面板不自己猜——猜的话并发下会与真实在飞的那几个错开。
 */
export function markBulkTesting(id, on) {
  const folded = fold(id);
  if (!folded) { return; }
  if (on) { state.testing.add(folded); } else { state.testing.delete(folded); }
}

export function setBulkProgress(progress) {
  state.bulk = progress;

  // 批量结束（置 null）时把在飞集合一并清空。
  //
  // 必须在这里清，不能只靠 settled 推送逐个摘：用户中途点停止时，
  // 已经在飞的那几个不会再推 settled（后端直接跳出循环），
  // 那几行会一直挂着扫光——批量早停了，列表里还有几行在扫。
  if (progress === null) { state.testing.clear(); }
}

/** 当前投影所属的连接键。 */
export function favoritesKey() {
  return state.key;
}

export function favorites() {
  return [...state.favorites];
}

export function isFavorite(id) {
  const folded = fold(id);
  return state.favorites.some((m) => fold(m) === folded);
}

/** 某个模型的三态。没有判定时返回 unknown——缺席就是「未确认」。 */
export function verdictOf(id) {
  return state.availability.get(fold(id)) ?? AVAILABILITY.unknown;
}

/**
 * 记下一条判定，供批量测试边跑边上色。
 *
 * 为什么需要它：批量的权威结果要等整批结束才随回复带回来，而几十个模型跑一遍要好一阵。
 * 只在结束时上色的话，中途整列都是「未确认」，看起来像没在动。后端每测完一个就推一次
 * 进度并带上该模型的判定，这个函数把它落到本地投影上。
 *
 * 仍以后端下发为权威：整批结束时 adoptFavorites 会整份替换，这里写进去的只是提前显示。
 */
export function recordVerdictLocally(id, verdict) {
  const folded = fold(id);
  if (!folded || !verdict) { return; }

  // 「未确认」不覆盖已有判定，与加载项侧 ModelAvailability.Record 同一条规则。
  //
  // Unknown 不是结论，只是这一次没拿到答案（限流一类花了钱没拿到答案）。
  // 让它覆盖的话：上次测出能用的模型这次被限流，行上的绿会当场消失，
  // 而整批结束时权威快照仍说「能用」，绿又回来——一行在批量途中掉了色又找回来，
  // 比从头到尾不变色更难读，而这正是「批量探测不够直观」要修的东西。
  if (verdict === AVAILABILITY.unknown && state.availability.has(folded)) {
    return;
  }

  state.availability.set(folded, verdict);
}

export function onlyFavorites() {
  return state.onlyFavorites;
}

export function setOnlyFavorites(value) {
  state.onlyFavorites = Boolean(value);
}

/** 本地先行更新名单，供点击后立刻重绘；权威值仍由后端回传覆盖。 */
export function toggleFavoriteLocally(id) {
  const trimmed = String(id ?? '').trim();
  if (!trimmed) { return false; }

  const folded = fold(trimmed);
  const index = state.favorites.findIndex((m) => fold(m) === folded);
  if (index >= 0) {
    state.favorites.splice(index, 1);
    return false;
  }

  state.favorites.push(trimmed);
  return true;
}

/**
 * 把目录排成「名单优先，其余保持原序」。
 *
 * 无条件生效，不看开关：排序什么都不藏，所以没有把人锁在外面的可能。
 * 判定不参与排序——一个标记忽然出现就让行跳走，比没有标记更难用。
 */
export function orderByFavorites(models) {
  const listed = [];
  const rest = [];

  for (const id of models ?? []) {
    if (isFavorite(id)) { listed.push(id); } else { rest.push(id); }
  }

  return [...listed, ...rest];
}

/**
 * 算出该显示哪些模型，以及被收起了哪些。
 *
 * 阀门只有一条，但同时管住「名单为空」与「名单全部失效」：名单里没有一个模型
 * 出现在当前目录里时，显示完整目录。少了这条，换过网关或模型下架之后开关一开，
 * 选择器就只剩当前模型，而能用的那些全被收起来了。
 *
 * justMarked 为真时也不收起：用户刚点的那一下说的是「记住这个」，
 * 当场把另外几十个藏掉不是他要的意思。
 */
export function applyFavoriteFilter(models, currentModel, justMarked = false) {
  const ordered = orderByFavorites(models);

  if (!state.onlyFavorites || justMarked) {
    return { visible: ordered, hidden: [] };
  }

  const anyListedPresent = ordered.some((id) => isFavorite(id));
  if (!anyListedPresent) {
    return { visible: ordered, hidden: [] };
  }

  const visible = [];
  const hidden = [];
  const currentFolded = fold(currentModel);

  for (const id of ordered) {
    // 当前模型永远可见，无论在不在名单里。
    if (isFavorite(id) || fold(id) === currentFolded) {
      visible.push(id);
    } else {
      hidden.push(id);
    }
  }

  return { visible, hidden };
}

/** 仅供测试重置。 */
export function resetFavorites() {
  state = {
    key: null,
    favorites: [],
    availability: new Map(),
    onlyFavorites: false,
    probing: new Set(),
    bulk: null,
    testing: new Set(),
  };
}
