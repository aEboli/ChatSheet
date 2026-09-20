// 驱动真实设置页事件和消息桥，验证四种接入模式的自动获取与状态恢复。
// 所有账号、接口和密钥均为内存 fixture，不访问网络或用户设置。

class Element {
  constructor(tag) {
    this.tagName = tag.toUpperCase();
    this.children = [];
    this.attributes = {};
    this.handlers = new Map();
    this.className = '';
    this.dataset = {};
    this.hidden = false;
    this.clientWidth = 500;
    this.scrollWidth = 500;
    this.classList = {
      contains: value => this.className.split(/\s+/).includes(value),
      toggle: (value, on) => {
        const classes = new Set(this.className.split(/\s+/).filter(Boolean));
        if (on) { classes.add(value); } else { classes.delete(value); }
        this.className = [...classes].join(' ');
      },
    };
  }
  append(...children) {
    for (const child of children) {
      if (typeof child === 'object') { child.parentNode = this; }
      this.children.push(child);
    }
  }
  replaceChildren(...children) { this.children = []; this._text = ''; this.append(...children); }
  set textContent(value) { this._text = value; this.children = []; }
  get textContent() { return (this._text ?? '') + this.children.map(child => child.textContent ?? child).join(''); }
  set value(value) { this._value = String(value); }
  get value() {
    return this._value ?? (this.tagName === 'SELECT'
      ? (this.children.find(child => child.selected) ?? this.children[0])?.value ?? '' : '');
  }
  get options() { return this.children; }
  setAttribute(name, value) { this.attributes[name] = String(value); }
  getAttribute(name) { return this.attributes[name]; }
  addEventListener(name, handler) {
    if (!this.handlers.has(name)) { this.handlers.set(name, []); }
    this.handlers.get(name).push(handler);
  }
  dispatchEvent(event) { for (const handler of this.handlers.get(event.type) ?? []) { void handler(event); } }
  click() { if (!this.disabled) { this.dispatchEvent({ type: 'click' }); } }
  getBoundingClientRect() { return { width: 500 }; }
  querySelectorAll(selector) {
    const selectors = selector.split(',').map(value => value.trim());
    return descendants(this).filter(node => selectors.some(value => value.startsWith('.')
      ? node.classList.contains(value.slice(1)) : value.startsWith('#') ? node.id === value.slice(1)
        : node.tagName === value.toUpperCase()));
  }
}

function descendants(node) {
  return node.children.filter(child => typeof child === 'object')
    .flatMap(child => [child, ...descendants(child)]);
}
const root = new Element('main');
for (const id of ['settings-form', 'settings-status', 'workbuddy-checkin']) {
  const node = new Element('div'); node.id = id; root.append(node);
}
const byId = id => descendants(root).find(node => node.id === id) ?? null;
globalThis.document = {
  createElement: tag => new Element(tag),
  getElementById: byId,
  documentElement: { scrollWidth: 500, clientWidth: 500 },
};
globalThis.requestAnimationFrame = callback => queueMicrotask(callback);

