import { request, on, isHosted, logToHost } from './bridge.js';
import { prefersReducedMotion, initRefusalShake } from './motion.js';
import { initChat, refreshReady } from './chat.js';
import { initSettings } from './settings.js';
import { describePicker } from './picker.js';
import { describeAttachments } from './attachments.js';

const ROUTES = ['chat', 'settings', 'diagnostics'];
let settingsLoaded = false;

let activeRoute = null;

/**
 * 切换视图。
 *
 * force 为真时即使已在目标页也重新执行页面的加载逻辑，
 * 用于用户主动点击导航按钮（例如想手动刷新就绪状态）。
 * 自动触发的路由变化不传 force，以免重复执行：
 * 本函数会写 location.hash，那会触发 hashchange 再回调进来一次。
 */
function setRoute(route, { force = false } = {}) {
  const target = ROUTES.includes(route) ? route : 'chat';

  if (activeRoute === target && !force) {
    return;
  }
  activeRoute = target;

  // 只认带 data-route 的按钮。栏上还有主题切换，它同样是 .nav-btn 但不是页签，
  // 不带 data-route——按 .nav-btn 全选会把它当成一个页签，于是每次切页都给它
  // 取消一次「当前」态，而它本来就不该有这个态。
  for (const button of document.querySelectorAll('.nav-btn[data-route]')) {
    button.classList.toggle('is-active', button.dataset.route === target);
  }

  for (const view of document.querySelectorAll('.view')) {
    view.classList.toggle('is-active', view.dataset.view === target);
  }

  if (window.location.hash.slice(1) !== target) {
    window.location.hash = target;
  }

  if (target === 'diagnostics') {
    void refreshDiagnostics();
  }

  // 设置页首次进入时才加载，避免面板启动时就发起额外调用。
  if (target === 'settings' && !settingsLoaded) {
    settingsLoaded = true;
    void initSettings();
  }

  // 每次回到对话页都重新判定就绪状态：用户可能刚在设置页改过配置，
  // 沿用旧结论会一直显示已经不成立的错误。
  if (target === 'chat') {
    void refreshReady(force ? '点击页签' : '路由进入');
  }
}

const DIAG_LABELS = {
  host: '宿主',
  process: '宿主进程',
  bitness: '进程位数',
  hostName: '宿主名称',
  hostVersion: '宿主版本',
  hostBuild: '宿主内部版本',
  webview2: 'WebView2 运行时',
  clr: '.NET 运行时',
  addInVersion: '加载项版本',
  logPath: '日志路径',
};

async function refreshDiagnostics() {
  const list = document.getElementById('diag-list');
  const status = document.getElementById('diag-status');

  if (!isHosted()) {
    status.textContent = '未运行在宿主内';
    status.className = 'diag-status is-error';
    list.replaceChildren();
    return;
  }

  status.textContent = '读取中…';
  status.className = 'diag-status';

  try {
    const info = await request('host.info');
    list.replaceChildren();

    for (const [key, label] of Object.entries(DIAG_LABELS)) {
      const value = info[key];
      if (value === undefined || value === null || value === '') {
        continue;
      }

      const dt = document.createElement('dt');
      dt.textContent = label;
      const dd = document.createElement('dd');
      dd.textContent = String(value);
      list.append(dt, dd);
    }

    status.textContent = '正常';
    status.className = 'diag-status is-ok';
  } catch (error) {
    status.textContent = error.message;
    status.className = 'diag-status is-error';
    list.replaceChildren();
  }
}

