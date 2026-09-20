import { request, on, logToHost } from './bridge.js';
import {
  checkinView, getWorkBuddyCheckin, subscribeWorkBuddyCheckin,
  clearWorkBuddyCheckin, setWorkBuddyCheckin, refreshWorkBuddyCheckin,
} from './workbuddy.js';
import {
  getModelCatalog,
  invalidateModelCatalog,
  modelCatalogKey,
  modelCatalogRevision,
  putModelCatalog,
  rememberAuthorizedModelCatalog,
  selectAuthorizedModel,
} from './model-catalog.js';

let form;
let statusLine;
let current = null;
let customTokenRevision = 0;
let lastModelFetch = null;
let customTokenDraft = '';
let activeModelRequest = null;
let modelRequestSequence = 0;
let modelFetchState = { phase: 'idle', detail: '' };
const connectionSnapshots = new Map();
const modelRequests = new Map();
let workBuddyBusy = false;
let workBuddyAuthUrl = '';
let workBuddyOperationId = '';
let workBuddyRefreshTimer = null;
let lastReturnRefreshAt = 0;
// 每次模式切换都推进。跨 await 的旧响应只能更新发起它的那一模式。
let modeRevision = 0;

export function isWorkBuddyMode(mode) {
  return mode === 'Authorized' || mode === 'AuthorizedInternational';
}

export function workBuddyLoginAvailable(runtime = {}) {
  // ACP 探测失败不等于用户不能发起登录：点击后后端会重新探测，
  // 没有组件时也要让用户得到安装/启动提示，而不是看到一个死按钮。
  return runtime.loginDisabled !== true;
}

/** 跨异步请求只接受仍属于当前模式代数的结果。 */
export function modeResponseIsCurrent(requestMode, requestRevision, currentMode, currentRevision) {
  return requestMode === currentMode && requestRevision === currentRevision;
}

function isDomesticWorkBuddyMode(mode) {
  return mode === 'Authorized';
}

const CLI_LABELS = {
  Auto: '自动（优先 Claude）',
  Claude: 'Claude CLI',
  Codex: 'Codex CLI',
};

function setStatus(text, variant = '') {
  statusLine.textContent = text ?? '';
  statusLine.className = variant ? `status is-${variant}` : 'status';
}

function clearDuplicateStatus(...details) {
  const currentStatus = statusLine?.textContent?.trim();
  if (!currentStatus) { return; }
  if (details.some(detail => typeof detail === 'string' && detail.trim() === currentStatus)) {
    setStatus('');
  }
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) { node.className = className; }
  if (text !== undefined) { node.textContent = text; }
  return node;
}

/**
 * 设置页的静态解释不常驻占一整行：收进原生 details，既不堆高面板，
 * 又保留键盘、触屏和读屏可访问的完整提示。
 */
function fieldHelp(text, label = '查看提示') {
  const help = el('details', 'field-help');
  const summary = el('summary', 'field-help-toggle', 'ⓘ');
  summary.title = label;
  summary.setAttribute('aria-label', label);
  help.append(summary, el('div', 'field-help-body', text));
  return help;
}

function appendFieldHelp(parent, text, label = '查看提示') {
  if (text) { parent.append(fieldHelp(text, label)); }
  return parent;
}

function field(labelText, control, hintText) {
  const wrapper = el('div', 'field');
  const label = el('label', 'field-label', labelText);
  if (control.id) { label.htmlFor = control.id; }
  const labelRow = el('div', 'field-label-row');
  labelRow.append(label);
  appendFieldHelp(labelRow, hintText);
  wrapper.append(labelRow, control);
  return wrapper;
}

function select(id, options, value) {
  const node = el('select', 'input');
  node.id = id;
  for (const option of options) {
    const item = el('option', null, option.label);
    item.value = option.value;
    if (option.value === value) { item.selected = true; }
    node.append(item);
  }
  return node;
}

function input(id, value, type = 'text', placeholder = '') {
  const node = el('input', 'input');
  node.id = id;
  node.type = type;
  node.value = value ?? '';
  node.placeholder = placeholder;
  return node;
}

/** 仅提取会影响 GET /models 的字段，绝不包含密钥。 */
function modelConnectionSettings() {
  return {
    mode: current.mode,
    cliSource: current.cliSource,
    customProtocol: current.customProtocol,
    customBaseUrl: current.customBaseUrl,
  };
}

/**
 * 接入模式变化后，旧模型不再属于同一个连接，不能继续带入保存请求。
 * 返回值用于区分“真的切换了模式”和重复选择当前模式。
 *
 * modelChosenForConnection 是给后端的正向确认：只有用户在当前这套接入配置下
 * 选过模型才置真。改成正向确认是因为反过来（「请清除旧模型」）依赖本页的
 * mode 一定比磁盘新，而本页的 current 可能在对话页改过模型后就已过期。
 */
export function resetModelOnModeChange(settings, nextMode) {
  const changed = settings.mode !== nextMode;
  settings.mode = nextMode;
  if (changed) {
    settings.model = '';
    settings.effectiveModel = '';
    settings.modelChosenForConnection = false;
    settings.ready = false;
    settings.readyDetail = '';
    settings.authorization = null;
    settings.workbuddyRuntime = null;
  }
  return changed;
}

/** 用户主动选定或输入了模型，登记为「为当前连接所选」。 */
function markModelChosen(model) {
  current.model = model;
  current.modelChosenForConnection = Boolean(model && model.trim());
  rememberConnection();
}

/**
 * 授权目录刷新后校正当前模型归属。
 *
 * 国内版和国际版虽然都走 WorkBuddy ACP，但它们是两套账号空间；只按
 * mode 切换缓存还不够，响应回来的目录必须再次验证输入框里的模型 ID。
 */
export function reconcileAuthorizedModel(settings, authorization = settings?.authorization) {
  if (!isWorkBuddyMode(settings?.mode) || !authorization ||
    !['authorized', 'unauthorized'].includes(authorization.status)) {
    return false;
  }

  const previous = String(settings.model ?? '').trim();
  const next = selectAuthorizedModel(previous, authorization);
  const changed = previous !== next || String(settings.model ?? '') !== next;
  settings.model = next;
  settings.effectiveModel = next;
  settings.modelChosenForConnection = Boolean(next);
  return changed;
}

