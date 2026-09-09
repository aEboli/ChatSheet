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
  anyProbing,
  applyFavoriteFilter,
  bulkProgress,
  bulkTestingCount,
  isBulkTesting,
  isFavorite,
  isProbing,
  markBulkTesting,
  markProbing,
  onlyFavorites,
  recordVerdictLocally,
  setBulkProgress,
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
  // 「测试」的范围菜单是否展开。
  testMenuOpen: false,
  // 上一次运行的结局，一句话。空串表示不显示。
  testNote: '',
  // 对话是否在飞。后端在对话进行中会拒绝测试，而那条错误只落宿主日志，
  // 面板上什么都不出——所以这里自己记一份，好把「测试」先禁掉并说明原因。
  turnInFlight: false,
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
    // 上一次运行的结局说的是上一个连接的模型，换了连接就不再成立。
    state.testMenuOpen = false;
    state.testNote = '';
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

  // 收起浮层时把范围菜单一起收掉。留着它的话，下次展开浮层会看到一个上次点开的
  // 菜单，而那是上一次的意图。
  //
  // 刻意不清 testNote：那是上一次运行的结局，用户可能正是关掉浮层去看列表颜色，
  // 回来还要读它。它由「新一批开跑」「换连接」「点刷新」清掉。
  if (!open && state.testMenuOpen) {
    state.testMenuOpen = false;
    renderTestMenu();
    renderTestAll();
  }

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
  // 这件事必须让用户看见，否则会以为开关坏了。但不能靠加长按钮文字来说——
  // 「只看名单（本次先不收起）」有一百三十来像素，列头四个元素本来就挤在一行里，
  // 它一出现整行就折。改为：文字恒为「名单」，这一态用 class 表现（描边变虚线），
  // 完整说法放悬停里。
  const suspended = on && (state.justMarked || state.showAllOnce);
  toggle.textContent = '名单';
  toggle.classList.toggle('is-suspended', suspended);
  toggle.title = suspended
    ? '只看名单：开关是开的，但本次先不收起其余模型（刚标过星或点过「显示全部」）'
    : (on
      ? '只看名单：当前只显示名单里的模型。名单里的模型都不在目录里时会显示完整目录'
      : '只看名单：点一下把列表收窄到常用名单');

  renderBulkProbe();
  renderTestAll();
}

/** 批量测试时的并发数。5 是用户定的：比串行快得多，又不至于一口气压满账号。 */
const TEST_CONCURRENCY = 5;

/**
 * 「测试」的三档范围。target 是「探出这么多个可用的就停」，0 表示测完整份目录。
 *
 * 为什么每档都要写出上界，而上界又都等于目录条数：目标一个都达不到时，运行就是全量。
 * 把带目标那两档的上界写成目标数（「最多 2 条」）是错的——那是这次最容易犯且不报错
 * 的一处，用户按那句话估成本，而账单可能是几十条。
 *
 * 三档之间真正不同的是**下界**与「什么会让它提前结束」。并发 5 时带目标那两档至少
 * 会发 5 条：达标那一刻那一波已经在飞了，收不回来。写「找到 1 个就停」而不说这件事，
 * 读起来像只发 1 条。
 */
const TEST_SCOPES = [
  { target: 1, label: '找到 1 个能用的就停' },
  { target: 2, label: '找到 2 个能用的就停' },
  { target: 0, label: '全部测试' },
];

/** 「测试」为什么点不动。返回原因，能点时返回空串。 */
function testBlockedReason() {
  if (state.models.length === 0) { return '目录是空的，先点「刷新」获取'; }
  if (anyProbing()) { return '正在确认一个模型，稍后再来'; }
  // 对话在飞时后端会抛 BUSY，而那条错误只落在宿主日志里，面板上什么都不出。
  // 改动之前这是「点一下没反应」；有了菜单就是「走两步仍然只换来一条日志」。
  if (state.turnInFlight) { return '正在对话中，等这一轮结束再测试模型'; }
  return '';
}

