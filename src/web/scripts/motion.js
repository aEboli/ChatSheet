// 动效的一个共同前提：用户有没有要求减少动效。
//
// 为什么要在 JS 里再判一次——CSS 那边 @media (prefers-reduced-motion: reduce)
// 已经把 animation 全关掉了，看着够了。不够的地方在于：动画不起播时
// animationend 与 animationcancel 都不会来，而本项目的两处动画都靠这两个事件
// 把类摘掉（对话流的 is-entering、顶栏图标的 is-tapped）。类于是永久留在节点上。
//
// 单看没有后果——两个类都只带 animation、不带静态样式，不放动画就什么都不显示。
// 出事的是用户中途在系统设置里改「动画效果」：WebView2 实时跟踪这个开关，
// reduce → no-preference 的那一帧，所有残留节点的 animation-name 同时从 none
// 变成真实的关键帧名，按规范全部立即起播。用户什么都没做，整条对话流一起淡入
// 一次、顶栏图标凭空回弹一下。0.2-0.3s 后各自结束并自愈，但那一下是凭空来的。
//
// 所以加类之前先问一次：要求减少动效就整个跳过，连类都不加。

/**
 * 用户是否要求减少动效。
 *
 * 每次都现问，不缓存结果：这个偏好在会话中途会变（用户改 Windows 的
 * 「动画效果」开关），缓存住等于把首次读到的值当成永久事实。
 *
 * matchMedia 缺失时返回 false（照常放动画）：那是极旧的运行时，
 * 此时把动画一律关掉比照常播更让人意外。
 */
export function prefersReducedMotion() {
  return Boolean(
    window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches,
  );
}

/**
 * 放一次「点不动」的抖动。
 *
 * 抽出来单独导出，是为了让点击入口与动画的重放规则分开：调用方只需说
 * 「这个元素被点了但点不动」，什么时候不该放、连点怎么重来都在这里。
 */
export function playRefusal(element) {
  if (!element || prefersReducedMotion()) {
    return false;
  }

  // 连点要每次都从头放。对已带同名类的元素再 add 是无操作，动画不会重启，
  // 第二下就没有反馈——先摘掉、读一次布局把移除落到渲染里，再挂回去。
  element.classList.remove('is-refusing');
  void element.offsetWidth;
  element.classList.add('is-refusing');
  return true;
}

/**
 * 让「点了点不动的按钮」抖一下。整个面板装一次即可。
 *
 * 为什么是文档级的捕获监听，而不是给每个禁用按钮绑事件：
 *
 *   一、**禁用的按钮不派发点击事件。** 绑在按钮上的 click / pointerdown 一次
 *       也不会来，这正是「点了没反应」的根源。所以监听必须在按钮之外。
 *   二、事件从禁用按钮身上不冒泡，但指针命中测试照常命中它——
 *       document.elementFromPoint 拿得到那个按钮。于是「指针落点下面是不是
 *       一个禁用按钮」这件事只能这么问。
 *   三、面板里有六处会把按钮设成禁用（发送、撤销、批量确认、批量测试、
 *       设置页的取模型与保存、审批卡片的三个按钮）。装一次覆盖全部，
 *       也覆盖以后新加的——逐处绑等于每次新增都要记得再绑一遍。
 *
 * 用 pointerdown 而不是 click：click 在禁用按钮上根本不会产生（连它的祖先
 * 也收不到），而 pointerdown 是指针动作本身，与元素的禁用状态无关。
 */
export function initRefusalShake(root = document) {
  root.addEventListener('pointerdown', (event) => {
    // 只认真实指针。键盘触发不到这里——禁用按钮拿不到焦点，本就按不下。
    if (event.button !== undefined && event.button !== 0) {
      return;
    }

    const hit = document.elementFromPoint(event.clientX, event.clientY);
    if (!hit) {
      return;
    }

    // 命中的可能是按钮里的图标或文字，往上找到按钮本身。
    // aria-disabled 一并认：那是「看得见、点不动」的另一种写法，
    // 用它的控件仍然可点可聚焦，反馈同样该给。
    const button = hit.closest('button:disabled, [aria-disabled="true"]');
    if (!button) {
      return;
    }

    playRefusal(button);
  }, true);

  // 放完即摘，取消也摘：动画被取消时只有 animationcancel 会来。
  // 委托到根上而不是逐个绑：抖动的元素是临时确定的，逐个绑要在
  // playRefusal 里每次 addEventListener，同一个按钮点十次就挂十个监听。
  //
  // 摘之前必须确认此刻真的没有抖动在跑。原因是连点：playRefusal 为了重放会
  // 先摘类，那一下让运行中的动画被取消，而 animationcancel 是**异步派发**的——
  // 等它到达时，重放的新动画已经在跑了。不加这层判断就会把刚加上的类又摘掉，
  // 于是第二下点击看不到任何反馈。真实鼠标连点两下、只抖一次，就是这个原因。
  const clear = (event) => {
    if (event.animationName !== 'refuse-shake') {
      return;
    }

    const node = event.target;
    const running = node.getAnimations
      ? node.getAnimations().some((a) => a.animationName === 'refuse-shake')
      : false;
    if (!running) {
      node.classList?.remove('is-refusing');
    }
  };
  root.addEventListener('animationend', clear, true);
  root.addEventListener('animationcancel', clear, true);
}
