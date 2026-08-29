import { request, on, logToHost } from './bridge.js';
import {
  getModelCatalog,
  modelCatalogKey,
  modelCatalogRevision,
  putModelCatalog,
} from './model-catalog.js';
import {
  AVAILABILITY,
  adoptFavorites,
  applyFavoriteFilter,
  isFavorite,
  onlyFavorites,
  setOnlyFavorites,
  toggleFavoriteLocally,
  verdictOf,
} from './model-favorites.js';

// 模型与思考等级的两列选择器。
//
// 为什么合成一个控件：二者强相关（不同模型支持的思考档位不同），
// 分成两个独立下拉既占横向空间，也让「这个档位在当前模型上有效吗」
// 变得不直观。
//
// 为什么不用原生 select：需要在每项旁展示说明与「会降级」标注，
// 而 option 内无法放结构化内容。

/**
 * 档位清单的兜底，用于设置尚未返回、或后端没带上选项清单时。
 *
 * 标签与 ID 同形且用英文原名，与协议参数取值逐字一致：档位名要在日志、
 * 请求体和官方文档之间对照，译成中文反而多一层心算。
 * 因此这里不再是「ID → 中文」的翻译表，只是缺省时的顺序与说明来源。
 */
const THINKING_FALLBACK = {
  Off: '不思考，最快，适合简单改动',
  Minimal: '仅 OpenAI 与 Gemini 支持，其他协议按 Low 处理',
  Low: '速度优先，适合明确的小任务',
  Medium: '速度与质量平衡',
  High: '多数模型的默认档，适合复杂表格逻辑',
  XHigh: '长链路任务；不支持时按 High 处理',
  Max: '不限制思考开销；不支持时按 High 处理',
};

function thinkingLabel(id) {
  const option = state.thinkingOptions.find((o) => o.id === id);
  // 兜底直接用 ID：标签本就与 ID 同形，缺了选项清单也不会显示错。
  return option?.label ?? id;
}

let state = {
  model: '',
  thinking: 'High',
  thinkingOptions: [],
  thinkingSupported: new Set(),
  models: [],
  modelsLoaded: false,
  loading: false,
  catalogKey: null,
  loadingCatalogKey: null,
  // 加载中的补充说明，目前用于显示重试进度。
  loadingNote: '',
  // 本次浮层内刚标过星。标完当场把其余模型收起来，用户的动作是「记住这个」，
  // 效果却成了「藏起另外几十个」，浮层里没有任何东西把两者连起来。
  justMarked: false,
  // 用户在本次浮层里点了「显示全部」。只影响这次展开，不落盘。
  showAllOnce: false,
};

let onChange = null;

/** 把当前连接的已获取目录投影到选择器状态。 */
function applyModelCatalog(models) {
  state.models = [...models];

  // 当前模型不在目录里时仍须保留。它可能是网关允许、但 GET /models 未列出的手填 ID。
  // 这里敢无条件保留，是因为 state.model 已由 reconcileModel 按后端设置校正过，
  // 而加载项保证下发的模型一定属于当前连接——不会把上一个连接的模型钉进来。
  if (state.model && !state.models.includes(state.model)) {
    state.models = [state.model, ...state.models];
  }

  state.modelsLoaded = true;
}

/**
 * 以后端设置为准修正当前选中的模型。
 *
 * 必须做：设置页换了接入配置后，加载项会丢弃不属于新连接的模型，
 * 而选择器里的 state.model 还是切换前那个。不修正的话 applyModelCatalog
 * 会把它继续钉在列表首位，看起来就是「切回本机 CLI 配置了，模型却还是自定义接口那个」。
 * 返回是否发生了变化，供调用方决定要不要重绘。
 */
function reconcileModel(settings) {
  const authoritative = settings.model || settings.effectiveModel || '';
  if (authoritative === state.model) {
    return false;
  }

  state.model = authoritative;
  return true;
}

