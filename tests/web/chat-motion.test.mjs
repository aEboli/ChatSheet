// 对话界面的动效：顶栏图标点击回弹、对话流进场动画、气泡方向角、按下反馈。
//
// 这个文件盯的全是「不会报错、只会静默不动或画错」的地方：
//
//   一、animation 引用的关键帧名与 @keyframes 定义必须逐字一致。
//       名字打错时那行 animation 看着完全正常，动画只是静默不放。
//   二、is-tapped / is-entering 只许带 animation，不许带静态样式。
//       减少动效时全局把动画关掉，animationend 不触发、类会留在节点上——
//       带静态样式（比如初始 opacity: 0）就是一条永远看不见的消息。
//   三、重放逻辑必须是「先摘、读一次布局、再挂」这个顺序。
//       对已带此类的元素再 add 同名类不会重启动画，连点第二下就没有反馈。
//   四、进场动画只许动 opacity 与 transform。二者不参与布局，
//       挂载后紧跟的 scrollTop = scrollHeight 才量得到真实高度。
//   五、进场类只在首挂时加。重复挂载（指示器移到末尾、还原重排）再淡入
//       一次是闪烁；而重插 DOM 本身就会重启动画，靠的就是类已被摘掉。
//
// 与 pending-ring.test.mjs 同理，几何与接线是算出来的，不是看出来的。

import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const webDir = join(here, '..', '..', 'src', 'web');
const css = readFileSync(join(webDir, 'styles', 'app.css'), 'utf8');
const html = readFileSync(join(webDir, 'index.html'), 'utf8');
const appJs = readFileSync(join(webDir, 'scripts', 'app.js'), 'utf8');
const chatJs = readFileSync(join(webDir, 'scripts', 'chat.js'), 'utf8');
const motionJs = readFileSync(join(webDir, 'scripts', 'motion.js'), 'utf8');

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

/**
 * 去掉块注释。
 *
 * 必须去：本项目习惯在声明块内写大段注释，而下面按 `词:` 提取属性名的正则
 * 分不清注释与声明。注释里出现「ASCII 词 + 半角冒号」（`WebView2: 见…`）会被
 * 当成一个属性，「只带 animation」这类断言于是对正确实现判红；反过来，声明
 * 紧贴注释结尾（`/*…*​/opacity: 0`）时前一个字符是斜杠，不在分隔符集合里，
 * 那条声明会被漏掉——而漏掉的恰好可能是不该存在的静态样式。
 */