/**
 * 采用加载项返回的设置作为表单状态。
 *
 * 加载项已经丢弃了不属于当前连接的模型，所以它回传的模型必然属于它同时
 * 回传的这套接入配置，可以直接标记为「为当前连接所选」。不标记的话，
 * 用户只改了接口地址、没重新选模型时保存会把模型一并丢掉。
 */
export function adoptSettings(settings) {
  reconcileAuthorizedModel(settings, settings.authorization);
  settings.modelChosenForConnection = Boolean((settings.model ?? '').trim());
  rememberAuthorizedModelCatalog(settings);
  return settings;
}

/**
 * 记录设置页刚获取的目录，供保存后的对话选择器直接复用。
 * 返回 false 说明用户已在等待期间改了 API 或密钥，旧响应不能更新当前表单。
 */
function rememberFetchedModels(connection, models, tokenRevision, catalogRevision) {
  const stillCurrent = modelCatalogKey(connection) === modelCatalogKey(modelConnectionSettings()) &&
    (connection.mode !== 'CustomApi' || tokenRevision === customTokenRevision);

  if (!stillCurrent || !putModelCatalog(connection, models, catalogRevision)) {
    return false;
  }

  lastModelFetch = {
    key: modelCatalogKey(connection),
    tokenRevision,
  };
  return true;
}

function connectionModels() {
  return connectionSnapshots.get(modelCatalogKey(current))?.models ??
    current.authorization?.models ?? getModelCatalog(current);
}

function rememberConnection(models, probe) {
  if (!current) { return; }
  const key = modelCatalogKey(current);
  const previous = connectionSnapshots.get(key);
  connectionSnapshots.set(key, {
    model: current.model,
    effectiveModel: current.effectiveModel,
    modelChosenForConnection: current.modelChosenForConnection,
    authorization: current.authorization,
    workbuddyRuntime: current.workbuddyRuntime,
    ready: current.ready,
    readyDetail: current.readyDetail,
    models: models ?? previous?.models ?? current.authorization?.models ?? getModelCatalog(current),
    probe: probe ?? previous?.probe,
    lastModelFetch: lastModelFetch?.key === key ? lastModelFetch : previous?.lastModelFetch,
  });
}

function activateConnection() {
  cancelAccountRefresh();
  modeRevision += 1;
  activeModelRequest = null;
  modelFetchState = { phase: 'idle', detail: '' };
  workBuddyAuthUrl = '';
  Object.assign(current, {
    model: '', effectiveModel: '', modelChosenForConnection: false,
    authorization: null, workbuddyRuntime: null, ready: false, readyDetail: '',
  });
  const cached = connectionSnapshots.get(modelCatalogKey(current));
  if (cached) {
    for (const key of ['model', 'effectiveModel', 'modelChosenForConnection', 'authorization', 'workbuddyRuntime', 'ready', 'readyDetail']) {
      current[key] = cached[key];
    }
    lastModelFetch = cached.lastModelFetch;
  }
  setStatus('');
  clearWorkBuddyCheckin();
  render();
  void refreshConnection();
  if (isDomesticWorkBuddyMode(current.mode)) {
    void refreshWorkBuddyCheckin({ active: true, mode: current.mode });
  }
}

function renderModeSection() {
  const options = [
    { value: 'LocalCli', label: '① 使用本机 CLI 配置' },
    { value: 'CustomApi', label: '② 自定义接口' },
    { value: 'Authorized', label: '③ WorkBuddy 授权登录' },
    { value: 'AuthorizedInternational', label: '④ WorkBuddy 国际版授权登录' },
  ];

  const node = select('mode', options, current.mode);
  node.disabled = workBuddyBusy;
  node.addEventListener('change', () => {
    if (current.mode === node.value) { return; }
    rememberConnection();
    resetModelOnModeChange(current, node.value);
    activateConnection();
  });

  return field('接入模式', node);
}

function renderLocalCliSection() {
  const section = el('div', 'section');
  section.append(el('div', 'section-title', '本机 CLI 配置'));

  const node = select(
    'cliSource',
    Object.entries(CLI_LABELS).map(([value, label]) => ({ value, label })),
    current.cliSource,
  );
  node.addEventListener('change', () => {
    if (node.value === current.cliSource) { return; }
    rememberConnection();
    current.cliSource = node.value;
    activateConnection();
  });
  section.append(field('使用哪个 CLI', node, '读取该 CLI 配置文件中的接口地址与密钥，不启动 CLI 进程。'));

  const probeBox = el('div', 'probe');
  probeBox.id = 'probe-result';
  const probe = connectionSnapshots.get(modelCatalogKey(current))?.probe;
  renderProbe(probeBox, probe);
  section.append(probeBox);
  return section;
}

/**
 * 把 CLI 配置自带的模型登记为当前生效模型。
 *
 * 只写 effectiveModel（后端计算出的只读字段，保存时会被剔除），不写 model：
 * 模型名一栏留空才表示「跟随 CLI 配置」，写进去反而会把它固化成用户的选择。
 * 候选顺序与后端 Resolve 一致：指定了 CLI 就用那个，Auto 时优先 Claude。
 */
function adoptCliConfiguredModel(usable) {
  if (current.mode !== 'LocalCli' || (current.model ?? '').trim()) {
    return;
  }

  const match = current.cliSource === 'Auto'
    ? usable.find((c) => c.model)
    : usable.find((c) => c.kind === current.cliSource && c.model);

  if (match) {
    current.effectiveModel = match.model;
  }
}