/**
 * 「测试」入口。
 *
 * 点下去先给出范围，不直接开跑：直接开跑的那一档是最贵的一档，而一次点击只能表达
 * 一个意思，于是「最贵」成了唯一能表达的意思。
 *
 * 跑起来之后分母改成目标数。用目录条数当分母的话，一次「找到 1 个就停」实际只跑
 * 五六个，按钮却从头到尾显示「停止 3/40」——读起来是「还要跑 37 个」，
 * 而它随后就停了，看起来像断了。
 */
function renderTestAll() {
  const button = document.getElementById('picker-test-all');
  if (!button) { return; }

  const progress = bulkProgress();
  if (progress) {
    const target = progress.target ?? 0;
    if (target > 0) {
      button.textContent = `停止 ${progress.availableFound ?? 0}/${target} 可用`;
      button.title = `停止批量测试。正在找 ${target} 个能用的，` +
        `已找到 ${progress.availableFound ?? 0} 个，已测 ${progress.index ?? 0} 个。` +
        '已经测出的结果保留，不影响正在进行的对话';
    } else {
      button.textContent = progress.total > 0
        ? `停止 ${progress.index}/${progress.total}`
        : '停止';
      button.title = '停止批量测试。已经测出的结果保留，不影响正在进行的对话';
    }
    button.classList.add('is-running');
    button.classList.remove('is-menu-open');
    button.setAttribute('aria-expanded', 'false');
    button.disabled = false;
    return;
  }

  const blocked = testBlockedReason();
  button.textContent = '测试';
  button.classList.remove('is-running');
  button.classList.toggle('is-menu-open', state.testMenuOpen);
  button.setAttribute('aria-expanded', state.testMenuOpen ? 'true' : 'false');
  button.disabled = blocked !== '';
  button.title = blocked !== ''
    ? `测试模型：${blocked}`
    : (state.testMenuOpen
      ? '选一档开始测，或再点一下收起'
      : `测试模型：点一下选测到什么程度为止。` +
        `目录有 ${state.models.length} 个模型，每个发一条最小请求；并发 ${TEST_CONCURRENCY}，` +
        '并发可能撞上限流，被限流的会记为「未确认」而不是「不可用」');
}

/** 范围菜单。每档写出上界、下界，以及什么会让它提前结束。 */
function renderTestMenu() {
  const menu = document.getElementById('picker-test-menu');
  if (!menu) { return; }

  const open = state.testMenuOpen && !bulkProgress() && testBlockedReason() === '';
  menu.hidden = !open;
  menu.replaceChildren();
  if (!open) { return; }

  const count = state.models.length;

  for (const scope of TEST_SCOPES) {
    const item = el('button', 'picker-test-item');
    item.type = 'button';
    item.setAttribute('data-target', String(scope.target));
    item.append(el('span', 'picker-test-item-label', scope.label));

    if (scope.target > 0) {
      // 下界取「目标数与并发数里更大的那个」，且不超过目录条数：达标那一刻
      // 在飞的一波收不回来，所以并发 5 时最少就是 5 条。
      const floor = Math.min(count, Math.max(scope.target, TEST_CONCURRENCY));
      item.append(el('span', 'picker-test-item-cost', `　最少 ${floor} 条，最多 ${count} 条`));
      item.title = `逐个测，累计探出 ${scope.target} 个能用的就不再发新请求。\n` +
        `最多 ${count} 条计费请求——目录里能用的不足 ${scope.target} 个时会全部测完。\n` +
        `最少 ${floor} 条：并发 ${TEST_CONCURRENCY}，达标那一刻已经发出去的收不回来。\n` +
        '被限流的记为「未确认」，不算找到了一个能用的。';
    } else {
      item.append(el('span', 'picker-test-item-cost', `　${count} 条`));
      item.title = `把目录里 ${count} 个模型全部测一遍，共 ${count} 条计费请求。\n` +
        `并发 ${TEST_CONCURRENCY}。不会提前结束。`;
    }

    item.addEventListener('click', (event) => {
      event.stopPropagation();
      setTestMenuOpen(false);
      void testAllModels(scope.target);
    });

    menu.append(item);
  }
}

function setTestMenuOpen(open) {
  state.testMenuOpen = Boolean(open);
  renderTestMenu();
  renderTestAll();
}

