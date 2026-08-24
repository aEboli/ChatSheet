// 模型目录只在当前面板会话内缓存，不写入设置文件。
//
// 设置页和对话选择器都需要同一份目录；若各自维护，就会出现
// “设置页刚获取，回到对话页还要再刷新一次”的重复请求。缓存键只包含
// 可公开的接入形态，不包含密钥。密钥变动由设置页显式使对应目录失效。

const catalogs = new Map();
const revisions = new Map();

/**
 * 返回模型目录所属连接的稳定键。
 *
 * 自定义接口需要区分协议与地址；本机 CLI 需要区分来源。模型名、思考档位
 * 等不会改变 GET /models 的结果，因此不参与键。密钥绝不进入该键、日志或 UI。
 */
export function modelCatalogKey(settings = {}) {
  const mode = String(settings.mode ?? '');
  if (mode === 'CustomApi') {
    return JSON.stringify([
      mode,
      String(settings.customProtocol ?? ''),
      String(settings.customBaseUrl ?? '').trim(),
    ]);
  }

  return JSON.stringify([mode, String(settings.cliSource ?? '')]);
}

function normalizeModels(models) {
  const unique = new Set();
  for (const model of models ?? []) {
    if (typeof model !== 'string') { continue; }
    const id = model.trim();
    if (id) { unique.add(id); }
  }
  return [...unique];
}

/**
 * 返回已获取的目录副本；null 表示当前连接从未获取过，[] 表示确实获取到空列表。
 */
export function getModelCatalog(settings) {
  const models = catalogs.get(modelCatalogKey(settings));
  return models === undefined ? null : [...models];
}

/** 当前目录修订号。失效后旧的异步响应不能重新写回缓存。 */
export function modelCatalogRevision(settings) {
  return revisions.get(modelCatalogKey(settings)) ?? 0;
}

/**
 * 写入一次模型获取结果。
 *
 * expectedRevision 来自请求开始前。若设置页在等待响应期间换了密钥并使
 * 目录失效，旧请求的结果会被丢弃，避免覆盖新 API/新密钥的目录。
 */
export function putModelCatalog(settings, models, expectedRevision = modelCatalogRevision(settings)) {
  const key = modelCatalogKey(settings);
  if (expectedRevision !== (revisions.get(key) ?? 0)) {
    return false;
  }

  catalogs.set(key, normalizeModels(models));
  return true;
}

/**
 * 使一个连接的目录失效，并推进修订号以屏蔽已在路上的旧请求。
 */
export function invalidateModelCatalog(settings) {
  const key = modelCatalogKey(settings);
  catalogs.delete(key);
  revisions.set(key, (revisions.get(key) ?? 0) + 1);
}