function bindEvents() {
  // 同上，只绑带 data-route 的：主题切换按钮的点击由 theme.js 自己接，
  // 在这里也绑一遍会让它顺带跳一次路由。
  for (const button of document.querySelectorAll('.nav-btn[data-route]')) {
    // 主动点击一律强制刷新：用户点已激活的页签通常就是想重新加载一次。
    button.addEventListener('click', () => setRoute(button.dataset.route, { force: true }));
  }

  // 顶栏三个图标（两个页签与主题切换）的点击回弹。动画本身在 CSS 的 is-tapped
  // 规则里，这里只负责「每次点击都从头放一遍」。
  //
  // 选择器按 .app-nav .nav-btn 取，不按 [data-route] 筛：主题切换按钮不是页签、
  // 没有 data-route，但它同样需要这个反馈——它点了之后什么都不在原地发生。
  for (const button of document.querySelectorAll('.app-nav .nav-btn')) {
    button.addEventListener('click', () => {
      // 要求减少动效就不加类。加了也不会放（CSS 全局关掉了 animation），
      // 但 animationend 因此永不触发，类会留在按钮上；用户中途关掉系统的
      // 「减少动效」时，媒体查询实时翻转，那一帧所有残留的类会同时起播——
      // 图标凭空回弹一下。见 motion.js 的说明。
      if (prefersReducedMotion()) {
        return;
      }

      // 连点时上一次的类可能还在（动画不到 0.3s，但点得快就撞上）。对已带同名
      // 类的元素再 add 是无操作，动画不会重启，第二下就没有反馈。所以先摘掉、
      // 读一次布局把移除落到渲染里，再挂回去。
      button.classList.remove('is-tapped');
      void button.offsetWidth;
      button.classList.add('is-tapped');
    });

    // 放完即摘，取消也摘：动画被取消时只有 animationcancel 会来。
    // 类只带动画不带静态样式，残留本身不可见，但摘干净后按钮的状态与
    // 「此刻是否在放动画」始终一致，不必依赖上面那步兜着。
    const clearTap = () => button.classList.remove('is-tapped');
    button.addEventListener('animationend', clearTap);
    button.addEventListener('animationcancel', clearTap);
  }

  document.getElementById('diag-refresh')?.addEventListener('click', () => {
    void refreshDiagnostics();
  });

  window.addEventListener('hashchange', () => {
    setRoute(window.location.hash.slice(1));
  });

  // 用户拖动面板边界后上报一次布局：窄栏溢出只有在真实宽度下才暴露，
  // 这条记录能在事后定位「某个宽度下布局坏了」。
  // 同时把宽度存档，让下次打开直接恢复到用户拖成的宽度。
  let resizeTimer = null;
  window.addEventListener('resize', () => {
    if (resizeTimer) { clearTimeout(resizeTimer); }
    // 拖动过程中会连续触发，防抖后只报最终状态。
    resizeTimer = setTimeout(() => {
      resizeTimer = null;
      void logToHost(describeLayout());
      void rememberWidth();
    }, 400);

  });

  // 功能区的“设置”“诊断”按钮由加载项推送路由。
  on('navigate', (message) => setRoute(message.route));

  // 主题一确定就告诉加载项，之后每次切换再报一次。
  window.chatSheetTheme?.subscribe((theme) => { void reportTheme(theme); });
}

/**
 * 把当前主题报给加载项。
 *
 * 为什么面板要管这件事：面板外面那一圈是宿主的 WinForms 控件，CSS 管不到。
 * 它的底色写死白色时，深色主题下开面板会先看到一块白，WebView2 把页面画出来
 * 之后才变深。加载项按这个值给控件与 WebView2 的默认底色上色，并存进设置，
 * 下次打开在页面加载之前就已经是深色。
 *
 * 主题存在面板侧的 localStorage（要在首屏之前读到，只有这一处来得及），
 * 加载项那份是给自己上色用的副本，不作为权威值回读。
 */
async function reportTheme(theme) {
  if (!isHosted()) {
    return;
  }

  try {
    await request('pane.saveTheme', { theme });
  } catch (error) {
    // 报不上去只是宿主底色仍是上一次的，页面自身的主题不受影响。
    await logToHost(`同步主题到宿主失败：${error.message}`, 'warn');
  }
}