/** 上一次运行是怎么结束的。 */
function renderTestNote() {
  const note = document.getElementById('picker-test-note');
  if (!note) { return; }

  const text = state.testNote;
  note.hidden = !text;
  note.textContent = text || '';
}

/**
 * 跑一遍目录，target 为 0 时全量。
 *
 * 三种结局各有各的说法。最要紧的是第三种：目标没达成而整份目录已经测完——
 * 那时用户选了省钱的选项却付了全额，不说出来界面上看不出这件事。
 */
async function testAllModels(target = 0) {
  if (bulkProgress()) { return; }

  state.testNote = '';
  renderTestNote();
  setBulkProgress({
    index: 0,
    total: state.models.length,
    model: '',
    target,
    availableFound: 0,
  });
  renderTestAll();
  renderColumnHead();

  try {
    const result = await request(
      'models.test.all',
      {
        models: [...state.models],
        concurrency: TEST_CONCURRENCY,
        stopAfterAvailable: target,
      },
      // 几十个模型 × 每个最长 15 秒截止时间，按并发 5 折算再留足余量。
      { timeout: 1800000 },
    );
    adoptFavorites(state.catalogKey, {
      favorites: favoritesSnapshot(),
      availability: result?.availability ?? {},
      onlyFavoriteModels: onlyFavorites(),
    });
    state.testNote = describeTestOutcome(result, target);
  } catch (error) {
    // 后端拒绝时（对话在飞会抛 BUSY）也要在面板上留一句：只写宿主日志的话，
    // 用户走完「开菜单、选一档」两步却什么都没看见。
    state.testNote = `没能开始测试：${error.message}`;
    void logToHost(`批量测试失败：${error.message}`, 'warn');
  } finally {
    setBulkProgress(null);
    renderModels();
    renderColumnHead();
    renderTestNote();
  }
}

/** 把结局翻成一句话。 */
function describeTestOutcome(result, requestedTarget) {
  const total = result?.total ?? 0;
  const attempted = result?.attempted ?? 0;
  const found = result?.availableFound ?? 0;
  // 后端的口径优先：面板不自己从别的字段推断结局。
  const outcome = result?.outcome
    ?? (result?.stopped ? 'stopped' : (result?.targetMet ? 'target' : 'completed'));
  const target = result?.target ?? requestedTarget ?? 0;

  if (outcome === 'stopped') {
    return `已停止：发了 ${attempted} 条请求，找到 ${found} 个能用的。` +
      '已经测出的结果都保留着。';
  }

  if (outcome === 'target') {
    return `找到 ${found} 个能用的，够了：发了 ${attempted} 条请求，` +
      `剩下 ${Math.max(0, total - attempted)} 个没测。`;
  }

  // 全部测完。目标没达成这一档必须明说——用户选的是省钱的那一档，付的是全款。
  if (target > 0 && found < target) {
    return `整份目录 ${total} 个都测了，只找到 ${found} 个能用的（想找 ${target} 个）。` +
      `发了 ${attempted} 条请求。`;
  }

  return `${total} 个模型都测完了，找到 ${found} 个能用的。`;
}

/** 名单区的「全部确认」入口与进度。 */
function renderBulkProbe() {
  const button = document.getElementById('picker-probe-all');
  if (!button) { return; }

  const progress = bulkProgress();
  const listed = state.models.filter(isFavorite).length;

  if (progress) {
    // 跑起来后按钮变成「停止」：批量的停止与对话的停止是两个动作，
    // 一个控件按隐藏状态决定停哪个，正是这个项目已经付过代价的故障。
    // 进度不带括号，省下的字宽让整行装得下：列头四个元素都在这一行里。
    button.textContent = progress.total > 0
      ? `停止 ${progress.index}/${progress.total}`
      : '停止';
    button.title = '停止批量确认。已经确认过的结果保留，不影响正在进行的对话';
    button.classList.add('is-running');
    button.disabled = false;
    return;
  }

  // 文字取「确认」而非「全部确认」：作用范围（名单里的那些）在悬停里说得更清楚，
  // 而列头的横向余量要留给模型 ID 那一列。
  button.textContent = '确认';
  button.classList.remove('is-running');

  // 名单为空时没什么可确认的；正在单个确认时也不放批量出去（单飞）。
  const blocked = listed === 0 || anyProbing();
  button.disabled = blocked;
  button.title = listed === 0
    ? '逐个确认名单里的模型：名单是空的，先给常用的模型标上星'
    : (anyProbing()
      ? '逐个确认名单里的模型：正在确认一个，稍后再来'
      : `逐个确认名单里的 ${listed} 个模型，各发一条最小请求`);
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
    list.append(buildThinkingRow(option, downgraded));
  }
}

