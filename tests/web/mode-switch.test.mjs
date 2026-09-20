// 接入模式切换的无宿主回归测试。
//
// settings.js 的模式状态变更是纯对象操作；这里给 bridge 提供最小的
// window stub，直接验证切换时必须丢弃旧模式的模型，而重复选择同一模式
// 不应误删用户已经选好的模型。
//
// modelChosenForConnection 是给加载项的正向确认：只有用户在当前这套接入
// 配置下选过模型才为真。缺少确认且连接变了，加载项会把模型当作残留丢弃。

globalThis.window = { chrome: null };

const { resetModelOnModeChange, adoptSettings } = await import('../../src/web/scripts/settings.js');

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

console.log('检查接入模式切换的模型状态：');

const switched = {
  mode: 'CustomApi',
  model: 'custom-only-model',
  effectiveModel: 'custom-only-model',
  modelChosenForConnection: true,
  ready: true,
  readyDetail: 'Claude CLI · http://localhost:8080/v1 · opus[1m]',
  authorization: { status: 'authorized' },
  workbuddyRuntime: { available: true, canLogin: true, standalone: true },
};
check(
  'CustomApi 切到 LocalCli 会清空模型',
  resetModelOnModeChange(switched, 'LocalCli') &&
    switched.mode === 'LocalCli' &&
    switched.model === '' &&
    switched.effectiveModel === '' &&
    switched.modelChosenForConnection === false &&
    switched.ready === false && switched.readyDetail === '' &&
    switched.authorization === null && switched.workbuddyRuntime === null,
  JSON.stringify(switched),
);

const unchanged = {
  mode: 'LocalCli',
  model: 'cli-selected-model',
  effectiveModel: 'cli-selected-model',
  modelChosenForConnection: true,
};
check(
  '重复选择当前模式不会清空模型',
  !resetModelOnModeChange(unchanged, 'LocalCli') &&
    unchanged.model === 'cli-selected-model' &&
    unchanged.effectiveModel === 'cli-selected-model' &&
    unchanged.modelChosenForConnection === true,
  JSON.stringify(unchanged),
);

// 切换后再选模型，确认标记必须重新置真，否则加载项会把它当残留丢掉。
const reselected = { mode: 'CustomApi', model: 'old-model', effectiveModel: 'old-model' };
resetModelOnModeChange(reselected, 'LocalCli');
reselected.model = 'cli-model';
reselected.modelChosenForConnection = true;
check(
  '切换后主动选新模型会带上确认标记',
  reselected.model === 'cli-model' && reselected.modelChosenForConnection === true,
  JSON.stringify(reselected),
);

const authorized = {
  mode: 'LocalCli',
  model: 'old-model',
  effectiveModel: 'old-model',
  ready: true,
  readyDetail: 'Claude CLI · http://localhost:8080/v1 · opus[1m]',
  workbuddyRuntime: { available: true, canLogin: true, standalone: true },
};
check(
  '切到 WorkBuddy 不保留旧 CLI 就绪文案',
  resetModelOnModeChange(authorized, 'Authorized') &&
    authorized.ready === false && authorized.readyDetail === '' &&
    authorized.authorization === null && authorized.workbuddyRuntime === null,
  JSON.stringify(authorized),
);

// 加载项回传的模型必然属于它同时回传的接入配置，直接算作已确认；
// 否则用户只改接口地址、没重新选模型时，保存会把模型一并丢掉。
const loaded = adoptSettings({ mode: 'CustomApi', model: 'listed-model' });
check(
  '加载项回传的模型算作为当前连接所选',
  loaded.modelChosenForConnection === true,
  JSON.stringify(loaded),
);

const loadedWithoutModel = adoptSettings({ mode: 'LocalCli', model: '', effectiveModel: 'from-cli-config' });
check(
  '回传模型为空时不算已确认',
  loadedWithoutModel.modelChosenForConnection === false,
  JSON.stringify(loadedWithoutModel),
);

console.log('');
console.log(`=== 接入模式切换：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