/**
 * 切换选择器正在显示的目录来源。
 *
 * 一旦 API、协议、地址或 CLI 来源变化，绝不继续展示旧来源的模型；若设置页
 * 已经为新来源获取过目录，则直接复用，避免回到对话页后再发一次 GET /models。
 */
function syncModelCatalog(settings) {
  const key = modelCatalogKey(settings);

  // 只在键真的变了时清理本连接的视图状态。
  //
  // 不能无条件清：本函数在「回到对话页」「点对话页签」「新会话」「点刷新」
  // 都会跑（syncPicker 与 loadModels 各调一次），无条件清等于每次切页
  // 都把刚标的星、刚拿到的三态、以及本次浮层的展开状态全抹掉。
  const switchedConnection = state.catalogKey !== null && state.catalogKey !== key;
  if (switchedConnection) {
    state.justMarked = false;
    state.showAllOnce = false;
  }

  state.catalogKey = key;

  const cached = getModelCatalog(settings);
  if (cached !== null) {
    applyModelCatalog(cached);
  } else {
    // 同一地址换密钥时键不会暴露密钥；设置页会显式失效该目录。
    // 因此即使连接键未变，只要缓存已不存在也不能继续显示旧模型。
    state.models = [];
    state.modelsLoaded = false;
  }

  // 其他 API 的慢请求不应把当前选择器卡成“正在获取”。
  state.loading = state.loadingCatalogKey === key;
  return key;
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) { node.className = className; }
  if (text !== undefined) { node.textContent = text; }
  return node;
}

function trigger() {
  return document.getElementById('picker-trigger');
}

function pop() {
  return document.getElementById('picker-pop');
}

function isOpen() {
  const node = pop();
  return node !== null && !node.hidden;
}

function setOpen(open) {
  const node = pop();
  const button = trigger();
  if (!node || !button) { return; }

  node.hidden = !open;
  button.setAttribute('aria-expanded', open ? 'true' : 'false');
  button.classList.toggle('is-open', open);

  // 首次展开时才拉取模型列表，避免面板启动就发起网络请求。
  if (open && !state.modelsLoaded && !state.loading) {
    void loadModels();
  }
}

/** 更新触发按钮上的摘要文字。 */
function renderTrigger() {
  const modelText = document.getElementById('picker-model');
  const thinkingText = document.getElementById('picker-thinking');
  const button = trigger();
  if (!modelText || !thinkingText || !button) { return; }

  modelText.textContent = state.model || '未选择模型';

  const label = thinkingLabel(state.thinking);
  thinkingText.textContent = label;

  const downgraded = state.thinkingSupported.size > 0 && !state.thinkingSupported.has(state.thinking);
  thinkingText.classList.toggle('is-downgraded', downgraded);

  // 当前模型已知不可用时在摘要行提示。用自己的 class：现有的
  // .is-downgraded 规则按 .picker-thinking 限定作用域，只把类名搬到模型 span 上
  // 会没有任何样式，而且不报错。
  const unavailable = Boolean(state.model) && verdictOf(state.model) === AVAILABILITY.unavailable;
  modelText.classList.toggle('is-unavailable', unavailable);

  button.title = `模型：${state.model || '未选择'}${unavailable ? '（上次用它报错说没这个模型）' : ''}\n` +
    `思考等级：${label}${downgraded ? '（当前模型会降级）' : ''}\n点击切换`;
}

/** 渲染模型列列头：「模型」标签、「只看名单」开关、「刷新」。 */
function renderColumnHead() {
  const toggle = document.getElementById('picker-only-favorites');
  if (!toggle) { return; }

  const on = onlyFavorites();
  toggle.setAttribute('aria-pressed', on ? 'true' : 'false');
  toggle.classList.toggle('is-on', on);

  // 名单刚从空变成一项、或本次点过「显示全部」时开关是开的但没在筛，
  // 如实说明，否则用户会以为开关坏了。
  const suspended = on && (state.justMarked || state.showAllOnce);
  toggle.textContent = suspended ? '只看名单（本次先不收起）' : '只看名单';
  toggle.title = on
    ? '当前只显示常用名单里的模型；名单里的模型都不在目录里时会显示完整目录'
    : '只显示常用名单里的模型';
}