/**
 * 思考等级行。
 *
 * 一行一档，档位名占一行的左端，右端只在会降级时留一个短标。说明文字收进
 * 悬停提示：七档说明每条十几个字，摊在行上就是七行小字，而用户在这一列里
 * 做的动作只是「挑一档」——挑的时候需要看清的是档位名，不是七份解释。
 *
 * 降级标注不收进悬停：它不是解释，是「你选的这一档在当前模型上不会生效」，
 * 属于必须先看见才能做对选择的信息。「标注永不隐藏」这条对档位与模型一致。
 */
function buildThinkingRow(option, downgraded) {
  const row = el('button', 'picker-item picker-item-line');
  row.type = 'button';
  if (option.id === state.thinking) { row.classList.add('is-active'); }
  if (downgraded) { row.classList.add('is-downgraded'); }

  row.append(el('span', 'picker-item-name', option.label));
  if (downgraded) {
    row.append(el('span', 'picker-thinking-tag', '会降级'));
  }

  const hint = option.hint ?? '';
  row.title = downgraded
    ? `当前模型不支持 ${option.label}，会就近降级${hint ? `\n${hint}` : ''}`
    : (hint || option.label);

  row.addEventListener('click', () => selectThinking(option.id));
  return row;
}

/** 三态对应的说明文字。未确认也有话说——它要与「标记没画出来」区分得开。 */
function verdictHint(verdict) {
  if (verdict === AVAILABILITY.available) { return '能用'; }
  if (verdict === AVAILABILITY.unavailable) { return '报错说没这个模型'; }
  return '还没确认过';
}

/**
 * 模型行的悬停说明。
 *
 * 结论从行上的文字改成了行的颜色与状态点，于是这句话是「颜色到底什么意思」
 * 唯一的出处，必须逐态都说得清。不可用那一态还要说明它仍然可选：判定是
 * 启发式的，用户认为判错了就该能直接点。
 */
function modelRowTitle(id, verdict, probing) {
  if (probing) { return `${id}\n正在确认能不能用…`; }
  if (verdict === AVAILABILITY.available) { return `${id}\n能用（确认过）`; }
  if (verdict === AVAILABILITY.unavailable) {
    return `${id}\n报错说没这个模型。仍可点击使用——判定可能已经过时`;
  }
  return `${id}\n还没确认过能不能用。把鼠标停在这一行上，右侧会出现「试一下」`;
}

/**
 * 确认一个模型。
 *
 * 先置「正在确认」再发请求：不置的话慢网关与「点了没反应」分不开。
 */
async function probeModel(id) {
  if (isProbing(id)) { return; }

  markProbing(id, true);
  renderModels();

  try {
    const result = await request('models.probe', { model: id }, { timeout: 30000 });
    adoptFavorites(state.catalogKey, {
      favorites: favoritesSnapshot(),
      availability: result?.availability ?? {},
      onlyFavoriteModels: onlyFavorites(),
    });
  } catch (error) {
    // 对话在飞时后端会拒，如实说明而不是静默失败。
    void logToHost(`确认 ${id} 失败：${error.message}`, 'warn');
  } finally {
    markProbing(id, false);
    renderModels();
    renderTrigger();
    renderColumnHead();
  }
}

/** 当前名单的快照，用于在采纳新 payload 时不丢掉名单。 */
function favoritesSnapshot() {
  return state.models.filter(isFavorite);
}

