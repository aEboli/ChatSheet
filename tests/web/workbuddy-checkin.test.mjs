// 通过真实消息桥和可控时钟验证自动签到调度，不访问账号或网络。
// 运行：node tests/web/workbuddy-checkin.test.mjs

const NativeDate = globalThis.Date;
let now = NativeDate.parse('2026-09-19T16:30:00Z');
globalThis.Date = class extends NativeDate {
  constructor(...args) { super(...(args.length ? args : [now])); }
  static now() { return now; }
};

const requests = [];
const timers = [];
const events = new Map();
let receive;
let respond;
let hold = false;
const badge = { hidden: true, classList: { toggle() {} } };
globalThis.window = {
  chrome: {
    webview: {
      addEventListener: (_, handler) => { receive = handler; },
      postMessage(message) {
        requests.push(message);
        if (!hold) { queueMicrotask(() => reply(message)); }
      },
    },
  },
  setInterval: (callback, delay) => { timers.push({ callback, delay }); },
};
globalThis.document = {
  hidden: false,
  getElementById: () => badge,
  addEventListener: (name, handler) => events.set(name, handler),
};

function reply(message) {
  receive({ data: { kind: 'response', id: message.id, ...respond() } });
}
const settle = () => new Promise(resolve => setImmediate(resolve));
async function advance(milliseconds) {
  now += milliseconds;
  for (const timer of timers) { timer.callback(); }
  await settle();
}

const {
  beijingDate, checkinView, clearWorkBuddyCheckin, getWorkBuddyCheckin,
  initWorkBuddyCheckin, refreshWorkBuddyCheckin, setWorkBuddyCheckin,
} = await import('../../src/web/scripts/workbuddy.js');
const status = value => ({ ok: true, data: { status: value, date: beijingDate() } });
let passed = 0;
let failed = 0;
function check(label, condition) {
  console.log(`  ${condition ? '通过' : '失败'}  ${label}`);
  if (condition) { passed += 1; } else { failed += 1; }
}

respond = () => status('unknown');
initWorkBuddyCheckin();
await settle();
check('面板启动自动检查，未知状态不显示成功',
  requests.length === 1 && getWorkBuddyCheckin()?.status === 'unknown' && !checkinView(getWorkBuddyCheckin()).checked);
await advance(5 * 60 * 1000 - 1);
check('失败后五分钟内不重复自动请求', requests.length === 1);
respond = () => status('checked_in');
await advance(1);
check('首次失败后同日自动恢复，无需手动刷新',
  requests.length === 2 && getWorkBuddyCheckin()?.status === 'checked_in');

setWorkBuddyCheckin({ status: 'checked_in', date: beijingDate() });
let before = requests.length;
await advance(60 * 60 * 1000);
check('当天已签到后停止自动重查', requests.length === before);

respond = () => status('not_checked_in');
await refreshWorkBuddyCheckin({ active: true, mode: 'Authorized' });
before = requests.length;
respond = () => status('checked_in');
await advance(5 * 60 * 1000);
check('领取未确认时自动回查，并保持国内模式',
  requests.length === before + 1 && requests.at(-1).payload.mode === 'Authorized' &&
    getWorkBuddyCheckin()?.status === 'checked_in');

clearWorkBuddyCheckin();
respond = () => ({ ok: false, error: '模拟消息桥暂时失败' });
await refreshWorkBuddyCheckin();
before = requests.length;
respond = () => status('checked_in');
await advance(5 * 60 * 1000);
check('启动时消息桥失败且尚无状态，仍会自动恢复',
  requests.length === before + 1 && getWorkBuddyCheckin()?.status === 'checked_in');

respond = () => status('unauthorized');
await refreshWorkBuddyCheckin({ active: true, mode: 'Authorized' });
before = requests.length;
respond = () => status('checked_in');
await advance(5 * 60 * 1000);
check('官方账号恢复可用后自动重新检查',
  requests.length === before + 1 && getWorkBuddyCheckin()?.status === 'checked_in');

respond = () => status('unknown');
await refreshWorkBuddyCheckin({ active: true, mode: 'Authorized' });
before = requests.length;
hold = true;
await advance(5 * 60 * 1000);
await advance(60 * 1000);
check('自动重查仍在等待时不叠加请求', requests.length === before + 1);
clearWorkBuddyCheckin();
respond = () => status('unknown');
if (requests.length > before) { reply(requests.at(-1)); }
hold = false;
await settle();
before = requests.length;
await advance(5 * 60 * 1000);
check('切出国内模式后，迟到响应不会恢复状态或重试',
  getWorkBuddyCheckin() === null && requests.length === before && badge.hidden);

for (const value of ['inactive', 'unsupported', null]) {
  respond = () => value ? status(value) : { ok: true, data: null };
  await refreshWorkBuddyCheckin();
  before = requests.length;
  await advance(5 * 60 * 1000);
  check(`${value ?? '非国内模式'}不会触发同日重试`, requests.length === before);
}

setWorkBuddyCheckin({ status: 'checked_in', date: beijingDate() });
now = NativeDate.parse('2026-09-20T16:00:00Z');
check('北京时间跨日不会把昨天显示为今日已签到', !checkinView(getWorkBuddyCheckin()).checked);
before = requests.length;
respond = () => status('checked_in');
events.get('visibilitychange')();
await settle();
check('休眠跨日后恢复可见会检查新一天',
  requests.length === before + 1 && getWorkBuddyCheckin()?.date === '2026-09-21');

globalThis.Date = NativeDate;
console.log(`\n=== WorkBuddy 自动签到：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