const requests = [];
const held = [];
const pausedModes = new Set();
let loginError = '';
let pauseLogin = false;
let pendingLogin;
const windowEvents = new Map();
const scheduledRefreshes = new Map();
const nativeTimeout = globalThis.setTimeout;
const nativeClearTimeout = globalThis.clearTimeout;
globalThis.setTimeout = (callback, delay, ...args) => {
  if (delay === 2000 || delay === 10000) {
    const timer = {};
    scheduledRefreshes.set(timer, { callback, delay });
    return timer;
  }
  return nativeTimeout(callback, delay, ...args);
};
globalThis.clearTimeout = timer => {
  if (!scheduledRefreshes.delete(timer)) { nativeClearTimeout(timer); }
};
async function tickAccountRefresh() {
  const [timer, entry] = scheduledRefreshes.entries().next().value ?? [];
  if (!entry) { return false; }
  scheduledRefreshes.delete(timer);
  await entry.callback();
  return true;
}
let savedAuthorization;
let receive;
const seed = {
  mode: 'CustomApi', cliSource: 'Auto', model: 'manual-api-model', ready: true,
  customProtocol: 'openai-chat-completions', customBaseUrl: 'https://api.example.test/v1',
  hasCustomToken: true, maskedToken: 'saved',
  protocols: [{ id: 'openai-chat-completions', label: 'OpenAI' }],
};
function modelResult(payload) {
  const prefix = payload.mode === 'Authorized' ? 'cn' : payload.mode === 'AuthorizedInternational' ? 'intl'
    : payload.mode === 'LocalCli' ? `cli-${payload.cliSource}` : 'api';
  const models = [1, 2].map(index => ({ modelId: `${prefix}-${index}`, name: `模型 ${index}` }));
  const workbuddy = payload.mode.startsWith('Authorized');
  return {
    models, runtime: { available: true, standalone: true },
    authorization: workbuddy ? {
      status: 'authorized', models, currentModelId: models[0].modelId, detail: '官方授权可用',
    } : undefined,
  };
}
function answer(message, data, error) {
  receive({ data: { kind: 'response', id: message.id, ok: !error, data, error } });
}
globalThis.window = {
  addEventListener: (event, handler) => windowEvents.set(event, handler),
  innerWidth: 500, location: { hash: 'settings' },
  chrome: { webview: {
    addEventListener: (_, handler) => { receive = handler; },
    postMessage(message) {
      requests.push(message);
      if (message.channel === 'workbuddy.login' && pauseLogin) { pendingLogin = message; return; }
      if (message.channel === 'models.list' && pausedModes.has(message.payload.mode)) {
        held.push(message); return;
      }
      queueMicrotask(() => {
        let data = {};
        if (message.channel === 'settings.get') { data = structuredClone(seed); }
        if (message.channel === 'models.list') { data = modelResult(message.payload); }
        if (message.channel === 'workbuddy.runtime') { data = { available: true, standalone: true }; }
        if (message.channel === 'workbuddy.account') { data = null; }
        if (message.channel === 'cli.probe') {
          data = { candidates: ['Claude', 'Codex'].map(kind => ({
            kind, displayName: kind, usable: true, baseUrl: `https://${kind}.example.test/v1`, model: `configured-${kind}`,
          })) };
        }
        if (message.channel === 'settings.save') { data = { ...structuredClone(seed), ...message.payload }; }
        if (message.channel === 'settings.save' && savedAuthorization) { data.authorization = savedAuthorization; }
        if (message.channel === 'workbuddy.login' && loginError) { answer(message, null, loginError); return; }
        answer(message, data);
      });
    },
  } },
};
const settle = async () => { await new Promise(resolve => setImmediate(resolve)); await new Promise(resolve => setImmediate(resolve)); };
async function change(id, value, type = 'change') {
  const node = byId(id); node.value = value; node.dispatchEvent({ type }); await settle();
}
async function release(mode, data, error) {
  pausedModes.delete(mode);
  for (let index = held.length - 1; index >= 0; index--) {
    const message = held[index];
    if (message.payload.mode !== mode) { continue; }
    held.splice(index, 1);
    answer(message, data ?? modelResult(message.payload), error);
  }
  await settle();
}
function fetchButton() { return descendants(root).find(node => node.classList.contains('model-fetch-button')); }
function models(mode) { return requests.filter(message => message.channel === 'models.list' && message.payload.mode === mode); }
function choices() { return byId('model-list').children.map(option => option.value); }
function stateText() { return descendants(root).find(node => node.classList.contains('workbuddy-status'))?.textContent ?? ''; }
let passed = 0;
let failed = 0;
function check(label, condition) {
  console.log(`  ${condition ? '通过' : '失败'}  ${label}`);
  if (condition) { passed++; } else { failed++; }
}