/** 把名单里的模型逐个确认完。 */
async function probeFavorites() {
  if (bulkProgress()) { return; }

  setBulkProgress({ index: 0, total: 0, model: '' });
  renderColumnHead();

  try {
    const result = await request('models.probe.bulk', {}, { timeout: 600000 });
    adoptFavorites(state.catalogKey, {
      favorites: favoritesSnapshot(),
      availability: result?.availability ?? {},
      onlyFavoriteModels: onlyFavorites(),
    });
  } catch (error) {
    void logToHost(`批量确认失败：${error.message}`, 'warn');
  } finally {
    setBulkProgress(null);
    renderModels();
    renderColumnHead();
  }
}

async function stopBulkProbe() {
  try {
    await request('models.probe.stop', {});
  } catch (error) {
    void logToHost(`停止批量确认失败：${error.message}`, 'warn');
  }
}

/**
 * 模型行。
 *
 * 与思考等级行（buildThinkingRow）分开构建：两列的行差得远——模型行是 column
 * 且带星标、状态点与「试一下」，档位行是单行且带降级标注。共用一个构造函数时，
 * 往里加节点会让另一列也长出不该有的东西。
 *
 * 结构是 .picker-row 容器 + 里面的 .picker-item（仍是 button）+ 星标（兄弟节点）。
 * .picker-item 必须保持 button 且 class 不变：HTML 禁止按钮里嵌套交互元素，
 * 而宿主的端到端驱动按 .picker-item-name 的 textContent 全等匹配后调 row.click()，
 * 把它降级成 div 会让那条路径失效。
 *
 * 结论落在行本身的 class 上（is-unavailable / is-available），由 CSS 给模型名上色。
 * 此前只有一个 7px 的点在变色，而它旁边是同样黑的模型名——一列几十行扫过去，
 * 能用与不能用看着是一样的。判定要一眼可见，只能落在这一行里最大的那块字上。
 */