/** 自动切换与手动刷新共用同一流程；渲染不再发起网络请求。 */
async function refreshConnection({ force = false } = {}) {
  if (!current || workBuddyBusy) { return; }
  const connection = modelConnectionSettings();
  const key = modelCatalogKey(connection);
  const tokenRevision = customTokenRevision;
  const requestModeRevision = modeRevision;
  if (connection.mode === 'CustomApi' &&
    (!connection.customBaseUrl?.trim() || (!customTokenDraft.trim() && !current.hasCustomToken))) {
    modelFetchState = { phase: 'incomplete', detail: '填写接口地址和密钥后自动获取模型。' };
    current.ready = false;
    current.readyDetail = modelFetchState.detail;
    render();
    return;
  }

  let entry = modelRequests.get(key);
  if (!entry || (connection.mode === 'CustomApi' && entry.tokenRevision !== tokenRevision)) {
    entry = {
      id: `settings-models-${++modelRequestSequence}`,
      tokenRevision,
      catalogRevision: modelCatalogRevision(connection),
    };
    const payload = { mode: connection.mode, cliSource: connection.cliSource, force, requestId: entry.id };
    if (connection.mode === 'CustomApi') {
      payload.protocol = connection.customProtocol;
      payload.baseUrl = connection.customBaseUrl;
      payload.token = customTokenDraft;
    }
    entry.task = Promise.allSettled([
      request('models.list', payload, { timeout: 60000 }),
      connection.mode === 'LocalCli' ? request('cli.probe') : Promise.resolve(null),
    ]).then(([models, probe]) => {
      if (probe.status === 'fulfilled') { entry.probe = probe.value; }
      if (models.status === 'rejected') { throw models.reason; }
      return models.value;
    }).finally(() => {
      if (modelRequests.get(key) === entry) { modelRequests.delete(key); }
    });
    modelRequests.set(key, entry);
  }
  activeModelRequest = entry;
  modelFetchState = { phase: 'loading', detail: '正在获取当前连接的模型…' };
  setStatus('');
  render();
  const stillCurrent = () => current && activeModelRequest === entry &&
    modeResponseIsCurrent(connection.mode, requestModeRevision, current.mode, modeRevision) &&
    key === modelCatalogKey(current) &&
    (connection.mode !== 'CustomApi' || tokenRevision === customTokenRevision);
  try {
    const result = await entry.task;
    if (!stillCurrent()) { return; }
    if (result.runtime) { current.workbuddyRuntime = result.runtime; }
    if (isWorkBuddyMode(connection.mode) && !result.authorization) {
      // WorkBuddy 模型响应必须同时给出授权三态；缺失字段不能沿用上一次的
      // “已授权”，否则组件异常会被误报成可用连接。
      throw new Error('暂时无法确认 WorkBuddy 授权状态，请稍后重试。');
    }
    if (isWorkBuddyMode(connection.mode) && result.authorization?.status === 'unavailable') {
      throw new Error(result.authorization.detail || '暂时无法确认 WorkBuddy 连接，请稍后重试。');
    }
    const models = result.models ?? [];
    if (!rememberFetchedModels(connection, models, tokenRevision, entry.catalogRevision)) {
      return;
    }
    if (isWorkBuddyMode(connection.mode) && result.authorization) {
      current.authorization = result.authorization;
      reconcileAuthorizedModel(current, result.authorization);
    }
    if (connection.mode === 'LocalCli' && entry.probe) {
      adoptCliConfiguredModel((entry.probe.candidates ?? []).filter(candidate => candidate.usable));
    }
    current.ready = Boolean(current.model || current.effectiveModel) &&
      (!isWorkBuddyMode(connection.mode) || current.authorization?.status === 'authorized');
    current.readyDetail = models.length ? `已获取 ${models.length} 个模型` :
      current.authorization?.detail || '接口未提供模型目录，可手动填写模型名。';
    modelFetchState = { phase: models.length ? 'ready' : 'empty', detail: current.readyDetail };
    rememberConnection(models, entry.probe);
  } catch (error) {
    if (!stillCurrent()) { return; }
    const detail = String(error?.message || '连接暂时不可用，请稍后重试。');
    modelFetchState = { phase: 'error', detail };
    current.ready = false;
    current.readyDetail = detail;
    if (isWorkBuddyMode(connection.mode)) {
      current.authorization = {
        ...current.authorization, status: 'unavailable', detail,
        models: connectionModels() ?? [],
      };
    }
    rememberConnection(undefined, entry.probe);
    setStatus(`获取模型失败：${detail}`, 'error');
  } finally {
    if (stillCurrent()) {
      activeModelRequest = null;
      if (modelFetchState.phase === 'loading') { modelFetchState = { phase: 'idle', detail: '' }; }
      render();
    }
  }
}

/** 用模型列表填充下拉框。 */
export function populateModelList(models, listTarget = null, modelTarget = null) {
  const list = listTarget ?? document.getElementById('model-list');
  const model = modelTarget ?? document.getElementById('model');
  if (!list) {
    return;
  }

  const options = normalizeModelOptions(models);
  const selectedModel = current?.model ?? model?.value ?? '';

  list.replaceChildren();
  const placeholder = el('option', null, `已获取 ${options.length} 个模型，选择一个`);
  placeholder.value = '';
  list.append(placeholder);

  for (const option of options) {
    const item = el('option', null, option.label);
    item.value = option.id;
    if (option.id === selectedModel) { item.selected = true; }
    if (option.label !== option.id) { item.title = option.id; }
    list.append(item);
  }

  list.hidden = false;

  // 只有一个模型时直接选中，省掉一次点击。
  if (options.length === 1 && model && !selectedModel) {
    model.value = options[0].id;
    if (current) { markModelChosen(options[0].id); }
  }
}

export function normalizeModelOptions(models) {
  const entries = [];
  const seen = new Set();
  for (const entry of models ?? []) {
    const id = typeof entry === 'string'
      ? entry.trim()
      : String(entry?.modelId ?? entry?.id ?? '').trim();
    if (!id || seen.has(id)) { continue; }
    seen.add(id);
    const name = typeof entry === 'string'
      ? id
      : String(entry?.name ?? '').trim() || id;
    const rawMultiplier = typeof entry === 'string'
      ? ''
      : String(entry?.multiplier ?? '').trim();
    entries.push({
      id,
      name,
      multiplier: /^\d+(?:\.\d+)?x$/i.test(rawMultiplier) ? rawMultiplier : '',
    });
  }

  const nameCounts = new Map();
  for (const entry of entries) {
    nameCounts.set(entry.name, (nameCounts.get(entry.name) ?? 0) + 1);
  }

  return entries.map((entry) => {
    const multiplier = entry.multiplier ? ` ${entry.multiplier}` : '';
    const duplicateId = nameCounts.get(entry.name) > 1 ? ` · ${entry.id}` : '';
    return {
      id: entry.id,
      label: `${entry.name}${multiplier}${duplicateId}`,
    };
  });
}