/** 渲染模型列。 */
function renderModels() {
  const list = document.getElementById('picker-models');
  if (!list) { return; }

  list.replaceChildren();

  if (state.loading) {
    // 重试期间显示进度：退避等待可达数十秒，一直显示「正在获取…」会像卡死。
    list.append(el('div', 'picker-empty', state.loadingNote || '正在获取…'));
    return;
  }

  if (state.models.length === 0) {
    list.append(el('div', 'picker-empty',
      state.modelsLoaded ? '接口未返回模型列表，可在设置页手填' : '点击「刷新」获取'));

    // 当前模型不在列表里也要能看到并保持选中。
    if (state.model) {
      list.append(buildModelRow(state.model, '当前使用', true, () => {}));
    }
    return;
  }

  const { visible, hidden } = applyFavoriteFilter(
    state.models,
    state.model,
    state.justMarked || state.showAllOnce,
  );

  for (const id of visible) {
    list.append(buildModelRow(id, '', id === state.model, () => selectModel(id)));
  }

  if (hidden.length > 0) {
    list.append(buildHiddenNotice(hidden));
  }
}

/**
 * 被收起的说明。
 *
 * 放列表底部而不是列头：列头是 space-between 的一行，装着「模型」与「刷新」，
 * 再塞一份清单会挤成多行并把列表顶出浮层，而浮层是 overflow: hidden，
 * 超出的部分会被静默裁掉、现有自检一条都不会报。
 *
 * 报数量并给一个「显示全部」的出口。刻意不逐个念名字：被收起的可能是几十个，
 * 那份清单在这个宽度里没有能放下的地方，而「说位置不说数量」是给范围写的规矩,
 * 平铺的模型列表没有位置可言。名字放在 title 里，需要时能看到。
 */
function buildHiddenNotice(hidden) {
  const notice = el('div', 'picker-hidden-note');
  notice.append(el('span', 'picker-hidden-count', `已按名单收起 ${hidden.length} 个模型`));

  const showAll = el('button', 'picker-hidden-show', '显示全部');
  showAll.type = 'button';
  showAll.title = hidden.join('\n');
  showAll.addEventListener('click', (event) => {
    event.stopPropagation();
    state.showAllOnce = true;
    renderModels();
  });

  notice.append(showAll);
  return notice;
}

/** 渲染思考等级列。 */
function renderThinkings() {
  const list = document.getElementById('picker-thinkings');
  if (!list) { return; }

  list.replaceChildren();

  // 选项未下发时用兜底清单，避免整列空白。
  const options = state.thinkingOptions.length > 0
    ? state.thinkingOptions
    : Object.entries(THINKING_FALLBACK).map(([id, hint]) => ({ id, label: id, hint }));

  for (const option of options) {
    const downgraded = state.thinkingSupported.size > 0 && !state.thinkingSupported.has(option.id);
    // 不隐藏不支持的档位：隐藏会让人以为功能缺失，标注则说明会就近降级。
    const hint = downgraded ? '当前模型会降级' : (option.hint ?? '');
    list.append(buildRow(option.label, hint, option.id === state.thinking, () => selectThinking(option.id)));
  }
}

function buildRow(label, hint, active, onClick) {
  const row = el('button', 'picker-item');
  row.type = 'button';
  if (active) { row.classList.add('is-active'); }

  row.append(el('span', 'picker-item-name', label));
  if (hint) { row.append(el('span', 'picker-item-hint', hint)); }

  row.addEventListener('click', onClick);
  return row;
}