function buildModelRow(id, hint, active, onClick) {
  const container = el('div', 'picker-row');

  const row = el('button', 'picker-item');
  row.type = 'button';
  if (active) { row.classList.add('is-active'); }

  const verdict = verdictOf(id);
  const probing = isProbing(id);
  // 批量测试正测到这一个。与 probing 是两回事：那个是用户逐个点「试一下」时置的，
  // 批量整批只占一次闸门、不逐个置 probing，所以批量期间没有任何一行会是 probing。
  const testing = isBulkTesting(id);

  // 正在测的那一行加一道从左到右扫过的高光（见 app.css 的 is-testing）。
  //
  // 为什么需要它：批量测试一次跑几十个模型、要好一阵，而此前列表里唯一的进度线索
  // 是列头按钮上的「停止 3/40」那个数字——看得出跑到第几个，看不出正在测哪一个。
  // 已测完的行会变绿变红，正在测的那一行却和还没测的完全一样。
  if (testing) { row.classList.add('is-testing'); }

  // 名字与状态点要横排，而 .picker-item 是 column，所以再包一层。
  const head = el('span', 'picker-item-head');
  const dot = el('span', 'picker-availability-dot');
  if (probing) {
    // 正在确认是第四个显示态，与三态都不同。
    dot.classList.add('is-probing');
    row.classList.add('is-probing');
  } else {
    if (verdict === AVAILABILITY.available) {
      dot.classList.add('is-ok');
      row.classList.add('is-available');
    }
    if (verdict === AVAILABILITY.unavailable) {
      dot.classList.add('is-error');
      row.classList.add('is-unavailable');
    }
  }
  head.append(dot);

  // textContent 必须是纯模型 ID：宿主靠它全等匹配来选中。
  head.append(el('span', 'picker-item-name', id));
  row.append(head);

  // 行上只留必须占一行的字：正在确认（这一态没有颜色可依，动画在点上，
  // 但「在等什么」得有字说），以及调用方给的补充说明（例如「当前使用」）。
  // 三态的结论收进悬停说明——它已经由行的颜色表达，再写一遍就是把每一行
  // 都撑成两行，而这一列要装的是几十个模型。
  const inline = probing ? '正在确认…' : '';
  const text = hint && inline ? `${hint} · ${inline}` : (hint || inline);
  if (text) { row.append(el('span', 'picker-item-hint', text)); }

  row.title = modelRowTitle(id, verdict, probing);
  row.addEventListener('click', onClick);
  container.append(row);

  // 「试一下」只对没有判定的模型显示：已经有结论的行再挂一个按钮只是噪音。
  // 平时不可见（CSS 里 opacity: 0），鼠标停在这一行或键盘聚焦到行内时才浮出来。
  // 藏起来而不是删掉：几十行各挂一个按钮会把这一列变成按钮墙，而它是个偶尔
  // 才用一次的动作。仍然留在 DOM 里且可聚焦，所以键盘与端到端驱动都拿得到。
  if (verdict === AVAILABILITY.unknown && !probing) {
    const probe = el('button', 'picker-probe', '试一下');
    probe.type = 'button';
    probe.title = `发一条最小请求，确认 ${id} 能不能用`;
    probe.addEventListener('click', (event) => {
      event.stopPropagation();
      void probeModel(id);
    });
    container.append(probe);
  }

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
      // 列头必须一起重画。它上面几个按钮的可用性按目录里有多少模型算
      // （「测试」要目录非空、「确认」要名单非空），而这个函数进来时
      // 目录还是空的——那时算出来的是禁用。只重画列表的话，模型都出来了，
      // 按钮却还停在「目录为空」那一刻的判断上，永远点不动。
      renderColumnHead();
      // 范围菜单同理：每档要写出目录条数，而进来时目录还是空的。
      renderTestMenu();
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
  renderTestMenu();
  renderTestNote();
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
    // 换来的目录可能与上一次运行说的那份不是一回事，那句结局随之作废。
    state.testMenuOpen = false;
    state.testNote = '';
    renderTestMenu();
    renderTestNote();
    void loadModels(true);
  });

  document.getElementById('picker-only-favorites')?.addEventListener('click', (event) => {
    event.stopPropagation();
    void toggleOnlyFavorites();
  });

  document.getElementById('picker-test-all')?.addEventListener('click', (event) => {
    event.stopPropagation();
    if (bulkProgress()) {
      // 跑起来之后这个按钮是「停止」，不是菜单入口。
      void stopBulkProbe();
      return;
    }
    // 点一下开菜单、再点一下收起。不做悬停打开：悬停打开会让最常见的鼠标操作
    // （移上去、点下去）变成「点一下把菜单关掉」。
    setTestMenuOpen(!state.testMenuOpen);
  });

  document.getElementById('picker-probe-all')?.addEventListener('click', (event) => {
    event.stopPropagation();
    if (bulkProgress()) {
      void stopBulkProbe();
      return;
    }
    void probeFavorites();
  });

  // 批量进度。逐个推送，让用户看到在确认哪一个。
  on('probe-progress', (message) => {
    // 边跑边上色：批量每测完一个就推一次进度并带上该模型的判定。
    // 不落这一步的话，整批结束前一列都是「未确认」，十几个模型跑一遍要好一阵，
    // 中途看起来像点了没反应。权威值仍由整批结束时的回复覆盖。
    //
    // 先落判定再设进度：下面 setBulkProgress 之后就要重绘，
    // 顺序反了这一行会晚一帧才上色。
    if (message?.model && message?.verdict) {
      recordVerdictLocally(message.model, message.verdict);
    }

    if (message?.done) {
      setBulkProgress(null);
    } else if (message?.settled && !bulkProgress()) {
      // 收尾之后迟到的 settled 不复活进度。
      //
      // 用户中止那条路会遗弃在飞的任务（取消从派发循环里抛出，WhenAll 没走到），
      // 而收尾的 done 已经推过了。那些任务随后推来的 settled 若照常处理，
      // 会把进度重新置上，列头按钮回到「停止 n/总」——而后端已经没有批量在跑，
      // 再点它只会拿到 stopped: false，且关面板也不清（这个仓库记着「关面板什么都不清」）。
      //
      // 只挡 settled，不挡 starting：会迟到的只有 settled——某个模型的 starting
      // 是在它自己发请求之前推的，那时批必然还在跑。把 starting 一起挡掉会连
      // 「批刚开始」这件事本身也挡住。
      //
      // 判定仍然照上面落了本地投影：那些请求已经付过钱，答案该留着。
      markBulkTesting(message.model, false);
      renderModels();
      renderColumnHead();
      return;
    } else {
      // 在飞集合由推送两端驱动：starting 加进去，settled 摘出来。
      //
      // 面板不自己猜「现在该是哪一个」。批量测试并发 5，同一时刻在飞的是五个，
      // 而推送是乱序到的——按「最后一条推送提到谁」去标，标的永远是刚探完那一个，
      // 也就是一行已经变绿或变红的行。
      if (message?.model) {
        if (message.settled) {
          markBulkTesting(message.model, false);
        } else if (message.starting) {
          markBulkTesting(message.model, true);
        }
      }

      // 进度计数只认带 index 的那条。starting 那条不带 index——已完成数由
      // settled 那条推进，两条都往里写会让计数在「开始探下一个」时倒退。
      const previous = bulkProgress();
      setBulkProgress({
        index: message?.index ?? previous?.index ?? 0,
        total: message?.total ?? previous?.total ?? 0,
        // 目标数与已找到个数同样要沿用上一条：后端每条推送都带着它们，
        // 但缺字段时不能落成 0，否则按钮上的分子会在 starting 与 settled
        // 交替时来回跳。名单批量那条路一直不带这两个字段，缺席即 0。
        target: message?.target ?? previous?.target ?? 0,
        availableFound: message?.availableFound ?? previous?.availableFound ?? 0,
      });
    }

    renderModels();
    renderColumnHead();
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
  //
  // 范围菜单开着时先只收菜单：菜单是浮层内的一层，逐层退出是这类控件的通用预期，
  // 而一下退到底会把用户从「我在挑测试范围」直接扔回对话页。焦点还给「测试」按钮，
  // 否则键盘用户退出后不知道自己在哪。
  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape' || !isOpen()) { return; }

    if (state.testMenuOpen) {
      setTestMenuOpen(false);
      document.getElementById('picker-test-all')?.focus();
      return;
    }

    setOpen(false);
    trigger()?.focus();
  });
}

