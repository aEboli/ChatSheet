import { request, on, logToHost } from './bridge.js';
import {
  getModelCatalog,
  modelCatalogKey,
  modelCatalogRevision,
  putModelCatalog,
} from './model-catalog.js';

// 模型与思考等级的两列选择器。
//
// 为什么合成一个控件：二者强相关（不同模型支持的思考档位不同），
// 分成两个独立下拉既占横向空间，也让「这个档位在当前模型上有效吗」
// 变得不直观。
//
// 为什么不用原生 select：需要在每项旁展示说明与「会降级」标注，
// 而 option 内无法放结构化内容。

/**
 * 档位 ID 到中文标签的兜底映射。
 *
 * 正常情况下标签由后端随设置一起下发，但在设置尚未返回、
 * 或后端因故没带上选项清单时，若直接回退显示 ID 就会露出英文。
 * 界面文案不应出现这种情况，故在此保底。
 */
const THINKING_FALLBACK = {
  Off: '关闭思考',
  Minimal: '极少',
  Low: '低',
  Medium: '中',
  High: '高',
  XHigh: '超高',
  Max: '最大',
};

function thinkingLabel(id) {
  const option = state.thinkingOptions.find((o) => o.id === id);
  return option?.label ?? THINKING_FALLBACK[id] ?? id;
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
};

let onChange = null;

/** 把当前连接的已获取目录投影到选择器状态。 */
function applyModelCatalog(models) {
  state.models = [...models];

  // 当前模型不在目录里时仍须保留。它可能是网关允许、但 GET /models 未列出的手填 ID。
  if (state.model && !state.models.includes(state.model)) {
    state.models = [state.model, ...state.models];
  }

  state.modelsLoaded = true;
}

/**
 * 切换选择器正在显示的目录来源。
 *
 * 一旦 API、协议、地址或 CLI 来源变化，绝不继续展示旧来源的模型；若设置页
 * 已经为新来源获取过目录，则直接复用，避免回到对话页后再发一次 GET /models。
 */
function syncModelCatalog(settings) {
  const key = modelCatalogKey(settings);
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

  button.title = `模型：${state.model || '未选择'}\n` +
    `思考等级：${label}${downgraded ? '（当前模型会降级）' : ''}\n点击切换`;
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
      list.append(buildRow(state.model, '当前使用', true, () => {}));
    }
    return;
  }

  for (const id of state.models) {
    list.append(buildRow(id, '', id === state.model, () => selectModel(id)));
  }
}

/** 渲染思考等级列。 */
function renderThinkings() {
  const list = document.getElementById('picker-thinkings');
  if (!list) { return; }

  list.replaceChildren();

  // 选项未下发时用兜底清单，避免整列空白。
  const options = state.thinkingOptions.length > 0
    ? state.thinkingOptions
    : Object.entries(THINKING_FALLBACK).map(([id, label]) => ({ id, label, hint: '' }));

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

function selectModel(id) {
  if (state.model === id) { return; }
  state.model = id;
  renderModels();
  renderTrigger();
  void push();
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

  const key = syncModelCatalog(settings);
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
  state.model = settings.model || settings.effectiveModel || '';
  syncModelCatalog(settings);

  renderTrigger();
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
  return `选择器：模型=${state.model || '未选'} 思考=${state.thinking} ` +
    `模型项=${state.models.length} 档位项=${state.thinkingOptions.length} ` +
    `已加载=${state.modelsLoaded} 展开=${isOpen()}`;
}