/** 三态对应的说明文字。未确认不写字——每行都挂一句「未确认」只是噪音。 */
function verdictHint(verdict) {
  if (verdict === AVAILABILITY.available) { return '用过，能用'; }
  if (verdict === AVAILABILITY.unavailable) { return '用过，报错说没这个模型'; }
  return '';
}

/**
 * 模型行。
 *
 * 与思考等级列分开构建，而不是给共用的 buildRow 加参数：buildRow 两列共用，
 * 就地加节点会让思考档位行也长出星标与状态点。
 *
 * 结构是 .picker-row 容器 + 里面的 .picker-item（仍是 button）+ 星标（兄弟节点）。
 * .picker-item 必须保持 button 且 class 不变：HTML 禁止按钮里嵌套交互元素，
 * 而宿主的端到端驱动按 .picker-item-name 的 textContent 全等匹配后调 row.click()，
 * 把它降级成 div 会让那条路径失效。
 */
function buildModelRow(id, hint, active, onClick) {
  const container = el('div', 'picker-row');

  const row = el('button', 'picker-item');
  row.type = 'button';
  if (active) { row.classList.add('is-active'); }

  const verdict = verdictOf(id);

  // 名字与状态点要横排，而 .picker-item 是 column，所以再包一层。
  const head = el('span', 'picker-item-head');
  const dot = el('span', 'picker-availability-dot');
  if (verdict === AVAILABILITY.available) { dot.classList.add('is-ok'); }
  if (verdict === AVAILABILITY.unavailable) { dot.classList.add('is-error'); }
  head.append(dot);

  // textContent 必须是纯模型 ID：宿主靠它全等匹配来选中。
  head.append(el('span', 'picker-item-name', id));
  row.append(head);

  const verdictText = verdictHint(verdict);
  const text = hint && verdictText ? `${hint} · ${verdictText}` : (hint || verdictText);
  if (text) { row.append(el('span', 'picker-item-hint', text)); }

  row.addEventListener('click', onClick);
  container.append(row);

  const star = el('button', 'picker-star', isFavorite(id) ? '★' : '☆');
  star.type = 'button';
  star.title = isFavorite(id) ? '从常用名单移出' : '加入常用名单';
  star.setAttribute('aria-pressed', isFavorite(id) ? 'true' : 'false');
  star.addEventListener('click', (event) => {
    // 阻止冒泡：否则会连带选中这一行，标星与切换模型是两件事。
    event.stopPropagation();
    void toggleFavorite(id);
  });

  container.append(star);
  return container;
}

function selectModel(id) {
  if (state.model === id) { return; }
  state.model = id;
  renderModels();
  renderTrigger();
  void push();
}

/**
 * 标星或取消标星。
 *
 * 先在本地翻转再重绘，然后把权威值从后端取回来覆盖：名单落在加载项那边的文件里，
 * 等一次往返再重绘会让点击看起来没反应。
 */
async function toggleFavorite(id) {
  const nowFavorite = toggleFavoriteLocally(id);

  // 名单刚从空变成一项时不当场收起其余的。
  if (nowFavorite && onlyFavorites()) {
    state.justMarked = true;
  }

  renderModels();
  renderColumnHead();

  try {
    const result = await request('models.favorites', { action: 'toggle', model: id });
    adoptFavorites(state.catalogKey, {
      favorites: result?.favorites ?? [],
      availability: result?.availability ?? {},
      onlyFavoriteModels: onlyFavorites(),
    });
    renderModels();
    renderColumnHead();
  } catch (error) {
    void logToHost(`更新常用名单失败：${error.message}`, 'warn');
  }
}

/** 列头的「只看名单」开关。 */
async function toggleOnlyFavorites() {
  const next = !onlyFavorites();
  setOnlyFavorites(next);

  // 显式拨动开关就是明确表达了意图，本次浮层的两个临时豁免随之失效。
  state.justMarked = false;
  state.showAllOnce = false;

  renderModels();
  renderColumnHead();

  try {
    await request('session.update', { onlyFavoriteModels: next });
  } catch (error) {
    void logToHost(`保存「只看名单」开关失败：${error.message}`, 'warn');
  }
}