/**
 * 告诉选择器对话是否在飞。
 *
 * 加载项在对话进行中会拒绝批量测试（抛 BUSY），而面板此前既不禁用也只写宿主日志：
 * 用户走完「开菜单、选一档」两步，界面上什么都不出。
 */
export function setPickerTurnInFlight(inFlight) {
  const next = Boolean(inFlight);
  if (state.turnInFlight === next) { return; }

  state.turnInFlight = next;
  // 对话跑起来时把菜单收掉：留着它意味着留着一个选下去只会失败的入口。
  if (next && state.testMenuOpen) {
    setTestMenuOpen(false);
    return;
  }

  renderTestMenu();
  renderTestAll();
}

/** 供布局自检使用：报告选择器的当前状态。 */
export function describePicker() {
  const { visible, hidden } = applyFavoriteFilter(
    state.models,
    state.model,
    state.justMarked || state.showAllOnce,
  );

  const progress = bulkProgress();

  return `选择器：模型=${state.model || '未选'} 思考=${state.thinking} ` +
    `模型项=${state.models.length} 档位项=${state.thinkingOptions.length} ` +
    `已加载=${state.modelsLoaded} 展开=${isOpen()} ` +
    `只看名单=${onlyFavorites()} 名单项=${state.models.filter(isFavorite).length} ` +
    `可见=${visible.length} 收起=${hidden.length} ` +
    `当前判定=${state.model ? verdictOf(state.model) : '无'} ` +
    `正在确认=${state.models.filter(isProbing).length} ` +
    `批量=${progress ? `${progress.index}/${progress.total}` : '无'} ` +
    `在飞=${bulkTestingCount()} ` +
    `范围菜单=${state.testMenuOpen} 目标=${progress?.target ?? 0} ` +
    `已找到=${progress?.availableFound ?? 0} ` +
    `结局说明=${state.testNote ? '有' : '无'}`;
}