function renderProbe(box, result) {
  if (!result) {
    box.textContent = modelFetchState.phase === 'error' ? '暂时无法读取 CLI 配置。' : '正在检测 CLI 配置…';
    return;
  }
  for (const candidate of result.candidates ?? []) {
    const row = el('div', 'probe-row');
    const dot = el('span', candidate.usable ? 'probe-dot is-ok' : 'probe-dot is-error');
    const name = el('span', 'probe-name', candidate.displayName);
    const detail = el('span', 'probe-detail', candidate.usable
      ? `${candidate.baseUrl}${candidate.model ? ` · ${candidate.model}` : ''}`
      : candidate.detail);
    row.append(dot, name, detail);
    box.append(row);
  }
  if (!(result.candidates ?? []).some(candidate => candidate.usable)) {
    appendFieldHelp(box, '未检测到可用配置。若 CLI 使用订阅登录而非 API 密钥，请改用「自定义接口」模式。');
  }
}

function renderCustomApiSection() {
  const section = el('div', 'section');
  section.append(el('div', 'section-title', '自定义接口'));

  const protocolOptions = (current.protocols ?? []).map((p) => ({ value: p.id, label: p.label }));
  const protocol = select('customProtocol', protocolOptions, current.customProtocol);
  protocol.addEventListener('change', () => {
    rememberConnection();
    current.customProtocol = protocol.value;
    activateConnection();
  });
  section.append(field('接口协议', protocol));

  const baseUrl = input('customBaseUrl', current.customBaseUrl, 'text', 'https://api.example.com/v1');
  baseUrl.addEventListener('input', () => { current.customBaseUrl = baseUrl.value; });
  baseUrl.addEventListener('change', () => { activateConnection(); });
  section.append(field('接口地址', baseUrl, '可填根地址或完整端点，会自动规范化。'));

  const token = input('customToken', customTokenDraft, 'password',
    current.hasCustomToken ? `已保存 ${current.maskedToken}，留空则不修改` : '填入密钥');
  token.addEventListener('input', () => {
    customTokenDraft = token.value;
    customTokenRevision += 1;
    invalidateModelCatalog(current);
    const cached = connectionSnapshots.get(modelCatalogKey(current));
    if (cached) { cached.models = null; cached.lastModelFetch = null; }
    lastModelFetch = null;
    activeModelRequest = null;
    modelFetchState = { phase: 'idle', detail: '' };
  });
  token.addEventListener('change', () => { void refreshConnection(); });
  section.append(field('密钥', token, '使用 Windows DPAPI 加密保存在本机，不会明文落盘，也不会发送给面板以外的任何地方。'));

  return section;
}

export function authorizationView(authorization = {}, international = false) {
  const status = authorization.status ?? 'checking';
  const count = Array.isArray(authorization.models) ? authorization.models.length : 0;
  const detail = authorization.detail || '尚未读取 WorkBuddy 授权状态。';

  return {
    status,
    statusClass: status === 'authorized'
      ? 'notice notice-ok'
      : status === 'checking' ? 'notice' : status === 'unauthorized' ? 'notice notice-warn' : 'notice notice-error',
    count,
    detail,
    hint: status === 'authorized'
      ? `当前账号可选择 ${count} 个模型。选择并保存后即可回到对话页使用。`
      : status === 'unauthorized'
        ? (international
          ? '点击「浏览器登录」并使用 Google 或 GitHub 完成国际版授权。'
          : '点击「浏览器登录」完成授权后，账号和可用模型会自动刷新。')
        : international
          ? '安装国际版独立授权组件后即可在浏览器登录，无需安装完整的 WorkBuddy 桌面端。'
          : '请启动或安装国内版 WorkBuddy 桌面端，再点击浏览器登录。',
  };
}

export function omitReadOnlySettingsFields(payload) {
  for (const key of [
    'protocols', 'maskedToken', 'hasCustomToken',
    'ready', 'readyDetail', 'effectiveModel',
    'thinkingLevels', 'approvalPolicies',
    'toolProtocolOptions',
    'authorization',
    'workbuddyRuntime',
    'onlyFavoriteModels', 'favorites', 'availability',
  ]) {
    delete payload[key];
  }

  return payload;
}