/**
 * 采用手填的模型 ID。
 *
 * 与点选列表项分成两条路：手填的 ID 通常不在目录里（网关不提供 GET /models
 * 时列表本就是空的），必须先并进 state.models，否则触发按钮上换了模型、
 * 模型列里却没有任何一项是选中态，看起来像没生效。
 *
 * 返回是否采用。空白输入不算失败，只是没什么可采用的，由调用方决定怎么提示。
 */
function applyManualModel(raw) {
  const id = String(raw ?? '').trim();
  if (!id) { return false; }

  // 比较忽略大小写：目录侧用 OrdinalIgnoreCase 去重，这里区分大小写的话
  // 手填 GPT-4O 而目录里已有 gpt-4o 就会并成两行。
  const folded = id.toLowerCase();
  if (!state.models.some((m) => m.toLowerCase() === folded)) {
    state.models = [id, ...state.models];
  }

  // 手填的 ID 自动进名单：肯花力气打出来的 ID 就是要用的。
  // 落点在这里而不是别处——本函数已经是「不在目录里也要可见」的既有特例。
  if (!isFavorite(id)) {
    toggleFavoriteLocally(id);
    if (onlyFavorites()) { state.justMarked = true; }
    void request('models.favorites', { action: 'add', model: id })
      .catch((error) => logToHost(`把手填模型加入名单失败：${error.message}`, 'warn'));
  }

  // 与当前模型相同时 selectModel 会提前返回，因此这里仍要重绘一次：
  // 上一步可能刚把它并进列表，列表需要显示出这一项。
  if (state.model === id) {
    renderModels();
  } else {
    selectModel(id);
  }

  renderColumnHead();
  return true;
}

function selectThinking(id) {
  if (state.thinking === id) { return; }
  state.thinking = id;
  renderThinkings();
  renderTrigger();
  void push();
}

async function push() {
  try {
    const result = await request('session.update', {
      model: state.model,
      thinking: state.thinking,
    });

    // 切换模型可能改变协议，进而改变支持的档位，同步回来。
    if (result?.thinkingSupported) {
      state.thinkingSupported = new Set(result.thinkingSupported);
      renderThinkings();
      renderTrigger();
    }

    if (typeof onChange === 'function') { onChange(result); }
  } catch (error) {
    void logToHost(`保存模型或思考等级失败：${error.message}`, 'warn');
  }
}

/**
 * 拉取模型列表。
 *
 * 可重复调用：早先的实现用一次性守卫，导致列表被重建成单项后
 * 再也无法恢复，表现为「选过一次模型就不能再切换」。
 */
async function loadModels(force = false) {
  let settings;
  try {
    settings = await request('settings.get');
  } catch (error) {
    void logToHost(`读取当前设置失败：${error.message}`, 'warn');
    return;
  }

  // 先按后端设置修正选中项，再投影目录：顺序反了会先把旧模型钉进新目录。
  if (reconcileModel(settings)) {
    renderTrigger();
  }

  const key = syncModelCatalog(settings);
  adoptFavorites(key, settings);
  renderColumnHead();
  if (!force && state.modelsLoaded) {
    renderModels();
    return;
  }
  if (state.loadingCatalogKey === key) { return; }

  const revision = modelCatalogRevision(settings);
  state.loadingCatalogKey = key;
  state.loading = true;
  state.loadingNote = '';
  renderModels();

  try {
    const result = await request(
      'models.list',
      { mode: settings.mode, cliSource: settings.cliSource },
      // 必须比加载项侧的预算（单次 30 秒 + 重试退避）宽，
      // 否则面板会先超时，重试就白做了。
      { timeout: 60000 },
    );

    // 设置页可能在请求期间已保存另一套 API/密钥。只有仍属于同一修订的
    // 结果才能进入会话缓存，更不能覆盖正在显示的新目录。
    const stored = putModelCatalog(settings, result.models ?? [], revision);
    if (stored && state.catalogKey === key) {
      applyModelCatalog(getModelCatalog(settings) ?? []);
    }
  } catch (error) {
    void logToHost(`拉取模型列表失败：${error.message}`, 'warn');
    // 不置 modelsLoaded：下次展开时会重试。
  } finally {
    if (state.loadingCatalogKey === key) {
      state.loadingCatalogKey = null;
    }
    state.loadingNote = '';
    if (state.catalogKey === key) {
      state.loading = false;
      renderModels();
    }
  }
}

