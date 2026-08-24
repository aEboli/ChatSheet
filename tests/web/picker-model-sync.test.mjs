// 选择器与后端设置的模型同步回归测试。
//
// 缺陷现场：从「自定义接口」切回「本机 CLI 配置」后，选择器仍把自定义接口
// 那个模型钉在列表首位并显示为当前模型——即使 CLI 那边根本没有它。要等用户
// 点了别的模型再点「刷新」才会消失。
//
// 加载项已保证 settings.model 不会跨连接残留，因此选择器必须无条件以后端
// 设置为准；这里用 DOM 存根验证这条不变量。渲染函数取不到元素会提前返回，
// 所以 getElementById 一律返回 null 也能跑通状态逻辑。

globalThis.window = { chrome: null, innerWidth: 420 };
globalThis.document = {
  getElementById: () => null,
  querySelector: () => null,
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: () => ({
    className: '',
    append: () => {},
    addEventListener: () => {},
    classList: { add: () => {}, toggle: () => {} },
  }),
};

const { syncPicker, describePicker } = await import('../../src/web/scripts/picker.js');
const { putModelCatalog } = await import('../../src/web/scripts/model-catalog.js');

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

const customApi = {
  mode: 'CustomApi',
  customProtocol: 'openai-chat-completions',
  customBaseUrl: 'https://api.example.test/v1',
};
const localCli = { mode: 'LocalCli', cliSource: 'Claude' };

putModelCatalog(customApi, ['custom-only-model', 'custom-only-mini']);
putModelCatalog(localCli, ['cli-model-a', 'cli-model-b']);

console.log('检查选择器与后端设置的模型同步：');

syncPicker({ ...customApi, model: 'custom-only-model', thinking: 'High' });
check(
  '自定义接口下显示所选模型',
  describePicker().includes('模型=custom-only-model') && describePicker().includes('模型项=2'),
  describePicker(),
);

// 加载项丢弃了跨连接的模型：model 与 effectiveModel 都为空。
syncPicker({ ...localCli, model: '', effectiveModel: '', thinking: 'High' });
const afterSwitch = describePicker();
check(
  '切回本机 CLI 后不再显示自定义接口的模型',
  afterSwitch.includes('模型=未选') && !afterSwitch.includes('custom-only-model'),
  afterSwitch,
);
check(
  '切回后只列出 CLI 的模型（旧模型没被钉进列表）',
  afterSwitch.includes('模型项=2'),
  afterSwitch,
);

// CLI 配置自带模型时，后端用 effectiveModel 下发，选择器应直接采用。
syncPicker({ ...localCli, model: '', effectiveModel: 'cli-model-a', thinking: 'High' });
check(
  'CLI 配置自带的模型会被采用',
  describePicker().includes('模型=cli-model-a'),
  describePicker(),
);

// 网关不提供 /models 时手填的模型仍要能显示并保持选中。
const emptyCatalogApi = {
  mode: 'CustomApi',
  customProtocol: 'openai-chat-completions',
  customBaseUrl: 'https://no-list.example.test/v1',
};
putModelCatalog(emptyCatalogApi, []);
syncPicker({ ...emptyCatalogApi, model: 'hand-typed-model', thinking: 'High' });
check(
  '目录为空时手填模型仍保留',
  describePicker().includes('模型=hand-typed-model') && describePicker().includes('模型项=1'),
  describePicker(),
);

console.log('');
console.log(`=== 选择器模型同步：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
