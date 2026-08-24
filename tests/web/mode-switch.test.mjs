// 接入模式切换的无宿主回归测试。
//
// settings.js 的模式状态变更是纯对象操作；这里给 bridge 提供最小的
// window stub，直接验证切换时必须丢弃旧模式的模型，而重复选择同一模式
// 不应误删用户已经选好的模型。

globalThis.window = { chrome: null };

const { resetModelOnModeChange } = await import('../../src/web/scripts/settings.js');

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
};
check(
  'CustomApi 切到 LocalCli 会清空模型',
  resetModelOnModeChange(switched, 'LocalCli') &&
    switched.mode === 'LocalCli' &&
    switched.model === '' &&
    switched.effectiveModel === '' &&
    switched.clearModelOnModeChange === true,
  JSON.stringify(switched),
);

const unchanged = {
  mode: 'LocalCli',
  model: 'cli-selected-model',
  effectiveModel: 'cli-selected-model',
};
check(
  '重复选择当前模式不会清空模型',
  !resetModelOnModeChange(unchanged, 'LocalCli') &&
    unchanged.model === 'cli-selected-model' &&
    unchanged.effectiveModel === 'cli-selected-model' &&
    unchanged.clearModelOnModeChange === false,
  JSON.stringify(unchanged),
);

unchanged.clearModelOnModeChange = true;
unchanged.model = 'cli-selected-model';
check(
  '主动选择新模型会取消清空标记',
  (() => {
    unchanged.clearModelOnModeChange = false;
    return unchanged.model === 'cli-selected-model' && !unchanged.clearModelOnModeChange;
  })(),
  JSON.stringify(unchanged),
);

console.log('');
console.log(`=== 接入模式切换：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