/**
 * 启动自检。把页面加载和消息桥往返的结果写进加载项日志，
 * 这样在 Excel / WPS 里出问题时，不用附加调试器也能定位到失败环节。
 */
async function reportStartup() {
  if (!isHosted()) {
    return;
  }

  try {
    const info = await request('host.info');
    await logToHost(
      `页面已加载，消息桥连通。宿主=${info.host} 位数=${info.bitness} WebView2=${info.webview2}`,
    );
    await logToHost(describeLayout());
  } catch (error) {
    await logToHost(`页面已加载，但消息桥调用失败：${error.message}`, 'error');
  }
}

/**
 * 上报渲染后的实际布局度量。
 * 日志只能证明页面加载成功，无法证明布局正确；
 * 这些数字可用来判断关键元素是否真的可见、有没有塌陷或溢出。
 */
function describeLayout() {
  const measure = (id) => {
    const node = document.getElementById(id);
    if (!node) { return `${id}=缺失`; }
    const rect = node.getBoundingClientRect();
    return `${id}=${Math.round(rect.width)}x${Math.round(rect.height)}`;
  };

  const overflowX = document.documentElement.scrollWidth > document.documentElement.clientWidth;
  const activeView = document.querySelector('.view.is-active')?.dataset.view ?? '<无>';

  const measurePlain = (id) => {
    const node = document.getElementById(id);
    if (!node) { return `${id}=缺失`; }
    const rect = node.getBoundingClientRect();
    return `${id}=${Math.round(rect.width)}x${Math.round(rect.height)}`;
  };

  // 控件行必须始终是一行。报告实际占了几行：超过 1 就是布局缺陷，
  // 换行会吃掉输入框的高度，而且只在特定内容下触发（例如模型 ID 过长），
  // 不记录的话事后无从知道是在哪个宽度、哪个模型下坏的。
  const controls = document.querySelector('.chat-controls');
  let controlsRow = 'chat-controls=缺失';
  if (controls) {
    const rect = controls.getBoundingClientRect();
    const children = Array.from(controls.children);
    const tops = new Set(children.map((c) => Math.round(c.getBoundingClientRect().top)));
    controlsRow = `chat-controls=${Math.round(rect.width)}x${Math.round(rect.height)}` +
      `/${children.length}项/${tops.size}行${tops.size > 1 ? '（应为 1 行，布局缺陷）' : ''}`;
  }

  const ring = document.getElementById('context-ring');
  const ringSize = ring
    ? `context-ring=${Math.round(ring.getBoundingClientRect().width)}px`
    : 'context-ring=缺失';

  /**
   * 报告左右分组的实际位置。
   * 左组应贴容器左缘，右组应贴右缘——用像素差值验证，
   * 比肉眼看更可靠，也能在窄栏下发现挤压。
   */
  const describeGrouping = () => {
    const row = document.querySelector('.chat-controls');
    const left = document.getElementById('model-picker');
    const right = document.querySelector('.controls-right');
    if (!row || !left || !right) { return '分组=元素缺失'; }

    const rowRect = row.getBoundingClientRect();
    const leftRect = left.getBoundingClientRect();
    const rightRect = right.getBoundingClientRect();

    const leftGap = Math.round(leftRect.left - rowRect.left);
    const rightGap = Math.round(rowRect.right - rightRect.right);
    const between = Math.round(rightRect.left - leftRect.right);

    return `分组=左距${leftGap}px 右距${rightGap}px 间隔${between}px`;
  };

  return [
    `布局：视口 ${window.innerWidth}x${window.innerHeight}`,
    `当前页=${activeView}`,
    measure('transcript'),
    measure('composer'),
    measure('send'),
    measurePlain('picker-trigger'),
    measurePlain('approval-icon'),
    controlsRow,
    ringSize,
    // 左右分组的验证：左组应贴左、右组应贴右。
    describeGrouping(),
    describePicker(),
    describeAttachments(),
    `欢迎语=${document.querySelectorAll('.welcome').length} 个`,
    // 工具卡片默认应为折叠状态。
    `工具卡片=${document.querySelectorAll('.tool-card').length} 个（展开 ${document.querySelectorAll('.tool-card[open]').length}）`,
    // 轮次组同样默认折叠。往前几轮的操作都应当已经收进组里。
    `轮次组=${document.querySelectorAll('.ops-group').length} 个（展开 ${document.querySelectorAll('.ops-group[open]').length}）`,
    // 页签只数带 data-route 的，主题切换按钮虽同为 .nav-btn 但不是页签。
    `页签=${document.querySelectorAll('.nav-btn[data-route]').length} 个`,
    // 主题写进布局日志：深色下的显示问题事后只能靠这一行判断当时是哪套配色。
    `主题=${document.documentElement.dataset.theme ?? '未设置'}`,
    // 横向溢出是窄栏布局最常见的缺陷，必须显式检查。
    `横向溢出=${overflowX ? '有（布局缺陷）' : '无'}`,
  ].join(' ');
}