function renderAuthorizedSection() {
  const section = el('div', 'section workbuddy-section');
  const international = current.mode === 'AuthorizedInternational';
  const accountRow = el('div', 'workbuddy-account-row');

  const view = authorizationView(current.authorization ?? {}, international);
  const countLabel = view.count > 0 ? ` · ${view.count} 个模型` : '';
  const checking = Boolean(activeModelRequest) || view.status === 'checking';
  const statusLabel = checking
    ? (view.count > 0 ? `正在确认${countLabel}` : '正在连接…') : view.status === 'authorized'
    ? `已授权${countLabel}`
    : view.status === 'unauthorized' ? '未授权' : `连接待确认${countLabel}`;
  const status = el('div', `workbuddy-status ${checking ? 'is-checking' : view.status === 'authorized'
    ? 'notice-ok' : view.status === 'unauthorized' || view.count > 0 ? 'notice-warn' : 'notice-error'}`, statusLabel);
  status.setAttribute('role', 'status');
  status.title = view.detail;
  status.setAttribute('aria-label', `${statusLabel}：${view.detail}`);

  const runtime = current.workbuddyRuntime ?? {};
  clearDuplicateStatus(view.detail, runtime.detail);
  const loginAvailable = workBuddyLoginAvailable(runtime);
  const runtimeLabel = runtime.available
    ? '授权组件可用'
    : runtime.standalone ? '授权组件已安装' : current.workbuddyRuntime ? '等待组件连接' : '检测组件中';
  const runtimeStatus = el('div', 'workbuddy-runtime', runtimeLabel);
  const runtimeDetail = runtime.detail || '正在检测授权组件…';
  runtimeStatus.title = runtimeDetail;
  runtimeStatus.setAttribute('aria-label', `${runtimeLabel}：${runtimeDetail}`);
  const state = el('div', 'workbuddy-state');
  state.append(el('div', 'section-title', international ? 'WorkBuddy 国际版' : 'WorkBuddy 国内版'));
  const stateLine = el('div', 'workbuddy-state-line');
  stateLine.append(status);
  state.append(stateLine);

  const actions = el('div', 'workbuddy-actions');
  const primaryActions = el('div', 'workbuddy-action-primary');
  const secondaryActions = el('div', 'workbuddy-action-secondary');
  if (international && current.workbuddyRuntime && !runtime.available && !runtime.standalone) {
    const install = el('button', 'btn workbuddy-install', '安装独立组件');
    install.type = 'button';
    install.title = '安装国际版独立授权组件';
    install.disabled = workBuddyBusy;
    install.addEventListener('click', () => { void runWorkBuddyAction('install'); });
    secondaryActions.append(install);
  }
  const login = el('button', `btn workbuddy-login${view.status === 'authorized' ? '' : ' btn-primary'}`, view.status === 'authorized'
    ? '切换账号'
    : international ? 'Google/GitHub 登录' : '浏览器登录');
  login.id = 'workbuddy-login';
  login.type = 'button';
  login.title = view.status === 'authorized' ? '在官方客户端切换账号，返回后刷新' : '在浏览器完成 WorkBuddy 授权';
  login.disabled = workBuddyBusy || !loginAvailable;
  login.addEventListener('click', () => { void runWorkBuddyAction('login'); });
  primaryActions.append(login);
  if (workBuddyBusy) {
    const cancel = el('button', 'btn workbuddy-cancel', '取消');
    cancel.type = 'button';
    cancel.title = '取消当前组件操作';
    cancel.addEventListener('click', () => {
      cancel.disabled = true;
      setStatus('正在取消…');
      void request('workbuddy.cancel', { operationId: workBuddyOperationId }).catch(() => setStatus('取消请求未完成，请稍后重试。', 'error'));
    });
    secondaryActions.append(cancel);
  }
  actions.append(primaryActions, secondaryActions);
  accountRow.append(state, actions);
  section.append(accountRow);

  if (workBuddyBusy && workBuddyAuthUrl && workBuddyOperationId && isWorkBuddyMode(current.mode)) {
    const authHint = el('div', 'workbuddy-auth-hint',
      (international
          ? '登录页面已打开，请完成 Google/GitHub 授权；完成后会自动刷新。'
          : '登录页面已打开，请完成浏览器授权；完成后会自动刷新。'));
    authHint.setAttribute('role', 'status');
    authHint.setAttribute('aria-live', 'polite');
    const reopen = el('button', 'btn workbuddy-reopen', '重新打开登录页');
    reopen.type = 'button';
    reopen.title = '在默认浏览器中重新打开官方授权页面';
    reopen.addEventListener('click', async () => {
      const operationId = workBuddyOperationId;
      reopen.disabled = true;
      setStatus('正在重新打开登录页面…');
      try {
        const result = await request('workbuddy.open-auth-url', {
          mode: current.mode,
          operationId,
        }, { timeout: 15000 });
        if (operationId !== workBuddyOperationId) { return; }
        setStatus(result.detail || (result.ok ? '登录页面已重新打开。' : '登录页面无法打开。'), result.ok ? 'ok' : 'error');
      } catch (error) {
        if (operationId !== workBuddyOperationId) { return; }
        setStatus(`登录页面无法打开：${error.message}`, 'error');
      } finally {
        reopen.disabled = false;
      }
    });
    const authActions = el('div', 'workbuddy-auth-actions');
    authActions.append(authHint, reopen);
    section.append(authActions);
  }

  const checkinRow = el('div', 'workbuddy-checkin-row');
  if (isDomesticWorkBuddyMode(current.mode)) {
    const checkin = el('div', 'workbuddy-checkin-status');
    checkin.id = 'workbuddy-checkin-detail';
    const checkinState = checkinView(getWorkBuddyCheckin());
    checkin.textContent = checkinState.visible ? checkinState.text : '签到：登录后同步';
    checkin.title = `${checkinState.visible
      ? checkinState.detail
      : '登录后自动查询今日签到；未签到时自动签到，已签到时跳过。'} 按北京时间每天刷新一次；面板关闭后不在后台签到。`;
    checkinRow.append(checkin);
    const refresh = el('button', 'btn workbuddy-refresh', '刷新签到');
    refresh.type = 'button';
    refresh.title = '重新查询今日签到状态';
    refresh.disabled = workBuddyBusy || !runtime.available;
    refresh.addEventListener('click', async () => {
      refresh.disabled = true;
      await refreshWorkBuddyCheckin({ active: true, force: true, mode: current.mode });
      refresh.disabled = false;
    });
    checkinRow.append(refresh);
  } else {
    const checkin = el('div', 'workbuddy-checkin-status', '签到：不适用');
    checkin.title = '国际版不调用国内每日签到服务。';
    checkin.setAttribute('aria-label', '签到：不适用。国际版不调用国内每日签到服务。');
    checkinRow.append(checkin);
  }
  const meta = el('div', 'workbuddy-meta');
  meta.append(runtimeStatus, checkinRow);
  section.append(meta);

  return section;
}

function cancelAccountRefresh() {
  if (workBuddyRefreshTimer !== null) { clearTimeout(workBuddyRefreshTimer); }
  workBuddyRefreshTimer = null;
}

function scheduleAccountRefresh(mode, revision) {
  cancelAccountRefresh();
  let remaining = 12;
  const refresh = async () => {
    workBuddyRefreshTimer = null;
    if (!current || !modeResponseIsCurrent(mode, revision, current.mode, modeRevision) || workBuddyBusy) { return; }
    if (!activeModelRequest) { await refreshConnection({ force: true }); }
    if (--remaining > 0 && current && modeResponseIsCurrent(mode, revision, current.mode, modeRevision) && !workBuddyBusy) {
      workBuddyRefreshTimer = setTimeout(refresh, 10000);
    }
  };
  workBuddyRefreshTimer = setTimeout(refresh, 2000);
}

