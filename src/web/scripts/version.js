/** 将程序集版本规范化为三段或四段阿拉伯数字，保留可重复部署构建号。 */
export function formatVersion(version) {
  const match = String(version ?? '').trim().match(/^(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?$/);
  if (!match) {
    return '';
  }

  return match.slice(1).filter((part) => part !== undefined).join('.');
}

/** 先显示静态回退版本；宿主返回程序集版本后再替换为实际版本。 */
export function updateVersionDisplay(version, documentRoot = globalThis.document) {
  const node = documentRoot?.getElementById?.('app-version');
  if (!node) {
    return false;
  }

  const formatted = formatVersion(version || node.dataset?.version);
  if (!formatted) {
    return false;
  }

  node.textContent = formatted;
  return true;
}