const stripComments = (text) => text.replace(/\/\*[\s\S]*?\*\//g, ' ');

const cleanCss = stripComments(css);

/** 同一选择器的全部声明块，按出现顺序。 */
function rules(selector) {
  const blocks = [];
  let from = 0;
  const needle = `\n${selector} {`;
  for (;;) {
    const start = cleanCss.indexOf(needle, from);
    if (start === -1) { break; }
    const open = cleanCss.indexOf('{', start);
    const close = cleanCss.indexOf('}', open);
    blocks.push(cleanCss.slice(open + 1, close));
    from = close;
  }
  return blocks;
}

/**
 * 取一条规则的声明块。
 *
 * 同名选择器出现多次时把各块拼起来，而不是只取第一条。取第一条会放过一整类
 * 缺陷：文件后面再写一条同样的选择器（调试残留、后续特性覆盖都常见），层叠
 * 上后者获胜，真机上动画/按下反馈完全死掉，而断言读的还是第一条，全绿。
 * 拼起来之后，后写的 `animation: none` 会出现在块里，「只带 animation」之外的
 * 断言（引用了哪个关键帧、时长多少）也会连同后者一起被看到。
 */
function rule(selector) {
  return rules(selector).join(';');
}

/**
 * 全文件的规则表：[选择器列表, 声明块]。
 * 只取顶层规则，@media 与 @keyframes 里的不算（前者是条件生效，
 * 后者的「选择器」是百分比）。
 */
const ruleTable = (() => {
  // 先整块剥掉 @media / @keyframes：它们内部还有一层花括号，留着会让下面的
  // 「选择器 { 声明 }」扫描把嵌套内容当成顶层规则。
  let flat = cleanCss;
  for (;;) {
    const m = /@[^{}]*\{/.exec(flat);
    if (!m) { break; }
    const open = m.index + m[0].length - 1;
    let depth = 0;
    let end = -1;
    for (let i = open; i < flat.length; i++) {
      if (flat[i] === '{') { depth += 1; }
      if (flat[i] === '}') {
        depth -= 1;
        if (depth === 0) { end = i; break; }
      }
    }
    if (end === -1) { break; }
    flat = flat.slice(0, m.index) + flat.slice(end + 1);
  }

  return [...flat.matchAll(/([^{}]+)\{([^{}]*)\}/g)]
    .map((m) => [m[1].trim(), m[2]]);
})();

/**
 * 某个属性是否被多条规则声明——即后写的那条会盖掉前面的。
 *
 * 按选择器列表的成员匹配，不按整段文本：真实规则常写成多选择器合写
 * （`.icon-btn:active, .approval-icon:active, …`），只按行首精确找会漏掉它，
 * 于是「文件末尾追加一条 .icon-btn:active { transform: none }」这种把按下
 * 反馈整个盖死的改动会被判成「只有一条、没被覆盖」。
 */
function declaringBlocks(selector, prop) {
  return ruleTable.filter(([sel, body]) =>
    sel.split(',').some((s) => s.trim() === selector) &&
    new RegExp(`(^|[\\s;])${prop}\\s*:`).test(body));
}

function overriddenLater(selector, prop) {
  return declaringBlocks(selector, prop).length > 1;
}

/** 取一段 @keyframes 的正文（含嵌套花括号）。
    名字后面必须紧跟空白或花括号：nav-tap 是 nav-tapx 的前缀子串，
    按子串找会让「名字打错」这种静默失败照样全绿。 */
function keyframes(name) {
  const m = new RegExp(`@keyframes ${name}[\\s{]`).exec(cleanCss);
  if (!m) { return ''; }
  const open = cleanCss.indexOf('{', m.index);
  let depth = 0;
  for (let i = open; i < cleanCss.length; i++) {
    if (cleanCss[i] === '{') { depth += 1; }
    if (cleanCss[i] === '}') {
      depth -= 1;
      if (depth === 0) { return cleanCss.slice(open + 1, i); }
    }
  }
  return '';
}

/** 声明块里的属性名列表（去重）。注释已在 cleanCss 里剥掉。 */
function props(block) {
  return [...new Set([...block.matchAll(/(?:^|[\s{;])([a-zA-Z-]+)\s*:/g)].map((m) => m[1]))];
}

/**
 * 解析一条 animation 简写，返回 {name, ms, iterations, delayMs}。
 *
 * 不按「名字在前、单位是秒」写死：`animation: 0.26s ease nav-tap` 与 `260ms`
 * 都是合法等价写法，写死会对正确实现判红。同时把第二个时间值（delay）与
 * 重复次数一并取出来——只看第一个时间值会放过两种「短促的一次」名存实亡的
 * 写法：加一个 1.5s 的 delay（消息先显示、1.5s 后突然消失再淡入），
 * 或把重复次数写成 3（弹三下）。
 */
function animation(block) {
  const m = /(?:^|[\s;])animation:\s*([^;]+)/.exec(block);
  if (!m) { return null; }

  const parts = m[1].trim().split(/\s+/);
  const times = [];
  let iterations = 1;
  let name = null;

  for (const part of parts) {
    const t = /^(-?[\d.]+)(ms|s)$/.exec(part);
    if (t) {
      times.push(parseFloat(t[1]) * (t[2] === 'ms' ? 1 : 1000));
      continue;
    }
    if (part === 'infinite') { iterations = Infinity; continue; }
    if (/^[\d.]+$/.test(part)) { iterations = parseFloat(part); continue; }
    // 剩下的是关键帧名或时间函数/方向/填充模式等关键字。
    const KEYWORDS = new Set([
      'ease', 'ease-in', 'ease-out', 'ease-in-out', 'linear', 'step-start', 'step-end',
      'normal', 'reverse', 'alternate', 'alternate-reverse',
      'none', 'forwards', 'backwards', 'both',
      'running', 'paused',
    ]);
    if (!KEYWORDS.has(part) && !part.startsWith('cubic-bezier') && !part.startsWith('steps')) {
      name = name ?? part;
    }
  }

  return { name, ms: times[0] ?? null, delayMs: times[1] ?? 0, iterations };
}

/** 声明块里 scale(x) 的 x。 */
function scaleOf(text) {
  const m = /scale\(([\d.]+)\)/.exec(text);
  return m ? parseFloat(m[1]) : null;
}

/* ---- 顶栏图标的点击回弹 ---- */

console.log('检查顶栏图标的点击动画接线：');

check(
  'app.js 给 .app-nav .nav-btn 绑了点击回弹（三个按钮都在其列）',
  /querySelectorAll\('\.app-nav \.nav-btn'\)/.test(appJs),
  '按 .nav-btn[data-route] 绑会漏掉主题切换按钮',
);

// 主题按钮必须真的在 .app-nav 里，否则上面那个选择器选不到它。
// 这一条只有读 index.html 才验得到：把 #theme-toggle 挪出 <nav class="app-nav">
// 或去掉它的 nav-btn 类，绑定会静默漏掉它、回弹消失，而 app.js 分毫未改。
const navBlock = (() => {
  const start = html.indexOf('<nav class="app-nav">');
  if (start === -1) { return ''; }
  const end = html.indexOf('</nav>', start);
  return end === -1 ? '' : html.slice(start, end);
})();

check(
  'index.html 里主题切换按钮在 .app-nav 内且带 nav-btn 类',
  /id="theme-toggle"/.test(navBlock) &&
    /class="nav-btn"[^>]*id="theme-toggle"|id="theme-toggle"[^>]*class="nav-btn"/.test(
      navBlock.replace(/\s+/g, ' '),
    ),
  '挪出 .app-nav 或去掉 nav-btn 类，绑定会静默漏掉它',
);

check(
  'index.html 里两个页签也带 nav-btn 类',
  (navBlock.match(/class="nav-btn[^"]*"/g) ?? []).length >= 3,
  `实测 ${(navBlock.match(/class="nav-btn[^"]*"/g) ?? []).length} 个 nav-btn`,
);

/**
 * 取出 click 回调的函数体。
 *
 * 顺序断言必须限定在这里面，不能对整个文件做 indexOf：三处代码可以分属不同
 * 回调，而 remove < reflow < add 照样成立。实测过的假绿场景是把 animationend
 * 的摘类注册挪到 click 之前、同时删掉 click 回调里的「先摘掉」——顺序断言全过，
 * 但连点第二下不再重启动画，正是这条断言声称要守的东西。
 */
const clickBody = (() => {
  /** 从 from 处的第一个 { 起做花括号配平，返回块内文本。 */
  const blockAt = (from) => {
    const open = appJs.indexOf('{', from);
    if (open === -1) { return ''; }
    let depth = 0;
    for (let i = open; i < appJs.length; i++) {
      if (appJs[i] === '{') { depth += 1; }
      if (appJs[i] === '}') {
        depth -= 1;
        if (depth === 0) { return appJs.slice(open + 1, i); }
      }
    }
    return '';
  };

  // 先定位到 .app-nav 那个循环，再在它里面找 click——文件里更早还有页签的
  // 路由监听，直接找第一个 click 会切到它的对象字面量 { force: true } 上。
  const loop = appJs.indexOf("querySelectorAll('.app-nav .nav-btn')");
  if (loop === -1) { return ''; }
  const loopBody = blockAt(loop);
  const at = loopBody.indexOf("addEventListener('click'");
  if (at === -1) { return ''; }

  // 回调体在 loopBody 内，重新按同样的方式配平。
  const open = loopBody.indexOf('{', at);
  if (open === -1) { return ''; }
  let depth = 0;
  for (let i = open; i < loopBody.length; i++) {
    if (loopBody[i] === '{') { depth += 1; }
    if (loopBody[i] === '}') {
      depth -= 1;
      if (depth === 0) { return loopBody.slice(open + 1, i); }
    }
  }
  return '';
})();

check('找到了 click 回调（顺序断言限定在它里面）', clickBody !== '');

const removeIdx = clickBody.indexOf("classList.remove('is-tapped')");
const reflowIdx = clickBody.indexOf('offsetWidth');
const addIdx = clickBody.indexOf("classList.add('is-tapped')");

check(
  'click 回调里的重放顺序是「先摘、读一次布局、再挂」',
  removeIdx !== -1 && reflowIdx !== -1 && addIdx !== -1 &&
    removeIdx < reflowIdx && reflowIdx < addIdx,
  `remove@${removeIdx} reflow@${reflowIdx} add@${addIdx}——` +
    '少了中间那步，连点第二下不会重新触发动画',
);

// 摘类要同时听两种结束事件。断言事件名，不逐字匹配回调写法——
// 后者会在无害的重构（把两个监听收成一个命名函数）后误报。
for (const event of ['animationend', 'animationcancel']) {
  check(
    `app.js 听 ${event} 摘掉回弹类`,
    new RegExp(`addEventListener\\('${event}'`).test(appJs),
    event === 'animationcancel'
      ? '动画被取消时只有这个事件会来，只听 animationend 会让类残留'
      : '不摘的话下一次点击全靠「先摘掉」那步兜着',
  );
}

check(
  'app.js 在加类之前先问减少动效',
  /prefersReducedMotion\(\)/.test(appJs) &&
    clickBody.indexOf('prefersReducedMotion()') !== -1 &&
    clickBody.indexOf('prefersReducedMotion()') < addIdx,
  '减少动效下动画不起播，animationend 永不触发，类会留在按钮上；' +
    '用户中途关掉该设置时所有残留的类会同时起播',
);

const tapRule = rule('.nav-btn.is-tapped svg');
const themeTapRule = rule('#theme-toggle.is-tapped svg');

check('有 .nav-btn.is-tapped svg 这条规则', tapRule.trim() !== '');
check(
  '主题切换另有一套（ID 选择器压过类规则）',
  themeTapRule.trim() !== '',
  '缺了它，换图标的瞬间缩放回弹读不出来',
);

/**
 * 「短促的一次」这句话要守四件事，缺一件它就名存实亡：
 * 时长有且不长、只放一次、没有 delay（有 delay 时元素先正常显示、
 * 到点才突然跳到起始帧再放一次）、引用的关键帧确实存在。
 */
function checkOneShot(label, selector, expectedName, maxMs) {
  // 只许声明一次。声明两次时层叠上后写的赢，而按属性名取第一个匹配的解析
  // 读到的仍是前一条——文件末尾追加一句 `animation: none` 就能让动画在真机上
  // 完全消失而断言全绿。
  const blocks = declaringBlocks(selector, 'animation');
  check(
    `${label}（${selector}）只声明一次 animation（实测 ${blocks.length} 处）`,
    blocks.length === 1,
    '声明多次时后写的那条获胜，按第一条取值的断言读到的不是真正生效的动画',
  );

  const a = blocks.length ? animation(blocks[blocks.length - 1][1]) : null;
  check(
    `${label} 引用的关键帧存在（${expectedName}）`,
    a !== null && a.name === expectedName && keyframes(expectedName).trim() !== '',
    a === null ? '没有 animation 声明' : `解析到名字 ${a.name}`,
  );
  check(
    `${label} 是短促的一次（${a?.ms}ms ≤ ${maxMs}ms、重复 ${a?.iterations} 次、delay ${a?.delayMs}ms）`,
    a !== null && a.ms !== null && a.ms > 0 && a.ms <= maxMs &&
      a.iterations === 1 && a.delayMs === 0,
    '拖长、循环、弹多下、或带 delay（先显示再突然重放一次）都不叫短促的一次',
  );
}

checkOneShot('页签回弹', '.nav-btn.is-tapped svg', 'nav-tap', 500);
checkOneShot('主题回弹', '#theme-toggle.is-tapped svg', 'theme-tap', 500);

for (const [label, block] of [
  ['.nav-btn.is-tapped svg', tapRule],
  ['#theme-toggle.is-tapped svg', themeTapRule],
]) {
  check(
    `${label} 只带 animation，无静态样式`,
    props(block).every((p) => p.startsWith('animation')),
    `实际属性：${props(block).join('、')}——减少动效时类会留在按钮上，静态样式就是永久残留`,
  );
}

for (const name of ['nav-tap', 'theme-tap']) {
  const body = keyframes(name);
  check(
    `${name} 关键帧只动 transform 与 opacity`,
    props(body).length > 0 && props(body).every((p) => p === 'transform' || p === 'opacity'),
    `实际属性：${props(body).join('、')}`,
  );
}

const activeScale = scaleOf(rule('.nav-btn:active svg'));
const tapStart = (() => {
  const m = /0%\s*\{([^}]*)\}/.exec(keyframes('nav-tap'));
  return m ? scaleOf(m[1]) : null;
})();
check(
  `回弹起点与按下的缩小档一致（:active ${activeScale} = 0% 帧 ${tapStart}）`,
  activeScale !== null && activeScale < 1 && activeScale === tapStart,
  '两档不等时松手的瞬间图标会跳一下',
);

/* ---- 对话流的进场动画 ---- */

console.log('');
console.log('检查对话流的进场动画：');

const fnStart = chatJs.indexOf('function mountToTranscript');
const fnEnd = chatJs.indexOf('\n}', fnStart);
const mountFn = fnStart !== -1 ? chatJs.slice(fnStart, fnEnd) : '';

check('找到 mountToTranscript', mountFn !== '');

// 它必须是唯一的挂载入口。绕过它直接 transcript.append 的节点拿不到 seq，
// restoreOpsGroup 按 Number(seq ?? 0) 排序时它恒为 0，「还原」一次就被排到
// 对话流最前面——而且也拿不到进场动画。只断言函数存在锁不住这件事。
const appendCount = (chatJs.match(/transcript\.append\(/g) ?? []).length;
check(
  `transcript.append 只出现在 mountToTranscript 里（实测 ${appendCount} 处）`,
  appendCount === 1 && mountFn.includes('transcript.append('),
  '绕过挂载入口的节点没有序号，还原时会被排到最前面，也没有进场动画',
);

const seqWriteIdx = mountFn.indexOf('node.dataset.seq = String');
const firstMountGuard = mountFn.indexOf('if (node.dataset.seq)');
const removeOnRemount = mountFn.indexOf("classList.remove('is-entering')");
const enterIdx = mountFn.indexOf("classList.add('is-entering')");

check(
  '以 seq 在不在分首挂与重挂，且判断先于写入',
  firstMountGuard !== -1 && seqWriteIdx !== -1 && firstMountGuard < seqWriteIdx,
  `guard@${firstMountGuard} seq@${seqWriteIdx}——写在前面的话每次都被当成重挂`,
);

// 这一条锁住的是真实渲染器里量到的那个缺陷（进度从 170ms 退回 0ms）：
// append 一个已是子节点的元素等于「先摘再插」，移出文档会取消动画、插回去
// 又重新起播。所以重挂时必须主动把类摘掉，「只在首挂时加类」挡不住重播。
check(
  '重挂时先把进场类摘掉（否则 append 会把动画从头重播）',
  removeOnRemount !== -1 && enterIdx !== -1 &&
    removeOnRemount < enterIdx && removeOnRemount > firstMountGuard &&
    removeOnRemount < seqWriteIdx,
  `remove@${removeOnRemount} add@${enterIdx} guard@${firstMountGuard}——` +
    'PaneHarness --motion 会当场量到进度退回 0ms',
);

check(
  '加类之前先问减少动效',
  mountFn.includes('prefersReducedMotion()') &&
    mountFn.indexOf('prefersReducedMotion()') < enterIdx,
  '减少动效下动画不起播，两种结束事件都不来，类会永久残留；' +
    '用户中途关掉该设置时整条对话流会同时淡入一次',
);

check(
  'chat.js 从 motion.js 导入这个判断（不各写一份）',
  /import \{ prefersReducedMotion \} from '\.\/motion\.js'/.test(chatJs) &&
    /export function prefersReducedMotion/.test(motionJs),
  '',
);

check(
  '不缓存减少动效的结果（这个偏好会在会话中途变）',
  !/(const|let|var)\s+\w*[Rr]educed\w*\s*=\s*window\.matchMedia/.test(motionJs),
  '缓存住等于把首次读到的值当成永久事实',
);

for (const event of ['animationend', 'animationcancel']) {
  check(
    `听 ${event} 摘掉进场类`,
    new RegExp(`addEventListener\\('${event}'`).test(mountFn),
    event === 'animationcancel'
      ? 'sealOpsBatch 把仍在动的卡片搬进未渲染的组容器时只有这个事件会来'
      : '',
  );
}

check(
  '摘类前核对 event.target',
  mountFn.includes('event.target === node') &&
    mountFn.includes("classList.remove('is-entering')"),
  '气泡里那圈点的动画事件冒泡上来会提前把进场动画掐掉',
);

const enterRule = rule('.is-entering');
checkOneShot('进场', '.is-entering', 'transcript-enter', 300);

check(
  '.is-entering 只带 animation，无静态样式',
  enterRule.trim() !== '' && props(enterRule).every((p) => p.startsWith('animation')),
  `实际属性：${props(enterRule).join('、')}——初始 opacity 写进类里，` +
    '任何一条没能摘掉类的路径都会变成一条永远看不见的消息',
);

const enterKf = keyframes('transcript-enter');
check(
  '进场只动 opacity 与 transform（不改布局，滚动定位量到的高度与动画无关）',
  props(enterKf).length > 0 && props(enterKf).every((p) => p === 'opacity' || p === 'transform'),
  `实际属性：${props(enterKf).join('、')}`,
);

check(
  '起点是透明的（淡入才成立）',
  /from\s*\{[^}]*opacity:\s*0[;\s]/.test(enterKf),
  enterKf.trim(),
);

/* ---- 气泡的方向角 ---- */

console.log('');
console.log('检查气泡圆角：');

const bodyRadius = (() => {
  const m = /border-radius:\s*([\d.]+)px/.exec(rule('.msg-body'));
  return m ? parseFloat(m[1]) : null;
})();

const cornerOf = (selector, prop) => {
  for (const block of rules(selector)) {
    const m = new RegExp(`${prop}:\\s*([\\d.]+)px`).exec(block);
    if (m) { return parseFloat(m[1]); }
  }
  return null;
};

const userCorner = cornerOf('.msg-user .msg-body', 'border-bottom-right-radius');
const assistantCorner = cornerOf('.msg-assistant .msg-body', 'border-bottom-left-radius');

check(`气泡主圆角存在（${bodyRadius}px）`, bodyRadius !== null && bodyRadius > 0);

// 类名是 JS 拼出来的（`msg-${role}`），CSS 侧的断言全过也可能是死代码：
// role 的取值或模板一改，规则就再也命中不了任何元素，方向角与气泡配色一起失效，
// 而搜「msg-user」这个字面量在 chat.js 里根本搜不到。
check(
  'chat.js 用 `msg-${role}` 拼类名，且 role 取值仍是 user/assistant',
  /className = `msg msg-\$\{role\}`/.test(chatJs) &&
    /buildBubble\('user'|addBubble\('user'/.test(chatJs) &&
    /addBubble\('assistant'|buildBubble\('assistant'/.test(chatJs),
  'CSS 里的 .msg-user / .msg-assistant 与 JS 拼出来的类名必须对得上，' +
    '否则那些规则是死代码',
);

// 方向角是 longhand（border-bottom-*-radius），任何一条落在同一选择器上的
// border-radius 简写都会把它连带重置——层叠上简写在后就赢，而按属性名取值的
// 断言仍读到 4px，全绿。主圆角写在 .msg-body 上，因此这两个更具体的选择器上
// 本就不该出现简写。
for (const selector of ['.msg-user .msg-body', '.msg-assistant .msg-body']) {
  const shorthand = declaringBlocks(selector, 'border-radius');
  check(
    `${selector} 上没有 border-radius 简写（简写会连带重置方向角）`,
    shorthand.length === 0,
    `实测 ${shorthand.length} 处简写；主圆角应写在 .msg-body 上`,
  );
}
check(
  '用户气泡收的是右下角（用户在右）',
  userCorner !== null,
  '方向角收错边等于给角色指反了方向',
);
check(
  '助手气泡收的是左下角（助手在左）',
  assistantCorner !== null,
  '方向角收错边等于给角色指反了方向',
);
check(
  `方向角比主圆角小（${userCorner}px / ${assistantCorner}px < ${bodyRadius}px）`,
  userCorner !== null && assistantCorner !== null && bodyRadius !== null &&
    userCorner < bodyRadius && assistantCorner < bodyRadius,
  '不小于主圆角就不成其为「角」，方向线索消失',
);
check(
  '两侧方向角对称',
  userCorner !== null && userCorner === assistantCorner,
  `${userCorner} / ${assistantCorner}`,
);

/* ---- 按下反馈与减少动效 ---- */

console.log('');
console.log('检查按下反馈与减少动效：');

// 三个选择器都要带 :not(:disabled)：禁用的按钮没有「按下」这件事，
// 缩一下等于说点成了，而它此刻给的反馈应当是抖动。
const pressSelectors = [
  '.icon-btn:not(:disabled):active',
  '.approval-icon:not(:disabled):active',
  '.context-ring:not(:disabled):active',
];
const pressBlocks = pressSelectors.map((s) => declaringBlocks(s, 'transform'));

check(
  '操作栏按钮按下时微缩，且只对可点的按钮（三个选择器都带 :not(:disabled)）',
  pressBlocks.every((b) => b.length === 1) &&
    pressBlocks.every((b) => {
      const v = scaleOf(b[0][1]);
      return v !== null && v < 1;
    }),
  pressSelectors.map((s, i) => `${s}: ${pressBlocks[i].length} 处`).join('；') +
    '——这排按钮带描边，只缩图标读不出反馈；禁用态不该缩',
);

// 后写的同名规则会把 transform 覆盖掉，按下反馈在真机上完全死掉。
for (const selector of ['.icon-btn:not(:disabled):active', '.nav-btn:active svg']) {
  check(
    `${selector} 的 transform 没有被后写的规则覆盖`,
    !overriddenLater(selector, 'transform'),
    '调试残留或后续特性再写一条同样的选择器就会盖掉它',
  );
}

/**
 * 全局兜底必须真的在某个 reduced-motion 块内。
 *
 * 此前这条检查用 `[\s\S]*?` 从第一个媒体头往后惰性匹配，只要那段文字出现在它
 * 之后的任意位置就通过——完全不核对它在不在块内。实测把兜底所在的媒体条件改成
 * forced-colors 后正则仍匹配。所以改成先切出每个 reduced-motion 块，再在块内找。
 */
const reduceBlocks = (() => {
  const blocks = [];
  const re = /@media[^{]*prefers-reduced-motion:\s*reduce[^{]*\{/g;
  for (let m = re.exec(cleanCss); m; m = re.exec(cleanCss)) {
    const open = m.index + m[0].length - 1;
    let depth = 0;
    for (let i = open; i < cleanCss.length; i++) {
      if (cleanCss[i] === '{') { depth += 1; }
      if (cleanCss[i] === '}') {
        depth -= 1;
        if (depth === 0) { blocks.push(cleanCss.slice(open + 1, i)); break; }
      }
    }
  }
  return blocks;
})();

check(
  `找到 ${reduceBlocks.length} 个 reduced-motion 块`,
  reduceBlocks.length > 0,
  '一个都没有说明减少动效完全没适配',
);

// 声明换序或分行写都是等价 CSS，因此分别找两条，不逐字匹配整行。
check(
  '某个 reduced-motion 块里有全局关掉动画与过渡的兜底',
  reduceBlocks.some((b) => {
    const star = /(^|[\s;}])\*\s*\{([^}]*)\}/.exec(b);
    if (!star) { return false; }
    return /animation:\s*none\s*!important/.test(star[2]) &&
      /transition:\s*none\s*!important/.test(star[2]);
  }),
  '这条被删掉或挪出 reduced-motion 块后，两个动画在减少动效下照样放',
);

/* ---- 点不动的按钮：抖一下 ---- */

console.log('');
console.log('检查「点了点不动」的抖动：');

checkOneShot('抖动', '.is-refusing', 'refuse-shake', 400);

const refuseRule = rule('.is-refusing');
check(
  '.is-refusing 只带 animation，无静态样式',
  refuseRule.trim() !== '' && props(refuseRule).every((p) => p.startsWith('animation')),
  `实际属性：${props(refuseRule).join('、')}`,
);

const refuseKf = keyframes('refuse-shake');

check(
  '抖动只动 transform（参考视频里不缩放、不变色）',
  props(refuseKf).length > 0 && props(refuseKf).every((p) => p === 'transform'),
  `实际属性：${props(refuseKf).join('、')}——实测弦宽变化 <2%、颜色恒定`,
);

check(
  '只动横轴（实测竖向偏移在 ±0.5px 噪声内）',
  /translateX\(/.test(refuseKf) &&
    !/translateY|translate\(\s*[^)]*,/.test(refuseKf) &&
    !/scale|rotate/.test(refuseKf),
  refuseKf.trim(),
);

// 参考视频量出来的形状：6 个半程、首程向左、振幅递减。
// px 写成可选：0 位通常写 translateX(0) 而不是 translateX(0px)，
// 要求带单位会把首末两帧漏掉，「起点终点都在原位」那条于是永远读不到它们。
const swings = [...refuseKf.matchAll(/([\d.]+)%\s*\{\s*transform:\s*translateX\((-?[\d.]+)(?:px)?\)/g)]
  .map((m) => ({ at: parseFloat(m[1]), px: parseFloat(m[2]) }))
  .sort((a, b) => a.at - b.at);

// 首末的 0 位不算半程。
const moves = swings.filter((s) => s.px !== 0);

check(
  `有 6 个半程（实测参考视频里方向反转 5 次，实际 ${moves.length} 个）`,
  moves.length === 6,
  moves.map((s) => `${s.at}%:${s.px}px`).join(' '),
);

check(
  '起点与终点都在原位（否则按钮会永久偏移）',
  swings.length >= 2 && swings[0].at === 0 && swings[0].px === 0 &&
    swings[swings.length - 1].at === 100 && swings[swings.length - 1].px === 0,
  swings.map((s) => `${s.at}%:${s.px}`).join(' '),
);

check(
  '首个半程向左（参考视频两次事件的首程都是向左）',
  moves.length > 0 && moves[0].px < 0,
  moves.length > 0 ? `首程 ${moves[0].px}px` : '没有半程',
);

check(
  '方向逐程交替（同向两程连着就不是抖动了）',
  moves.length > 1 && moves.every((s, i) => i === 0 || Math.sign(s.px) !== Math.sign(moves[i - 1].px)),
  moves.map((s) => s.px).join(' '),
);

// 振幅递减：参考视频归一化后是 1.0、0.7、0.6、0.6、0.3、0.3——
// 允许相等（60fps 下每半程仅约 1.8 帧，包络精度约 ±0.1），但不许回升。
const amps = moves.map((s) => Math.abs(s.px));
check(
  `振幅不回升（${amps.join(' → ')}px）`,
  amps.every((a, i) => i === 0 || a <= amps[i - 1] + 0.01),
  '参考视频的包络是递减的：1.0、0.7、0.6、0.6、0.3、0.3',
);

check(
  `峰值在首程（${amps[0]}px）`,
  amps.length > 0 && amps[0] === Math.max(...amps),
  amps.join(' '),
);

// 半程间隔要匀。实测周期约 62ms、时长 184ms，即每程约 17%。
const gaps = moves.slice(1).map((s, i) => s.at - moves[i].at);
check(
  `半程间隔均匀（${gaps.join(' / ')}%，实测参考视频半程约 31ms ≈ 17%）`,
  gaps.length > 0 && Math.max(...gaps) - Math.min(...gaps) <= 4,
  '间隔忽长忽短会读成卡顿而不是抖动',
);

/* ---- 批量测试：正在测的那一行有一道扫光 ---- */

console.log('');
console.log('检查批量测试的扫光：');

const pickerJs = readFileSync(join(webDir, 'scripts', 'picker.js'), 'utf8');
const favoritesJs = readFileSync(join(webDir, 'scripts', 'model-favorites.js'), 'utf8');

const sweepLayer = rule('.picker-item.is-testing::after');
const sweepHost = rule('.picker-item.is-testing');
const sweepKf = keyframes('model-test-sweep');

check(
  '有 .picker-item.is-testing::after 这一层',
  sweepLayer.trim() !== '',
  '扫光靠伪元素铺一层，行本身不动',
);

const sweepAnim = animation(sweepLayer);
check(
  '扫光引用的关键帧存在（model-test-sweep）',
  sweepAnim !== null && sweepAnim.name === 'model-test-sweep' && sweepKf.trim() !== '',
  sweepAnim === null ? '没有 animation 声明' : `解析到名字 ${sweepAnim.name}`,
);

// 这一条与前面几个动画相反：扫光必须循环。测一个模型要一两秒，
// 只放一次的话光扫过去就没了，而那一行还在测。
check(
  `扫光是循环的（重复 ${sweepAnim?.iterations} 次）`,
  sweepAnim !== null && sweepAnim.iterations === Infinity,
  '只放一次的话光扫过去就没了，而那一行还在测',
);

check(
  `扫光周期在 0.6–2s 之间（${sweepAnim?.ms}ms）`,
  sweepAnim !== null && sweepAnim.ms !== null && sweepAnim.ms >= 600 && sweepAnim.ms <= 2000,
  '再快显得慌；再慢则一个模型测完了光还没扫过一遍，看不出它在动',
);

check(
  '扫光只动 transform（宽度或 left 变化会触发布局）',
  props(sweepKf).length > 0 && props(sweepKf).every((p) => p === 'transform'),
  `实际属性：${props(sweepKf).join('、')}`,
);

// 方向：从左到右。起始帧必须是负位移、结束帧正位移，反了就是从右往左扫。
const sweepFrom = /from\s*\{[^}]*translateX\((-?[\d.]+)%\)/.exec(sweepKf);
const sweepTo = /to\s*\{[^}]*translateX\((-?[\d.]+)%\)/.exec(sweepKf);
check(
  '从左扫到右（起始为负位移、结束为正位移）',
  sweepFrom !== null && sweepTo !== null &&
    parseFloat(sweepFrom[1]) < 0 && parseFloat(sweepTo[1]) > 0,
  sweepFrom && sweepTo ? `${sweepFrom[1]}% → ${sweepTo[1]}%` : sweepKf.trim(),
);

check(
  '两端各移出整行宽度（否则高光在行内凭空出现又消失）',
  sweepFrom !== null && sweepTo !== null &&
    Math.abs(parseFloat(sweepFrom[1])) >= 100 && Math.abs(parseFloat(sweepTo[1])) >= 100,
  sweepFrom && sweepTo ? `${sweepFrom[1]}% → ${sweepTo[1]}%` : '',
);

/**
 * 取出 linear-gradient(...) 的参数列表，按括号配平。
 *
 * 不用 [^)]* 之类的正则：渐变里有 var(--sweep)，它自带一对括号，
 * 「到第一个右括号为止」会在那里断掉，于是一条完全正确的渐变被判成不匹配。
 */
function gradientArgs(text) {
  const at = text.indexOf('linear-gradient(');
  if (at === -1) { return null; }
  const open = text.indexOf('(', at);
  let depth = 0;
  for (let i = open; i < text.length; i++) {
    if (text[i] === '(') { depth += 1; }
    if (text[i] === ')') {
      depth -= 1;
      if (depth === 0) { return text.slice(open + 1, i); }
    }
  }
  return null;
}

// 渐变两端必须透明：不透明的话整层就是一块移动的色块，不是一道光。
const gradient = gradientArgs(sweepLayer);
const stops = gradient
  // 顶层逗号切分：var(...) 里没有逗号，所以直接切是安全的；
  // 万一以后有了，这里会切出多余的项，断言随之变红而不是静默放过。
  ? gradient.split(',').map((s) => s.trim())
  : [];

check(
  `渐变沿横向且两端透明（${stops.length} 段：${stops.join(' | ')}）`,
  stops.length >= 3 && stops[0] === '90deg' &&
    stops[1] === 'transparent' && stops[stops.length - 1] === 'transparent',
  sweepLayer.trim(),
);

check(
  '高光色走调色板变量，且两套主题各定义一份',
  /var\(--sweep\)/.test(sweepLayer) &&
    (cleanCss.match(/--sweep\s*:/g) ?? []).length === 2,
  `--sweep 定义了 ${(cleanCss.match(/--sweep\s*:/g) ?? []).length} 处，应为两套调色板各一份`,
);

check(
  '这一层不吃点击（否则挡住选中那一行）',
  /pointer-events:\s*none/.test(sweepLayer),
  '',
);

// 定位与裁剪只加在 .is-testing 上：.picker-item 那条规则管着几十行。
check(
  '定位与裁剪加在 .is-testing 上，不动 .picker-item 本身',
  /position:\s*relative/.test(sweepHost) && /overflow:\s*hidden/.test(sweepHost) &&
    !/overflow:\s*hidden/.test(rule('.picker-item')),
  '改 .picker-item 的定位与裁剪会波及整列',
);

// 减少动效：扫光停下后要留一个看得见的静态标记，否则「正在测哪一个」就没了。
check(
  '减少动效时那一行仍有静态高光',
  reduceBlocks.some((b) =>
    /\.picker-item\.is-testing::after\s*\{[^}]*background:\s*var\(--sweep\)/.test(b)),
  '动画一关，光带静止成一道居中软光带，读不出「被标记」；改成整块高光',
);

console.log('');
console.log('检查扫光的接线：');

check(
  '批量测试的「正在测这一个」由 model-favorites 判定（大小写折叠在那里）',
  /export function isBulkTesting/.test(favoritesJs) &&
    /fold\(current\)\s*===\s*fold\(id\)/.test(favoritesJs),
  '调用方直接比字符串，会在网关回报的 ID 大小写与目录不一致时静默失配',
);

check(
  'picker.js 导入并用它给行加 is-testing',
  /isBulkTesting/.test(pickerJs) &&
    /classList\.add\('is-testing'\)/.test(pickerJs),
  '',
);

// is-testing 与 is-probing 是两回事，混用会让批量期间一行都标不上。
check(
  '标记取自批量进度，不是 isProbing',
  /const testing = isBulkTesting\(id\)/.test(pickerJs),
  '批量整批只占一次闸门、不逐个置 probing，所以批量期间没有任何一行会是 probing',
);

console.log('');
console.log('检查抖动的接线（禁用按钮不派发点击事件）：');

check(
  '监听装在容器上而不是按钮上',
  /addEventListener\('pointerdown'/.test(motionJs) &&
    /initRefusalShake/.test(motionJs),
  '绑在禁用按钮上的事件一次也不会来——那正是「点了没反应」的根源',
);

check(
  '用 pointerdown 而不是 click',
  /addEventListener\('pointerdown'[\s\S]{0,600}elementFromPoint/.test(motionJs) &&
    !/addEventListener\('click'[\s\S]{0,200}elementFromPoint/.test(motionJs),
  'click 在禁用按钮上根本不会产生，连它的祖先也收不到',
);

check(
  '用捕获阶段（禁用元素不冒泡，且不能被别处 stopPropagation 掉）',
  /addEventListener\('pointerdown',[\s\S]{0,800}\},\s*true\)/.test(motionJs),
  '',
);

check(
  '靠 elementFromPoint 判断点在了哪',
  /document\.elementFromPoint\(/.test(motionJs),
  '事件从禁用按钮身上不冒泡，但命中测试照常命中它',
);

check(
  '同时认 disabled 与 aria-disabled',
  /button:disabled/.test(motionJs) && /aria-disabled/.test(motionJs),
  'aria-disabled 是「看得见、点不动」的另一种写法，反馈同样该给',
);

check(
  '加类前先问减少动效',
  /playRefusal[\s\S]{0,300}prefersReducedMotion\(\)/.test(motionJs),
  '',
);

// 连点重放：先摘、读一次布局、再挂。
const refuseFn = (() => {
  const at = motionJs.indexOf('export function playRefusal');
  if (at === -1) { return ''; }
  const end = motionJs.indexOf('\n}', at);
  return end === -1 ? '' : motionJs.slice(at, end);
})();

const rRemove = refuseFn.indexOf("classList.remove('is-refusing')");
const rReflow = refuseFn.indexOf('offsetWidth');
const rAdd = refuseFn.indexOf("classList.add('is-refusing')");

check(
  '连点能重放（先摘、读一次布局、再挂）',
  rRemove !== -1 && rReflow !== -1 && rAdd !== -1 && rRemove < rReflow && rReflow < rAdd,
  `remove@${rRemove} reflow@${rReflow} add@${rAdd}`,
);

// 这一条锁住真实鼠标实测抓到的那个缺陷：重放时摘类会让运行中的动画被取消，
// 而 animationcancel 是异步派发的——等它到达时新动画已在跑，若无条件摘类
// 就会把刚加上的类又摘掉，第二下点击于是毫无反馈。
check(
  '清理前先确认此刻没有抖动在跑（否则重放会被异步到达的 cancel 摘掉）',
  /getAnimations\(\)[\s\S]{0,200}refuse-shake/.test(motionJs) &&
    /if\s*\(!running\)/.test(motionJs),
  '真实鼠标连点两下、只抖一次，就是这个原因',
);

check(
  '禁用的按钮不再走按下微缩（缩一下等于说点成了）',
  /\.icon-btn:not\(:disabled\):active/.test(cleanCss),
  '',
);

console.log('');
console.log(`=== 对话动效：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