async function runWorkBuddyAction(action) {
  if (workBuddyBusy) { return; }
  cancelAccountRefresh();
  const switchAccount = action === 'login' && current.authorization?.status === 'authorized';
  // 账号/组件操作会刷新授权目录与运行时状态；使操作前发出的旧请求失效，
  // 即使模式名称没变也不能让旧账号结果晚到覆盖新账号。
  modeRevision += 1;
  const actionMode = current.mode;
  const actionModeRevision = modeRevision;
  let accountClientOpened = false;
  activeModelRequest = null;
  modelRequests.delete(modelCatalogKey(current));
  modelFetchState = { phase: 'idle', detail: '' };
  workBuddyBusy = true;
  workBuddyAuthUrl = '';
  workBuddyOperationId = `workbuddy-${Date.now()}-${++modelRequestSequence}`;
  render();
  setStatus(action === 'install' ? '正在安装官方独立组件…' : !switchAccount ? '正在读取本机登录状态…' :
    '正在打开官方客户端，请在其中切换账号…');
  try {
    const result = await request(`workbuddy.${action}`, { mode: actionMode, switchAccount, operationId: workBuddyOperationId }, { timeout: action === 'install' ? 720000 : 420000 });
    if (!current || !modeResponseIsCurrent(actionMode, actionModeRevision, current.mode, modeRevision)) {
      return;
    }
    if (result.runtime) { current.workbuddyRuntime = result.runtime; }
    accountClientOpened = switchAccount && result.ok === true;
    if (result.authorization) {
      current.authorization = result.authorization.status === 'unavailable'
        ? { ...result.authorization, models: connectionModels() ?? [] } : result.authorization;
      reconcileAuthorizedModel(current, result.authorization);
      current.ready = result.authorization.status === 'authorized' && Boolean(current.model);
      current.readyDetail = result.authorization.detail ?? '';
      rememberAuthorizedModelCatalog(current);
      rememberConnection(current.authorization.models);
    }
    if (result.ok) { workBuddyAuthUrl = ''; }
    if (result.authorization) { setWorkBuddyCheckin(isDomesticWorkBuddyMode(current.mode) ? (result.checkin ?? null) : null); }
    setStatus(result.detail || '操作已完成。', result.ok ? 'ok' : 'error');
  } catch (error) {
    const detail = String(error?.message ?? '').trim();
    setStatus(detail || '组件操作未完成，请检查网络或稍后重试。', 'error');
  } finally {
    workBuddyBusy = false;
    workBuddyOperationId = '';
    workBuddyAuthUrl = '';
    render();
    if (accountClientOpened) { scheduleAccountRefresh(actionMode, actionModeRevision); }
  }
}

function renderModelSection() {
  const section = el('div', 'section');
  const title = el('div', 'section-title', '模型');
  section.append(title);

  const row = el('div', 'row model-entry-row');

  const model = input('model', current.model, 'text', '例如 gpt-4o');
  // 手填的模型同样属于当前这套接入配置，即使它与旧模型同名也不能被当作残留清掉。
  model.addEventListener('input', () => { markModelChosen(model.value); });

  const fetchButton = el('button', 'btn model-fetch-button', activeModelRequest ? '获取中…' : '刷新模型');
  fetchButton.type = 'button';
  fetchButton.title = '重新获取当前接入方式的模型列表';
  fetchButton.disabled = workBuddyBusy || Boolean(activeModelRequest);

  const list = el('select', 'input');
  list.id = 'model-list';
  list.hidden = true;
  list.addEventListener('change', () => {
    if (list.value) {
      model.value = list.value;
      markModelChosen(list.value);
    }
  });

  const models = connectionModels();
  if (models?.length) {
    populateModelList(models, list, model);
  }

  fetchButton.addEventListener('click', () => { void refreshConnection({ force: true }); });

  row.append(model, fetchButton);
  section.append(field('模型名', row));
  section.append(list);
  if (modelFetchState.phase === 'empty' || modelFetchState.phase === 'incomplete') {
    section.append(el('div', 'model-fetch-status', modelFetchState.detail));
  }

  appendFieldHelp(title,
    '思考档位与处理方式已移到对话页输入框上方，那里切换更顺手，无需回到本页。',
    '查看模型设置提示');

  return section;
}

function renderBehaviorSection() {
  const section = el('div', 'section');
  const title = el('div', 'section-title', '行为');
  section.append(title);

  const selection = el('input');
  selection.type = 'checkbox';
  selection.id = 'autoIncludeSelection';
  selection.checked = current.autoIncludeSelection !== false;
  selection.addEventListener('change', () => { current.autoIncludeSelection = selection.checked; });

  const selectionRow = el('label', 'checkbox-row');
  selectionRow.append(selection, el('span', null, '每轮自动附带当前选区信息'));
  section.append(selectionRow);
  appendFieldHelp(title,
    '关闭后，你说「这一列」时我需要先调用工具确认，会多一次往返。',
    '查看选区提示');

  // 三个数值项折叠起来：它们有合理默认值，多数用户不需要调整，
  // 平铺会让设置页显得冗长，掩盖真正需要填的接入信息。
  const advanced = el('details', 'advanced');
  const summary = el('summary', 'advanced-summary', '高级参数');
  advanced.append(summary);

  const budget = input('contextBudgetTokens', current.contextBudgetTokens, 'number');
  budget.min = '8000';
  budget.addEventListener('input', () => { current.contextBudgetTokens = Number(budget.value); });
  advanced.append(field('上下文预算（tokens）', budget,
    '达到 90% 时自动压缩较早的工具结果，必要时移除最早的记录。'));

  const maxTokens = input('maxOutputTokens', current.maxOutputTokens, 'number');
  maxTokens.min = '256';
  maxTokens.addEventListener('input', () => { current.maxOutputTokens = Number(maxTokens.value); });
  advanced.append(field('单次回复上限（tokens）', maxTokens,
    '思考模式下这个值也约束思考长度，设太小会让模型来不及给出结论。'));

  const steps = input('maxSteps', current.maxSteps, 'number');
  steps.min = '1';
  steps.addEventListener('input', () => { current.maxSteps = Number(steps.value); });
  advanced.append(field('单轮最多工具步数', steps, '防止模型陷入循环。达到上限会明确告知。'));

  advanced.append(renderCapabilityFields());

  section.append(advanced);
  return section;
}

/**
 * 模型能力相关的两项。
 *
 * 放在高级参数里：默认的「自动探测」对绝大多数模型都是对的，需要手动指定的
 * 是那些服务端既不报错、也不真的调用工具的网关——探测对它们无从下手，
 * 而遇到这种情况的用户本就在找开关。
 */
