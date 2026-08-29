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

  state = {
    key,
    favorites,
    availability,
    onlyFavorites: Boolean(settings.onlyFavoriteModels),
  };
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
  state = { key: null, favorites: [], availability: new Map(), onlyFavorites: false };
}