/** 用后端返回的设置同步选择器状态。 */
export function syncPicker(settings) {
  state.thinkingOptions = settings.thinkingOptions ?? state.thinkingOptions;
  state.thinkingSupported = new Set(settings.thinkingSupported ?? []);
  state.thinking = settings.thinking ?? state.thinking;
  reconcileModel(settings);
  syncModelCatalog(settings);
  adoptFavorites(state.catalogKey, settings);

  renderTrigger();
  renderColumnHead();
  renderModels();
  renderThinkings();
}

export function initPicker(changeHandler) {
  onChange = changeHandler;

  // 加载项在重试获取模型列表时推送进度，展示在模型列的占位文字上。
  on('models-retry', (message) => {
    if (!state.loading) { return; }
    state.loadingNote = message.text ?? '正在重试…';
    renderModels();
  });

  trigger()?.addEventListener('click', () => setOpen(!isOpen()));

  document.getElementById('picker-refresh')?.addEventListener('click', (event) => {
    // 阻止冒泡：否则会连带触发外部点击而关闭浮层。
    event.stopPropagation();
    void loadModels(true);
  });

  document.getElementById('picker-only-favorites')?.addEventListener('click', (event) => {
    event.stopPropagation();
    void toggleOnlyFavorites();
  });

  // 手填模型 ID。用 submit 而非按钮 click，这样输入框里按 Enter 也生效。
  document.getElementById('picker-manual')?.addEventListener('submit', (event) => {
    // 必须阻止默认提交：页面的 CSP 是 form-action 'none'，
    // 真提交会被拦掉并在控制台报错，而面板里看不到控制台。
    event.preventDefault();

    const input = document.getElementById('picker-manual-input');
    if (!applyManualModel(input?.value)) {
      // 空白输入不做提示，把焦点留在输入框即可，用户自然会继续填。
      input?.focus();
      return;
    }

    // 清空输入框：填过的 ID 已经成为列表里的选中项，留着反而像还没提交。
    if (input) { input.value = ''; }

    // 不关闭浮层：与点选列表项一致，方便顺手再调思考等级。
  });

  // 点击浮层外部关闭。用捕获阶段以免被内部 stopPropagation 阻断。
  document.addEventListener(
    'click',
    (event) => {
      if (!isOpen()) { return; }
      const container = document.getElementById('model-picker');
      if (container && !container.contains(event.target)) {
        setOpen(false);
      }
    },
    true,
  );

  // Esc 关闭，符合浮层的通用预期。
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && isOpen()) {
      setOpen(false);
      trigger()?.focus();
    }
  });
}

/** 供布局自检使用：报告选择器的当前状态。 */
export function describePicker() {
  const { visible, hidden } = applyFavoriteFilter(
    state.models,
    state.model,
    state.justMarked || state.showAllOnce,
  );

  return `选择器：模型=${state.model || '未选'} 思考=${state.thinking} ` +
    `模型项=${state.models.length} 档位项=${state.thinkingOptions.length} ` +
    `已加载=${state.modelsLoaded} 展开=${isOpen()} ` +
    `只看名单=${onlyFavorites()} 名单项=${state.models.filter(isFavorite).length} ` +
    `可见=${visible.length} 收起=${hidden.length} ` +
    `当前判定=${state.model ? verdictOf(state.model) : '无'}`;
}