function renderCapabilityFields() {
  const wrap = el('div', 'capability-fields');

  const protocolOptions = (current.toolProtocolOptions ?? []).map((o) => ({
    value: o.id,
    label: o.label,
  }));

  // 后端没下发选项时不画这个控件：画一个空下拉只会让人以为功能坏了。
  if (protocolOptions.length > 0) {
    const protocol = select('toolProtocol', protocolOptions, current.toolProtocol ?? 'Auto');
    protocol.addEventListener('change', () => { current.toolProtocol = protocol.value; });

    const hint = (current.toolProtocolOptions ?? [])
      .map((o) => `${o.label}：${o.hint}`)
      .join('；');
    wrap.append(field('工具调用方式', protocol, hint));
  }

  const relay = input('visionRelayModel', current.visionRelayModel, 'text', '例如 gpt-4o-mini');
  relay.addEventListener('input', () => { current.visionRelayModel = relay.value; });
  wrap.append(field('视觉中转模型', relay,
    '主模型看不了图片时，先用这个模型把图转成文字。沿用同一套接口地址与密钥，只换模型名；' +
    '留空则遇到图片时去掉图片继续，并提示你换模型。'));

  return wrap;
}

/** 诊断入口：把日志与安装信息集中到设置页底部，便于排查。 */
function renderDiagnosticsSection() {
  const section = el('div', 'section');
  const title = el('div', 'section-title', '排查');
  section.append(title);
  appendFieldHelp(title,
    '诊断页列出宿主版本、注册状态与 WebView2 版本；' +
    '详细日志在 %LOCALAPPDATA%\\ChatSheet\\logs，面板自身的状态也会写入同一份。' +
    ' 功能区 ChatSheet 选项卡里的「诊断」按钮也到同一个页。',
    '查看诊断信息');

  // 面板里必须有一个可点的入口。诊断没有页签（三个页签会把标题挤掉，
  // 而它是出问题时才去一次的地方），此前入口只剩功能区那个按钮——
  // 于是这一段提到了一个页，却没人在面板里找得到它。
  const open = el('button', 'btn', '打开诊断');
  open.type = 'button';
  open.title = '查看宿主版本、注册状态与 WebView2 运行时版本';
  // 改 hash 而不是直接调用路由函数：app.js 监听 hashchange，
  // 由它统一决定怎么切页，两个模块不必互相引用。
  open.addEventListener('click', () => { window.location.hash = 'diagnostics'; });
  section.append(open);

  return section;
}

/** 校验当前配置是否足以发起对话，返回问题清单。 */
function validate() {
  const problems = [];

  if (current.mode === 'CustomApi') {
    if (!current.customBaseUrl || current.customBaseUrl.trim() === '') {
      problems.push('请填写接口地址');
    }

    const tokenInput = document.getElementById('customToken');
    const hasNewToken = tokenInput && tokenInput.value.trim() !== '';
    if (!hasNewToken && !current.hasCustomToken) {
      problems.push('请填写接口密钥');
    }
  }

  // 模式 ① 下 CLI 配置可能自带模型，此时不必强制填写。
  // effectiveModel 由后端解析得出，是权威值。
  const hasModel = (current.model && current.model.trim() !== '') ||
    (current.mode === 'LocalCli' && current.effectiveModel);
  // Authorized 可以先保存模式，再在 WorkBuddy 登录后获取并选择模型。
  if (!hasModel && !isWorkBuddyMode(current.mode)) {
    problems.push('请填写或选择模型');
  }

  return problems;
}

/** 就绪状态条：让用户一眼看出当前配置能否发起对话。 */
function renderReadyBanner() {
  if (isWorkBuddyMode(current.mode)) {
    return null;
  }

  const loading = Boolean(activeModelRequest);
  const connected = ['ready', 'empty'].includes(modelFetchState.phase);
  const banner = el('div', loading ? 'ready-banner' : connected || current.ready ? 'ready-banner is-ok' : 'ready-banner is-warn');
  const icon = el('span', 'ready-dot');
  const text = el('span', 'ready-text', loading ? '正在获取当前连接的状态与模型…'
    : modelFetchState.phase === 'error' ? '连接待确认；可重试，已有模型选择已保留。'
    : connected ? `接口已连接：${modelFetchState.detail}` : current.ready
    ? `配置就绪：${current.readyDetail}`
    : `配置未完成：${current.readyDetail || '请填写下方必填项'}`);
  banner.append(icon, text);
  return banner;
}