const { initSettings } = await import('../../src/web/scripts/settings.js');
await initSettings(); await settle();
check('打开已配置的自定义接口时自动获取模型', models('CustomApi').length === 1 && choices().includes('api-1'));
check('普通接口的手填模型不被目录强行替换', byId('model').value === 'manual-api-model');
await change('mode', 'LocalCli');
check('切换到本机 CLI 自动读取目录与配置', models('LocalCli').length === 1 && choices().includes('cli-Auto-1'));
await change('model', 'cli-Auto-2', 'input');
await change('mode', 'CustomApi');
await change('mode', 'LocalCli');
check('切回 CLI 保留该连接的模型选择并刷新目录', byId('model').value === 'cli-Auto-2' && choices().includes('cli-Auto-1'));

pausedModes.add('Authorized');
await change('mode', 'Authorized');
check('首次自动获取期间显示加载状态而非组件不可用', /正在|连接中|获取中/.test(stateText()) && !/不可用/.test(stateText()));
check('自动获取期间手动刷新按钮不会重复发请求', fetchButton().disabled && models('Authorized').length === 1);
await release('Authorized');
check('国内模式无需点击登录或获取即可展示授权和模型', /已授权|已连接|已登录/.test(stateText()) && choices().includes('cn-1'));
await change('model-list', 'cn-2');
await change('mode', 'AuthorizedInternational');
check('国际模式自动取得独立目录', models('AuthorizedInternational').length === 1 && choices().includes('intl-1') && !choices().includes('cn-1'));
await change('model-list', 'intl-2');

pausedModes.add('Authorized');
await change('mode', 'Authorized');
check('切回国内立即保留原模型和目录，不等待网络', byId('model').value === 'cn-2' && choices().includes('cn-1'));
const pendingCount = models('Authorized').length;
await change('mode', 'AuthorizedInternational');
await change('mode', 'Authorized');
check('快速 A→B→A 合并同一连接的在途请求', models('Authorized').length === pendingCount);
await release('Authorized');
check('在途请求完成后仍保留该连接的用户选择', byId('model').value === 'cn-2');

pausedModes.add('Authorized'); fetchButton().click(); await settle();
await change('mode', 'AuthorizedInternational');
await release('Authorized', null, '旧请求错误');
check('旧模式手动刷新失败不会覆盖当前状态', !byId('settings-status').textContent.includes('旧请求错误') && choices().includes('intl-1'));
check('切回国际保留国际版模型选择', byId('model').value === 'intl-2');

pausedModes.add('Authorized');
await change('mode', 'Authorized');
await release('Authorized', { models: [], authorization: { status: 'unavailable', detail: '网络暂时不可用', models: [] } });
check('短暂失败保留原目录和模型，并标记待确认', byId('model').value === 'cn-2' && choices().includes('cn-1') && /待确认|未确认|暂时/.test(stateText()));
fetchButton().click(); await settle();
check('刷新恢复后无需重新登录且保留模型选择', /已授权|已连接|已登录/.test(stateText()) && byId('model').value === 'cn-2');

pausedModes.add('Authorized'); fetchButton().click(); await settle();
await release('Authorized', { models: [], authorization: { status: 'unauthorized', detail: '请登录', models: [] } });
check('明确未授权才清空国内模型和目录', byId('model').value === '' && !choices().includes('cn-1') && /未授权|未登录/.test(stateText()));

await change('mode', 'CustomApi');
await change('customToken', 'fixture-draft-token', 'input');
await change('mode', 'AuthorizedInternational');
await change('mode', 'CustomApi');
check('模式切换和刷新重绘不会丢失未保存密钥草稿', byId('customToken').value === 'fixture-draft-token');

await change('mode', 'LocalCli');
const beforeSourceChange = models('LocalCli').length;
await change('cliSource', 'Codex');
check('已有模型时更换 CLI 来源仍自动获取正确目录', models('LocalCli').length === beforeSourceChange + 1 && choices().includes('cli-Codex-1') && !choices().includes('cli-Auto-1'));
check('模式切换不会偷偷保存设置或重新登录', !requests.some(message => message.channel === 'settings.save' || message.channel === 'workbuddy.login'));

