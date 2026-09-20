// 模型目录只在当前面板会话内缓存，不写入设置文件。
//
// 设置页和对话选择器都需要同一份目录；若各自维护，就会出现
// “设置页刚获取，回到对话页还要再刷新一次”的重复请求。缓存键只包含
// 可公开的接入形态，不包含密钥。密钥变动由设置页显式使对应目录失效。

const catalogs = new Map();
const multipliers = new Map();
const revisions = new Map();

/**
 * 返回模型目录所属连接的稳定键。
 *
 * 自定义接口需要区分协议与地址；本机 CLI 需要区分来源。模型名、思考档位
 * 等不会改变 GET /models 的结果，因此不参与键。密钥绝不进入该键、日志或 UI。
 */
export function modelCatalogKey(settings = {}) {
  const mode = String(settings.mode ?? '');
  if (mode === 'Authorized' || mode === 'AuthorizedInternational') {
    // WorkBuddy 的 cliSource 只是设置页遗留字段，不代表账号或 ACP
    // 路径。把它放进键会让同一个账号无意义地分裂成多份目录；模式本身
    // 才是国内/国际隔离的边界。
    return JSON.stringify(['WorkBuddy', mode]);
  }

  if (mode === 'CustomApi') {
    return JSON.stringify([
      mode,
      String(settings.customProtocol ?? ''),
      String(settings.customBaseUrl ?? '').trim(),
    ]);
  }

  return JSON.stringify([mode, String(settings.cliSource ?? '')]);
}

/**
 * 从授权响应中选择当前连接真正可用的模型 ID。
 *
 * 已选模型仍在目录中时保持用户选择；否则优先采用 ACP 的当前模型，
 * 再退到目录第一项。空目录返回空串，避免把另一套连接的模型继续带下去。
 */
export function selectAuthorizedModel(currentModel, authorization = {}) {
  if (authorization?.status !== 'authorized') {
    // 未授权或组件不可用时无法证明模型属于当前账号空间；保留旧值会
    // 把上一套国内/国际连接的模型继续显示出来。
    return '';
  }

  const ids = [];
  const seen = new Set();
  for (const entry of authorization.models ?? []) {
    const id = typeof entry === 'string'
      ? entry.trim()
      : String(entry?.modelId ?? entry?.id ?? '').trim();
    if (id && !seen.has(id)) {
      seen.add(id);
      ids.push(id);
    }
  }

  const current = String(currentModel ?? '').trim();
  if (current && seen.has(current)) {
    return current;
  }

  const preferred = String(authorization.currentModelId ?? '').trim();
  return preferred && seen.has(preferred) ? preferred : (ids[0] ?? '');
}

function normalizeModels(models) {
  const unique = new Set();
  for (const model of models ?? []) {
    const id = typeof model === 'string'
      ? model.trim()
      : String(model?.modelId ?? model?.id ?? '').trim();
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

/** 按连接键读取已校验的倍率，缺失时不补默认值。 */
export function getModelMultiplier(catalogKey, modelId) {
  return multipliers.get(catalogKey)?.get(modelId) ?? '';
}

function normalizeMultipliers(models) {
  const result = new Map();
  for (const model of models ?? []) {
    const id = String(model?.modelId ?? model?.id ?? '').trim();
    const multiplier = String(model?.multiplier ?? '').trim();
    if (id && /^\d+(?:\.\d+)?x$/.test(multiplier)) {
      result.set(id, multiplier);
    }
  }
  return result;
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
  multipliers.set(key, normalizeMultipliers(models));
  return true;
}

/**
 * 使一个连接的目录失效，并推进修订号以屏蔽已在路上的旧请求。
 */
export function invalidateModelCatalog(settings) {
  const key = modelCatalogKey(settings);
  catalogs.delete(key);
  multipliers.delete(key);
  revisions.set(key, (revisions.get(key) ?? 0) + 1);
}

/**
 * 把 settings.get 返回的授权目录同步到共享缓存。
 *
 * settings.get 已经完成了一次 ACP 查询；不把它写入缓存会让对话页再次打开
 * 时重复查询同一份目录。目录变化时先推进修订号，屏蔽可能仍在路上的旧
 * models.list 响应。
 */
export function rememberAuthorizedModelCatalog(settings) {
  if (!['Authorized', 'AuthorizedInternational'].includes(settings?.mode) ||
    !['authorized', 'unauthorized'].includes(settings.authorization?.status)) {
    return false;
  }

  const connection = {
    mode: settings.mode,
    cliSource: settings.cliSource,
  };
  const models = settings.authorization.models ?? [];
  const next = normalizeModels(models);
  const nextMultipliers = normalizeMultipliers(models);
  const existing = getModelCatalog(connection);
  if (existing !== null && existing.length === next.length &&
    existing.every((model, index) => model === next[index] &&
      getModelMultiplier(modelCatalogKey(connection), model) === (nextMultipliers.get(model) ?? ''))) {
    return true;
  }

  invalidateModelCatalog(connection);
  return putModelCatalog(connection, models);
}