function render() {
  form.replaceChildren();
  const readyBanner = renderReadyBanner();
  if (readyBanner) { form.append(readyBanner); }
  form.append(renderModeSection());

  if (current.mode === 'LocalCli') {
    form.append(renderLocalCliSection());
  } else if (current.mode === 'CustomApi') {
    form.append(renderCustomApiSection());
  } else {
    form.append(renderAuthorizedSection());
  }

  form.append(renderModelSection());
  form.append(renderBehaviorSection());
  form.append(renderDiagnosticsSection());

  // 保存按钮吸附在底部：设置页内容较长，滚到中途也能随时保存，
  // 不必回到页尾找按钮。
  const actions = el('div', 'settings-actions');
  const save = el('button', 'btn btn-primary', '保存设置');
  save.type = 'button';
  save.disabled = workBuddyBusy;
  save.addEventListener('click', async () => {
    // 保存前校验必填项。否则配置不完整也能存下，
    // 用户要到发送消息时才发现问题，反馈链路太长。
    const problems = validate();
    if (problems.length > 0) {
      setStatus(problems.join('；'), 'error');
      return;
    }

    save.disabled = true;
    try {
      const payload = { ...current };
      const requestMode = payload.mode;
      const requestModeRevision = modeRevision;
      const connection = modelConnectionSettings();
      const connectionKey = modelCatalogKey(connection);
      const tokenInput = document.getElementById('customToken');
      const hasNewToken = Boolean(tokenInput && tokenInput.value.trim() !== '');
      const hasFreshCatalog = lastModelFetch?.key === connectionKey &&
        (!hasNewToken || lastModelFetch.tokenRevision === customTokenRevision);
      // 留空表示不修改已保存的密钥，不能传空串（那会清除密钥）。
      if (hasNewToken) {
        payload.customToken = tokenInput.value.trim();
      }
      // 这些是后端计算出的只读字段，不参与保存。
      //
      // onlyFavoriteModels、favorites、availability 三项也在这里删掉，但理由不同：
      // 它们不是只读的，而是归选择器管的。current 是本页打开时的快照
      // （initSettings 由 app.js 的 settingsLoaded 一次性守卫保护，整个面板生命周期
      // 只跑一次），用户在对话页拨过开关后再回本页点保存，把快照原样送回去
      // 就会把刚拨的开关写回旧值。
      omitReadOnlySettingsFields(payload);

      const saved = await request('settings.save', payload);
      // 用户可能在保存期间切换了模式。后端响应属于旧模式，不能把它
      // 再写回当前表单，否则国内授权目录会短暂显示到国际版（反之亦然）。
      if (!current || !modeResponseIsCurrent(requestMode, requestModeRevision, current.mode, modeRevision)) {
        setStatus('设置已保存；当前模式已变更，请再次保存。', 'warn');
        return;
      }
      current = adoptSettings(saved);
      if (hasNewToken) { customTokenDraft = ''; }
      if (isWorkBuddyMode(current.mode) && current.authorization?.status === 'unavailable') {
        current.authorization = { ...current.authorization, models: connectionModels() ?? [] };
      }
      // 明确未授权返回空目录时也要覆盖旧快照；暂时失败的刷新路径仍会
      // 传 undefined，从而保留上一份目录。
      rememberConnection(isWorkBuddyMode(current.mode) ? current.authorization?.models : undefined);
      clearWorkBuddyCheckin();
      if (isDomesticWorkBuddyMode(current.mode)) {
        void refreshWorkBuddyCheckin({ mode: current.mode });
      }
      // 同一地址换密钥时账号可见的模型也可能不同。若没有用这份新密钥
      // 成功获取过目录，保存后必须让对话选择器按需重新获取。
      if (hasNewToken && !hasFreshCatalog) {
        invalidateModelCatalog(current);
      }
      setStatus('已保存。', 'ok');
      render();
    } catch (error) {
      setStatus(`保存失败：${error.message}`, 'error');
    } finally {
      save.disabled = false;
    }
  });

  actions.append(save);
  form.append(actions);

  // 渲染完成后上报布局：设置页控件多，窄栏下最容易横向溢出，
  // 需要在真实宿主宽度下实测而不是靠肉眼。
  void reportLayout();
}

async function reportLayout() {
  // 等一帧，确保浏览器已完成布局计算。
  await new Promise((resolve) => requestAnimationFrame(() => resolve()));

  const overflowing = [];
  for (const node of form.querySelectorAll('.input, .btn, .row, .probe')) {
    if (node.scrollWidth > node.clientWidth + 1) {
      overflowing.push(`${node.className}(${node.scrollWidth}>${node.clientWidth})`);
    }
  }

  const docOverflow = document.documentElement.scrollWidth > document.documentElement.clientWidth;

  await logToHost(
    `设置页布局：视口宽 ${window.innerWidth} 表单宽 ${Math.round(form.getBoundingClientRect().width)} ` +
      `控件 ${form.querySelectorAll('.input').length} 个 ` +
      `页面横向溢出=${docOverflow ? '有' : '无'} ` +
      `溢出控件=${overflowing.length === 0 ? '无' : overflowing.join(', ')}`,
    overflowing.length > 0 || docOverflow ? 'warn' : 'info',
  );
}

export async function initSettings() {
  form = document.getElementById('settings-form');
  statusLine = document.getElementById('settings-status');

  // 获取模型列表期间的重试进度。退避等待可达数十秒，
  // 不说明的话「获取中…」看起来就是卡住了。
  on('models-retry', (message) => {
    if (activeModelRequest && message.requestId === activeModelRequest.id) {
      setStatus(message.text ?? '正在重试…', 'warn');
    }
  });
  on('workbuddy.progress', message => {
    if (workBuddyBusy && message.operationId === workBuddyOperationId) { setStatus(message.detail); }
  });
  on('workbuddy.auth-url', message => {
    const url = String(message.url ?? '').trim();
    if (!workBuddyBusy || !workBuddyOperationId || message.operationId !== workBuddyOperationId ||
      !url || !/^https:\/\//i.test(url) ||
      (message.mode && message.mode !== current?.mode)) {
      return;
    }
    workBuddyAuthUrl = url;
    if (workBuddyBusy) {
      setStatus(isDomesticWorkBuddyMode(current?.mode)
        ? '登录页面已打开，请完成浏览器授权；完成后会自动刷新。'
        : '登录页面已打开，请完成 Google/GitHub 授权；完成后会自动刷新。', 'ok');
      render();
    }
  });
  const refreshOnReturn = () => {
    const now = Date.now();
    if (isWorkBuddyMode(current?.mode) && !workBuddyBusy && !activeModelRequest && document.hidden !== true &&
      now - lastReturnRefreshAt >= 3000) {
      lastReturnRefreshAt = now;
      void refreshConnection({ force: true });
    }
  };
  window.addEventListener?.('focus', refreshOnReturn);
  document.addEventListener?.('visibilitychange', refreshOnReturn);
  window.addEventListener?.('pagehide', cancelAccountRefresh);
  subscribeWorkBuddyCheckin(value => {
    const node = document.getElementById('workbuddy-checkin-detail');
    if (node) {
      const view = checkinView(value);
      node.textContent = view.visible ? view.text : '签到：登录后同步';
      node.title = `${view.visible
        ? view.detail
        : '登录后自动查询今日签到；未签到时自动签到，已签到时跳过。'} 按北京时间每天刷新一次；面板关闭后不在后台签到。`;
      node.className = `workbuddy-checkin-status${view.checked ? ' is-ok' : ''}`;
    }
  });

  try {
    current = adoptSettings(await request('settings.get'));
    rememberConnection();
    render();
    void refreshConnection();
    if (isDomesticWorkBuddyMode(current.mode)) { void refreshWorkBuddyCheckin({ active: true, mode: current.mode }); }
  } catch (error) {
    setStatus(`读取设置失败：${error.message}`, 'error');
  }
}