pausedModes.add('LocalCli');
await change('cliSource', 'Claude');
const oldCli = held.find(message => message.payload.cliSource === 'Claude');
await change('mode', 'AuthorizedInternational');
receive({ data: { kind: 'models-retry', requestId: oldCli.payload.requestId, text: '旧 CLI 重试' } });
check('旧请求的重试推送不会污染当前状态', !byId('settings-status').textContent.includes('旧 CLI 重试'));
await change('mode', 'LocalCli');
await change('cliSource', 'Codex');
await release('LocalCli');
check('CLI 来源旧响应不会覆盖新来源目录', choices().includes('cli-Codex-1') && !choices().includes('cli-Claude-1'));

await change('mode', 'CustomApi');
pausedModes.add('CustomApi'); fetchButton().click(); await settle();
const oldTokenRequest = held.pop();
await change('customToken', 'fixture-replacement-token', 'input');
await change('customToken', 'fixture-replacement-token');
await release('CustomApi', { models: ['replacement-1', 'replacement-2'] });
answer(oldTokenRequest, { models: ['stale-token-model'] }); await settle();
check('密钥变更后的迟到响应不能覆盖新目录', choices().includes('replacement-1') && !choices().includes('stale-token-model') && !fetchButton().disabled);
await change('model', 'handwritten-model', 'input');
pausedModes.add('CustomApi'); fetchButton().click(); await settle();
await release('CustomApi', { models: [] });
check('空目录保留手填模型并显示手填状态', byId('model').value === 'handwritten-model' && byId('model-list').hidden && root.textContent.includes('可手动填写'));
const originalUrl = byId('customBaseUrl').value;
const beforeIncomplete = models('CustomApi').length;
await change('customBaseUrl', '', 'input');
await change('customBaseUrl', '');
check('自定义接口不完整时不会发出获取请求', models('CustomApi').length === beforeIncomplete && root.textContent.includes('填写接口地址和密钥'));
await change('customBaseUrl', originalUrl, 'input');
await change('customBaseUrl', originalUrl);
check('恢复原接口地址后恢复原模型选择', byId('model').value === 'handwritten-model');

