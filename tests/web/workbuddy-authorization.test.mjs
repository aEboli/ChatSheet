// WorkBuddy 授权目录的前端归一化回归测试。
//
// ACP 返回对象目录时，modelId 是真实选择值，name 只是显示文本；普通接口返回的
// 字符串目录仍须保持原有语义。这个测试不读取任何 WorkBuddy 凭据或外部配置。

globalThis.window = { chrome: null };

function makeNode(tag = 'div') {
  const node = {
    tag,
    id: '',
    className: '',
    textContent: '',
    value: '',
    selected: false,
    hidden: false,
    title: '',
    children: [],
    append: (...kids) => node.children.push(...kids),
    replaceChildren: (...kids) => { node.children = [...kids]; },
    addEventListener: () => {},
  };
  return node;
}

globalThis.document = {
  createElement: (tag) => makeNode(tag),
  getElementById: () => null,
};

const {
  adoptSettings,
  authorizationView,
  normalizeModelOptions,
  omitReadOnlySettingsFields,
  populateModelList,
  reconcileAuthorizedModel,
  resetModelOnModeChange,
  isWorkBuddyMode,
  workBuddyLoginAvailable,
} =
  await import('../../src/web/scripts/settings.js');
const { getModelCatalog } = await import('../../src/web/scripts/model-catalog.js');

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

console.log('检查 WorkBuddy 授权模型目录：');

check('国内版和国际版都识别为 WorkBuddy 模式',
  isWorkBuddyMode('Authorized') && isWorkBuddyMode('AuthorizedInternational') &&
    !isWorkBuddyMode('CustomApi'));
check('ACP 探测失败时登录入口仍保持可点击',
  workBuddyLoginAvailable({ available: true, standalone: false }) &&
    workBuddyLoginAvailable({ available: false, canLogin: false, standalone: false }) &&
    !workBuddyLoginAvailable({ loginDisabled: true }));

const options = normalizeModelOptions([
  { modelId: 'auto', name: 'Auto', multiplier: '0.29x', supportsImages: true },
  { modelId: 'auto', name: 'duplicate' },
  { id: 'model-b', name: 'Model B', multiplier: '0.05x' },
  { id: 'model-d', name: 'Model B', multiplier: '0.06x' },
  'model-c',
  { modelId: '  ', name: 'ignored' },
  null,
]);

check(
  '对象目录保留 ID 并使用名称显示',
  JSON.stringify(options) === JSON.stringify([
    { id: 'auto', label: 'Auto 0.29x' },
    { id: 'model-b', label: 'Model B 0.05x · model-b' },
    { id: 'model-d', label: 'Model B 0.06x · model-d' },
    { id: 'model-c', label: 'model-c' },
  ]),
  JSON.stringify(options),
);
check(
  '显示名称不会替代保存值',
  options[0]?.id === 'auto' && options[0]?.label === 'Auto 0.29x',
  JSON.stringify(options[0]),
);
check(
  '字符串目录保持兼容',
  JSON.stringify(normalizeModelOptions([' model-a ', 'model-a', 'model-b'])) === JSON.stringify([
    { id: 'model-a', label: 'model-a' },
    { id: 'model-b', label: 'model-b' },
  ]),
);

const authorizedSettings = {
  mode: 'Authorized',
  cliSource: 'Auto',
  model: 'model-a',
  authorization: {
    status: 'authorized',
    detail: 'WorkBuddy 已授权，已读取 2 个可用模型。',
    models: [
      { modelId: 'model-a', name: 'Model A' },
      { modelId: 'model-b', name: 'Model B' },
    ],
  },
};
adoptSettings(authorizedSettings);
check(
  'settings.get 的授权目录进入共享缓存',
  JSON.stringify(getModelCatalog(authorizedSettings)) === JSON.stringify(['model-a', 'model-b']),
  JSON.stringify(getModelCatalog(authorizedSettings)),
);

const unauthorizedSettings = {
  mode: 'Authorized',
  cliSource: 'Auto',
  model: 'stale-domestic-model',
  authorization: {
    status: 'unauthorized',
    detail: 'WorkBuddy 当前未授权，请先在 WorkBuddy 中完成登录。',
    models: [],
  },
};
adoptSettings(unauthorizedSettings);
check(
  '未授权的空目录会清掉旧缓存',
  Array.isArray(getModelCatalog(unauthorizedSettings)) && getModelCatalog(unauthorizedSettings).length === 0 &&
    unauthorizedSettings.model === '' && unauthorizedSettings.modelChosenForConnection === false,
  JSON.stringify({ catalog: getModelCatalog(unauthorizedSettings), model: unauthorizedSettings.model }),
);

