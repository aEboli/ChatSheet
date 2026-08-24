// 模型目录缓存的无宿主单元测试。
//
// 运行：node tests/web/model-catalog.test.mjs

import {
  getModelCatalog,
  invalidateModelCatalog,
  modelCatalogKey,
  modelCatalogRevision,
  putModelCatalog,
} from '../../src/web/scripts/model-catalog.js';

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

const apiA = {
  mode: 'CustomApi',
  customProtocol: 'openai-chat-completions',
  customBaseUrl: ' https://api-a.example.test/v1/ ',
};
const apiB = {
  mode: 'CustomApi',
  customProtocol: 'openai-responses',
  customBaseUrl: 'https://api-b.example.test/v1',
};
const cliClaude = { mode: 'LocalCli', cliSource: 'Claude' };
const cliCodex = { mode: 'LocalCli', cliSource: 'Codex' };

console.log('检查模型目录缓存：');

check('自定义接口按协议和地址隔离', modelCatalogKey(apiA) !== modelCatalogKey(apiB));
check('CLI 按来源隔离', modelCatalogKey(cliClaude) !== modelCatalogKey(cliCodex));
check('未获取的目录返回 null', getModelCatalog(apiA) === null);

const revision = modelCatalogRevision(apiA);
check('写入目录成功', putModelCatalog(apiA, ['model-a', ' model-a ', '', null, 'model-a-mini'], revision));
check(
  '目录去空白和重复',
  JSON.stringify(getModelCatalog(apiA)) === JSON.stringify(['model-a', 'model-a-mini']),
  JSON.stringify(getModelCatalog(apiA)),
);

const copy = getModelCatalog(apiA);
copy.push('mutated-outside');
check('读取目录不会暴露缓存本体', !getModelCatalog(apiA).includes('mutated-outside'));

check('空结果也是已获取目录', putModelCatalog(apiB, [], modelCatalogRevision(apiB)));
check('空结果不会和未获取混淆', Array.isArray(getModelCatalog(apiB)) && getModelCatalog(apiB).length === 0);

invalidateModelCatalog(apiA);
check('失效后目录清空', getModelCatalog(apiA) === null);
check('失效会推进修订号', modelCatalogRevision(apiA) === revision + 1);
check('旧请求结果不能回写', !putModelCatalog(apiA, ['stale-model'], revision));
check('其他 API 目录不受影响', Array.isArray(getModelCatalog(apiB)) && getModelCatalog(apiB).length === 0);

// 模拟设置页先获取、对话页随后打开的共享语义。这里的计数代表真实 GET /models；
// 普通打开只读取目录，只有明确 force 才会再触发一次获取。
const sharedApi = {
  mode: 'CustomApi',
  customProtocol: 'openai-chat-completions',
  customBaseUrl: 'https://shared.example.test/v1',
};
let remoteRequests = 0;
function loadForTest(force = false) {
  const cached = getModelCatalog(sharedApi);
  if (!force && cached !== null) {
    return cached;
  }

  remoteRequests += 1;
  putModelCatalog(sharedApi, [`shared-model-${remoteRequests}`], modelCatalogRevision(sharedApi));
  return getModelCatalog(sharedApi);
}

check('设置页首次获取会请求服务端', loadForTest()[0] === 'shared-model-1' && remoteRequests === 1);
check('对话页普通打开复用设置页目录', loadForTest()[0] === 'shared-model-1' && remoteRequests === 1);
check('对话页显式刷新才重新请求', loadForTest(true)[0] === 'shared-model-2' && remoteRequests === 2);

console.log('');
console.log(`=== 模型目录缓存：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
