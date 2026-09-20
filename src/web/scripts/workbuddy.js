import { request, isHosted } from './bridge.js';

let state = null;
let pending = null;
let revision = 0;
let lastDay = '';
let initialized = false;
let retryAt = 0;
let retryMode;
const RETRY_DELAY_MS = 5 * 60 * 1000;
const subscribers = new Set();

export function beijingDate(now = new Date()) {
  return new Date(now.getTime() + 8 * 60 * 60 * 1000).toISOString().slice(0, 10);
}

export function checkinView(value, now = new Date()) {
  if (!value) { return { visible: false, text: '', detail: '' }; }
  const today = value.date === beijingDate(now);
  const checked = today && value.status === 'checked_in';
  const checking = !today || value.status === 'checking';
  return {
    visible: true,
    checked,
    text: checked ? '今日已签到' : checking ? '正在检查今日签到…'
      : value.status === 'unauthorized' ? '登录后自动签到'
      : value.status === 'inactive' ? '暂无签到活动'
      : value.status === 'unsupported' ? '此账号暂不支持签到'
      : value.status === 'not_checked_in' ? '今日签到未完成' : '签到状态待确认',
    detail: checking ? '正在刷新今日签到状态。' : value.detail || '每天按北京时间自动检查一次。',
  };
}

export function getWorkBuddyCheckin() { return state; }

export function subscribeWorkBuddyCheckin(callback) {
  subscribers.add(callback);
  return () => subscribers.delete(callback);
}

export function setWorkBuddyCheckin(value) {
  state = value;
  retryAt = value && ['unknown', 'not_checked_in', 'unauthorized'].includes(value.status)
    ? Date.now() + RETRY_DELAY_MS : 0;
  const view = checkinView(state);
  const badge = document.getElementById('workbuddy-checkin');
  if (badge) {
    badge.hidden = !view.visible;
    badge.textContent = view.text;
    badge.title = view.detail;
    badge.classList.toggle('is-ok', Boolean(view.checked));
  }
  for (const callback of subscribers) { callback(state); }
}

export function clearWorkBuddyCheckin() {
  revision += 1;
  pending = null;
  retryMode = undefined;
  setWorkBuddyCheckin(null);
}

export function refreshWorkBuddyCheckin({ active, force = false, mode } = {}) {
  if (pending) { return pending; }
  const requestRevision = revision;
  retryMode = mode;
  lastDay = beijingDate();
  if (active || state) { setWorkBuddyCheckin({ status: 'checking', date: lastDay }); }
  const payload = { active, force };
  if (mode) { payload.mode = mode; }
  const task = request('workbuddy.account', payload, { timeout: 100000 })
    .then(value => {
      if (requestRevision === revision) { setWorkBuddyCheckin(value); }
      return value;
    })
    .catch(() => {
      if (requestRevision === revision) {
        if (active || state) {
          setWorkBuddyCheckin({ status: 'unknown', date: lastDay, detail: '签到状态暂时无法确认，请稍后刷新。' });
        }
        retryAt = Date.now() + RETRY_DELAY_MS;
      }
      return null;
    })
    .finally(() => { if (pending === task) { pending = null; } });
  pending = task;
  return task;
}

export function initWorkBuddyCheckin() {
  if (initialized || !isHosted()) { return; }
  initialized = true;
  void refreshWorkBuddyCheckin();
  const refreshDue = () => {
    if (beijingDate() !== lastDay || (retryAt > 0 && Date.now() >= retryAt)) {
      void refreshWorkBuddyCheckin({ mode: retryMode });
    }
  };
  // 未确认时每五分钟重查；已确认的结果只在跨日刷新，恢复可见时也检查。
  window.setInterval(refreshDue, 60000);
  document.addEventListener('visibilitychange', () => { if (!document.hidden) { refreshDue(); } });
}