const internationalSettings = {
  mode: 'AuthorizedInternational',
  cliSource: 'Auto',
  model: 'domestic-model',
  authorization: {
    status: 'authorized',
    detail: 'International WorkBuddy authorized.',
    models: [{ modelId: 'intl-model', name: 'International Model' }],
  },
};
adoptSettings(authorizedSettings);
adoptSettings(internationalSettings);
check(
  '国际版授权目录写入独立缓存键',
  JSON.stringify(getModelCatalog(internationalSettings)) === JSON.stringify(['intl-model']) &&
    JSON.stringify(getModelCatalog(authorizedSettings)) === JSON.stringify(['model-a', 'model-b']),
  JSON.stringify(getModelCatalog(internationalSettings)),
);
check(
  '国际版目录返回后不会保留国内模型',
  internationalSettings.model === 'intl-model' &&
    internationalSettings.modelChosenForConnection === true,
  JSON.stringify(internationalSettings),
);

const preferredInternational = {
  mode: 'AuthorizedInternational',
  model: 'domestic-model',
  authorization: {
    status: 'authorized',
    currentModelId: 'intl-current',
    models: [{ modelId: 'intl-first' }, { modelId: 'intl-current' }],
  },
};
check(
  '国际版失效模型会切到 ACP 当前模型',
  reconcileAuthorizedModel(preferredInternational) && preferredInternational.model === 'intl-current',
  JSON.stringify(preferredInternational),
);

const modelList = makeNode('select');
const modelInput = makeNode('input');
modelInput.value = 'model-b';
populateModelList([
  { modelId: 'model-a', name: 'Model A', multiplier: '0.03x' },
  { modelId: 'model-b', name: 'Model B', multiplier: '0.52x' },
], modelList, modelInput);
check(
  '对象目录会填充下拉框并保留 ID 作为 value',
  modelList.children.map((option) => option.value).join('|') === '|model-a|model-b' &&
    modelList.children[2].textContent === 'Model B 0.52x' &&
    modelList.children[2].selected === true,
  JSON.stringify(modelList.children),
);

const authorizedView = authorizationView(authorizedSettings.authorization);
check(
  'Authorized 模式渲染已授权状态区域',
  authorizedView.status === 'authorized' && authorizedView.statusClass === 'notice notice-ok' &&
    authorizedView.count === 2 && authorizedView.detail.includes('已读取 2 个'),
  JSON.stringify(authorizedView),
);

const unauthorizedView = authorizationView({
  status: 'unauthorized',
  detail: 'WorkBuddy 当前未授权，请先在 WorkBuddy 中完成登录。',
  models: [],
});
check(
  '未授权状态显示登录提示',
  unauthorizedView.statusClass === 'notice notice-warn' &&
    unauthorizedView.hint.includes('浏览器登录'),
  JSON.stringify(unauthorizedView),
);

const unavailableView = authorizationView({ status: 'unavailable', models: [] });
check(
  '国内不可用状态提示启动或安装国内版',
  unavailableView.statusClass === 'notice notice-error' &&
    unavailableView.hint.includes('国内版 WorkBuddy'),
  JSON.stringify(unavailableView),
);

const internationalUnavailableView = authorizationView(
  { status: 'unavailable', models: [] },
  true,
);
check(
  '国际版不可用状态提示安装国际版组件',
  internationalUnavailableView.statusClass === 'notice notice-error' &&
    internationalUnavailableView.hint.includes('国际版独立授权组件'),
  JSON.stringify(internationalUnavailableView),
);

const savePayload = omitReadOnlySettingsFields({
  mode: 'Authorized',
  model: 'model-a',
  authorization: authorizedSettings.authorization,
  ready: false,
  readyDetail: 'WorkBuddy 授权',
  customToken: 'must-stay-when-present',
});
check(
  '保存 payload 不包含授权状态对象',
  !Object.prototype.hasOwnProperty.call(savePayload, 'authorization') &&
    savePayload.model === 'model-a' && savePayload.customToken === 'must-stay-when-present',
  JSON.stringify(savePayload),
);

const authorized = {
  mode: 'Authorized',
  model: 'old-model',
  effectiveModel: 'old-model',
  modelChosenForConnection: true,
  authorization: { status: 'authorized', models: [{ modelId: 'old-model' }] },
};
check(
  '切换接入模式会清除旧模型',
  resetModelOnModeChange(authorized, 'LocalCli') && authorized.model === '' &&
    authorized.effectiveModel === '' && !authorized.modelChosenForConnection &&
    authorized.authorization === null,
  JSON.stringify(authorized),
);

const internationalSwitch = {
  mode: 'AuthorizedInternational',
  model: 'intl-model',
  effectiveModel: 'intl-model',
  modelChosenForConnection: true,
};
check(
  '国际版切换到国内版会清除模型归属',
  resetModelOnModeChange(internationalSwitch, 'Authorized') && internationalSwitch.model === '' &&
    !internationalSwitch.modelChosenForConnection,
  JSON.stringify(internationalSwitch),
);

console.log('');
console.log(`=== WorkBuddy 授权目录：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