/**
 * 请求把面板加宽到可用宽度。
 *
 * 宿主的宽度单位随显示缩放变化，面板这边只知道自己的 CSS 像素数，
 * 因此把「当前值、目标值与设备像素比」一起交给加载项换算。
 * 只在明显过窄时请求一次，尊重用户手动拖拽的宽度。
 */
const TARGET_WIDTH_CSS = 400;

/**
 * 等视口宽度稳定后再返回。
 *
 * 必须等：面板显示的瞬间宿主还在调整窗格尺寸，此刻测到的是过渡值。
 * 拿过渡值去换算，算出的目标宽度会偏大，表现为面板先窄一下再猛地拉宽。
 */
function waitForStableWidth({ interval = 100, needed = 3, timeout = 2000 } = {}) {
  return new Promise((resolve) => {
    const deadline = Date.now() + timeout;
    let last = window.innerWidth;
    let stable = 0;

    const tick = () => {
      const now = window.innerWidth;
      if (now === last) {
        stable += 1;
        if (stable >= needed) {
          resolve(now);
          return;
        }
      } else {
        stable = 0;
        last = now;
      }

      if (Date.now() >= deadline) {
        resolve(now);
        return;
      }

      setTimeout(tick, interval);
    };

    setTimeout(tick, interval);
  });
}

async function ensureUsableWidth() {
  if (!isHosted()) {
    return;
  }

  const current = await waitForStableWidth();
  if (current >= TARGET_WIDTH_CSS) {
    // 已经够宽：把当前宽度存档，下次打开就不必再走校准这条路。
    await rememberWidth();
    return;
  }

  try {
    const result = await request('pane.ensureWidth', {
      currentCss: current,
      targetCss: TARGET_WIDTH_CSS,
      devicePixelRatio: window.devicePixelRatio || 1,
    });

    if (result?.adjusted) {
      await logToHost(
        `已请求加宽面板：${current} → 目标 ${TARGET_WIDTH_CSS} CSS 像素` +
          `（缩放 ${window.devicePixelRatio || 1}），宿主宽度 ${result.hostWidth}`,
      );
    }
  } catch (error) {
    await logToHost(`加宽面板失败：${error.message}`, 'warn');
  }
}

/**
 * 让加载项记住当前宽度。
 * 宽度值由加载项自己读取，面板只负责说「现在可以记了」。
 */
async function rememberWidth() {
  if (!isHosted()) {
    return;
  }

  try {
    await request('pane.saveWidth');
  } catch (error) {
    await logToHost(`记录面板宽度失败：${error.message}`, 'warn');
  }
}

bindEvents();
// 点了点不动的按钮抖一下。装在文档上，一次覆盖面板里所有禁用按钮
// （禁用的按钮不派发点击事件，绑在按钮上收不到，见 motion.js）。
initRefusalShake();
initChat();
setRoute(window.location.hash.slice(1) || 'chat');
void reportStartup();
void ensureUsableWidth();
