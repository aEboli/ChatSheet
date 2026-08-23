// 与加载项通信的客户端。所有网络请求和密钥都留在加载项侧，
// 面板只通过这里请求能力，不直接访问外部资源。

const pending = new Map();
const listeners = new Map();
let sequence = 0;

function hostChannel() {
  return window.chrome && window.chrome.webview ? window.chrome.webview : null;
}

export function isHosted() {
  return hostChannel() !== null;
}

function handleMessage(event) {
  const message = event.data;
  if (!message || typeof message !== 'object') {
    return;
  }

  if (message.kind === 'response') {
    const entry = pending.get(message.id);
    if (!entry) {
      return;
    }
    pending.delete(message.id);
    if (entry.timer) { clearTimeout(entry.timer); }
    if (message.ok) {
      entry.resolve(message.data);
    } else {
      entry.reject(new Error(message.error || '加载项未返回原因'));
    }
    return;
  }

  const handlers = listeners.get(message.kind);
  if (handlers) {
    for (const handler of handlers) {
      try {
        handler(message);
      } catch (error) {
        console.error('处理宿主消息失败', error);
      }
    }
  }
}

const channel = hostChannel();
if (channel) {
  channel.addEventListener('message', handleMessage);
}

/** 订阅宿主主动推送的消息，例如功能区触发的路由切换。 */
export function on(kind, handler) {
  if (!listeners.has(kind)) {
    listeners.set(kind, new Set());
  }
  listeners.get(kind).add(handler);
  return () => listeners.get(kind)?.delete(handler);
}

/**
 * 把面板侧的状态写进加载项日志。
 * 面板运行在 WebView2 里，控制台输出无法留存，排查问题只能靠这个通道。
 */
export function logToHost(message, level = 'info') {
  if (!hostChannel()) {
    return Promise.resolve();
  }
  return request('client.log', { message, level }, { timeout: 5000 }).catch(() => {});
}

/**
 * 调用加载项能力。超时会明确报错，避免面板卡在无反馈状态。
 * timeout 传 0 表示不设超时——对话一轮可能长达数分钟，
 * 加上工具审批还要等用户操作，任何固定超时都会误杀。
 */
export function request(channelName, payload = {}, { timeout = 30000 } = {}) {
  const target = hostChannel();
  if (!target) {
    return Promise.reject(new Error('未运行在宿主内，无法调用加载项能力'));
  }

  sequence += 1;
  const id = `r${sequence}`;

  return new Promise((resolve, reject) => {
    const timer =
      timeout > 0
        ? setTimeout(() => {
            pending.delete(id);
            reject(new Error(`调用 ${channelName} 超时`));
          }, timeout)
        : null;

    pending.set(id, { resolve, reject, timer });

    try {
      target.postMessage({ id, channel: channelName, payload });
    } catch (error) {
      pending.delete(id);
      if (timer) { clearTimeout(timer); }
      reject(error);
    }
  });
}
