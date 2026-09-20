/**
 * 更新标题栏中的已保存渠道。文本由加载项生成，页面不接触连接凭据或账号原始响应。
 */
export function updateChannelHeader(settings = {}) {
  const node = document.getElementById('app-channel');
  if (!node) { return; }

  const label = String(settings.channelLabel ?? '').trim();
  node.textContent = label;
  node.title = label;
  node.setAttribute('aria-label', label ? `当前渠道：${label}` : '当前渠道');
  node.hidden = label.length === 0;
}