await change('mode', 'AuthorizedInternational');
pausedModes.add('AuthorizedInternational'); fetchButton().click(); await settle();
await release('AuthorizedInternational', { models: [] });
check('缺失授权字段的响应不会伪装已授权或擦除目录', /待确认/.test(stateText()) && choices().includes('intl-1'));
fetchButton().click(); await settle();
loginError = '测试登录已取消';
byId('workbuddy-login').click(); await settle();
check('账号操作失败不改动原授权状态、目录和选择', /已授权/.test(stateText()) && choices().includes('intl-1') && byId('model').value === 'intl-2');
loginError = '';
pauseLogin = true;
byId('workbuddy-login').click(); await settle();
const firstLogin = pendingLogin;
check('显式切换账号请求带独立操作标识', firstLogin.payload.switchAccount === true && !!firstLogin.payload.operationId);
const authNotice = operationId => receive({ data: {
  kind: 'workbuddy.auth-url', mode: 'AuthorizedInternational', operationId,
  url: 'https://www.workbuddy.ai/login?state=fixture',
} });
authNotice('stale-operation'); await settle();
check('旧操作通知不能创建登录提示', !root.textContent.includes('重新打开登录页'));
authNotice(firstLogin.payload.operationId); await settle();
check('当前操作收到授权页后显示重开入口', root.textContent.includes('重新打开登录页'));
descendants(root).find(node => node.classList.contains('workbuddy-reopen')).click(); await settle();
const reopened = requests.findLast(message => message.channel === 'workbuddy.open-auth-url');
check('重开仅传当前操作标识，不接受页面提供的任意链接', reopened.payload.operationId === firstLogin.payload.operationId && !('url' in reopened.payload));
const beforeDoubleClick = requests.filter(message => message.channel === 'workbuddy.login').length;
byId('workbuddy-login').click(); await settle();
check('登录期间重复点击不再拉起新流程', requests.filter(message => message.channel === 'workbuddy.login').length === beforeDoubleClick);
answer(firstLogin, { ok: true, detail: '登录成功', authorization: modelResult(firstLogin.payload).authorization }); await settle();
check('登录成功立即恢复操作并移除旧登录提示', /已授权/.test(stateText()) && !byId('workbuddy-login').disabled && !root.textContent.includes('重新打开登录页'));
const beforeTimedRefresh = models('AuthorizedInternational').length;
await tickAccountRefresh(); await settle();
check('账号管理后无焦点事件也会刷新', models('AuthorizedInternational').length === beforeTimedRefresh + 1);
let timedRefreshCount = 1;
while (timedRefreshCount < 15 && await tickAccountRefresh()) { timedRefreshCount++; }
check('切换账号轮询有界，不永久探测', timedRefreshCount === 12 && scheduledRefreshes.size === 0);
authNotice(firstLogin.payload.operationId); await settle();
check('完成后的迟到授权通知不会恢复失效链接', !root.textContent.includes('重新打开登录页'));
byId('workbuddy-login').click(); await settle();
const secondLogin = pendingLogin;
authNotice(firstLogin.payload.operationId); await settle();
check('上轮通知不会污染新一轮登录', secondLogin.payload.operationId !== firstLogin.payload.operationId && !root.textContent.includes('重新打开登录页'));
authNotice(secondLogin.payload.operationId); await settle();
descendants(root).find(node => node.classList.contains('workbuddy-cancel')).click(); await settle();
check('取消只针对当前登录操作', requests.findLast(message => message.channel === 'workbuddy.cancel').payload.operationId === secondLogin.payload.operationId);
answer(secondLogin, { ok: false, detail: '操作已取消。' }); await settle();
check('取消保留原授权并清除重开入口', /已授权/.test(stateText()) && !root.textContent.includes('重新打开登录页'));
pauseLogin = false;
const beforeFocusRefresh = models('AuthorizedInternational').length;
windowEvents.get('focus')(); await settle();
check('从官方客户端返回面板时强制刷新账号和模型', models('AuthorizedInternational').length === beforeFocusRefresh + 1 && models('AuthorizedInternational').at(-1).payload.force === true);
windowEvents.get('focus')(); await settle();
check('短时间重复焦点事件不会重复启动探测', models('AuthorizedInternational').length === beforeFocusRefresh + 1);
fetchButton().click(); await settle();
savedAuthorization = { status: 'unavailable', models: [], detail: '保存后暂时无法确认连接' };
const unavailableSaveButton = descendants(root).find(node => node.tagName === 'BUTTON' && node.textContent === '保存设置');
unavailableSaveButton?.click(); await settle();
check('保存后暂时不可用仍保留该连接目录和选择', /待确认/.test(stateText()) && choices().includes('intl-1') && byId('model').value === 'intl-2');
fetchButton().click(); await settle();
savedAuthorization = { status: 'unauthorized', models: [], detail: '授权已失效' };
const authSaveButton = descendants(root).find(node => node.tagName === 'BUTTON' && node.textContent === '保存设置');
authSaveButton?.click(); await settle();
check('保存时明确未授权同样清除旧目录', /未授权/.test(stateText()) && byId('model').value === '' && byId('model-list').hidden);
savedAuthorization = null;
await change('mode', 'CustomApi');
const customSaveButton = descendants(root).find(node => node.tagName === 'BUTTON' && node.textContent === '保存设置');
customSaveButton?.click(); await settle();
const { getModelCatalog } = await import('../../src/web/scripts/model-catalog.js');
check('显式保存后模型目录可供对话选择器复用', getModelCatalog(seed)?.includes('api-1') && byId('customToken').value === '');

for (const mode of [...pausedModes]) { await release(mode); }
console.log(`\n=== 设置页接入切换：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
