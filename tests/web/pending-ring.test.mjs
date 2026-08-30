// 处理中指示器：一圈点 + 绕圈的亮度波。
//
// 这个文件盯的是几处「不会报错、只会画错」的地方：
//
//   一、点数与 CSS 里定位规则的条数必须一致。多出来的点没有 transform，
//       会全堆在圆圈顶端同一个位置——而那时节点数、class、规则统统正常。
//   二、八个角度必须互不相同且均分一周。少一条 nth-child 规则就有两个点重合，
//       圈上留一个缺口，看着像画坏了。
//   三、transform-origin 的纵坐标必须等于容器半高，否则旋转中心不在圆心，
//       点会排成一个偏心的椭圆。
//   四、相位差必须是周期的 1/8 且为负值。取正值时首屏有一段全暗的空窗，
//       而这个指示器常常只显示一两秒。
//   五、暗态不能是 0：全暗的点在圈上留缺口，缺口绕圈转比波本身更抓眼。
//   六、减少动效时要恢复成看得见的实心圈——动画一关，点会停在暗态上。
//
// 几何是算出来的，不是量出来的：本环境的 Read 工具显示不了图片，
// 而 CSS 里这几个值之间的关系可以纯算术地核对。

import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const webDir = join(here, '..', '..', 'src', 'web');
const css = readFileSync(join(webDir, 'styles', 'app.css'), 'utf8');
const chatJs = readFileSync(join(webDir, 'scripts', 'chat.js'), 'utf8');

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

/** 取一条规则的声明块。 */
function rule(selector) {
  const needle = `\n${selector} {`;
  const start = css.indexOf(needle);
  if (start === -1) { return ''; }
  const open = css.indexOf('{', start);
  const close = css.indexOf('}', open);
  return css.slice(open + 1, close);
}

function num(text, prop) {
  const m = new RegExp(`${prop}:\\s*(-?[\\d.]+)px`).exec(text);
  return m ? parseFloat(m[1]) : null;
}

console.log('检查点数与定位规则的一致性：');

const declared = /const PENDING_DOT_COUNT = (\d+)/.exec(chatJs);
check('chat.js 用常量声明点数', declared !== null, '写死数字时两处会各自漂移');

const dotCount = declared ? parseInt(declared[1], 10) : -1;

check(
  'chat.js 建点时用的是这个常量',
  /i < PENDING_DOT_COUNT/.test(chatJs),
  '循环上界仍是字面量',
);

// 收集所有 nth-child 定位规则
const nth = [...css.matchAll(
  /\.pending-dots i:nth-child\((\d+)\)\s*\{([^}]*)\}/g,
)].map(([, idx, body]) => ({
  index: parseInt(idx, 10),
  angle: (() => {
    const m = /rotate\((-?[\d.]+)deg\)/.exec(body);
    return m ? parseFloat(m[1]) : null;
  })(),
  delay: (() => {
    const m = /animation-delay:\s*(-?[\d.]+)s/.exec(body);
    return m ? parseFloat(m[1]) : null;
  })(),
}));

check(
  `定位规则条数与点数一致（${nth.length} 条 / ${dotCount} 个点）`,
  nth.length === dotCount,
  '多出来的点没有 transform，会全堆在圆圈顶端同一处',
);

check(
  'nth-child 的序号是 1..N 连续无缺',
  nth.length > 0 &&
    nth.map((r) => r.index).sort((a, b) => a - b).every((v, i) => v === i + 1),
  nth.map((r) => r.index).join(','),
);

check(
  '每条规则都给了角度',
  nth.length > 0 && nth.every((r) => r.angle !== null),
  nth.filter((r) => r.angle === null).map((r) => r.index).join(','),
);

const angles = nth.map((r) => r.angle);
check(
  '八个角度互不相同（重合的点会在圈上留缺口）',
  new Set(angles).size === angles.length,
  angles.join(','),
);

const sorted = [...angles].sort((a, b) => a - b);
const step = dotCount > 0 ? 360 / dotCount : 0;
check(
  `角度均分一周（每 ${step}°）`,
  sorted.every((v, i) => Math.abs(v - i * step) < 0.01),
  sorted.join(','),
);

console.log('');
console.log('检查圆周几何：');

const box = rule('.pending-dots');
const dot = rule('.pending-dots i');

const boxW = num(box, 'width');
const boxH = num(box, 'height');
const dotW = num(dot, 'width');
const originY = (() => {
  const m = /transform-origin:\s*50%\s+([\d.]+)px/.exec(dot);
  return m ? parseFloat(m[1]) : null;
})();
const marginLeft = num(dot, 'margin-left');

check('容器是正方形', boxW !== null && boxW === boxH, `${boxW}x${boxH}`);
check(
  '点用 margin-left 抵掉自身一半宽度（否则圆心偏右半个点）',
  dotW !== null && marginLeft === -dotW / 2,
  `dotW=${dotW} margin-left=${marginLeft}`,
);
check(
  'transform-origin 的纵坐标等于容器半高（旋转中心即圆心）',
  originY !== null && boxH !== null && Math.abs(originY - boxH / 2) < 0.01,
  `origin=${originY} 半高=${boxH / 2}`,
);

// 点定位在 top:0，中心在 y = dotW/2；圆心在 y = originY。
const radius = originY !== null && dotW !== null ? originY - dotW / 2 : null;
check(
  '半径为正（否则点会落到圆心另一侧）',
  radius !== null && radius > 0,
  `radius=${radius}`,
);
check(
  '点不会越出容器边界',
  radius !== null && dotW !== null && boxH !== null &&
    boxH / 2 + radius + dotW / 2 <= boxH + 0.01,
  `最外沿=${boxH / 2 + radius + dotW / 2} 容器=${boxH}`,
);

// 相邻点圆心间距要大于点径，否则糊成一条实线。
const spacing = radius !== null
  ? 2 * radius * Math.sin(Math.PI / dotCount)
  : null;
check(
  `相邻点留得下间隙（间距 ${spacing ? spacing.toFixed(2) : '?'}px > 点径 ${dotW}px）`,
  spacing !== null && dotW !== null && spacing > dotW,
  '间距小于点径时整圈会糊成实线',
);

console.log('');
console.log('检查相位与波形：');

const period = (() => {
  const m = /animation:\s*pending-orbit\s+([\d.]+)s/.exec(dot);
  return m ? parseFloat(m[1]) : null;
})();

check('周期取视频实测的 0.94s', period === 0.94, String(period));

const delays = nth.slice().sort((a, b) => a.index - b.index).map((r) => r.delay);
check('每条规则都给了相位', delays.every((d) => d !== null), delays.join(','));

check(
  '首个点相位为 0',
  delays[0] === 0,
  String(delays[0]),
);

check(
  '其余相位都是负值（正值会让首屏先黑一段）',
  delays.slice(1).every((d) => d < 0),
  delays.join(','),
);

const expectedStep = period !== null && dotCount > 0 ? period / dotCount : null;
check(
  `相邻相位差是周期的 1/${dotCount}（${expectedStep ? expectedStep.toFixed(4) : '?'}s）`,
  expectedStep !== null &&
    delays.every((d, i) => Math.abs(Math.abs(d) - i * expectedStep) < 0.0005),
  delays.join(','),
);

// 相位单调递增（绝对值），保证波是连续绕圈而不是乱跳。
check(
  '相位沿角度顺序单调推进（否则亮点乱跳，不成一道波）',
  delays.every((d, i) => i === 0 || Math.abs(d) > Math.abs(delays[i - 1])),
  delays.join(','),
);

const kf = (() => {
  const start = css.indexOf('@keyframes pending-orbit');
  if (start === -1) { return ''; }
  const open = css.indexOf('{', start);
  let depth = 0;
  for (let i = open; i < css.length; i++) {
    if (css[i] === '{') { depth += 1; }
    if (css[i] === '}') {
      depth -= 1;
      if (depth === 0) { return css.slice(open + 1, i); }
    }
  }
  return '';
})();

check('有 pending-orbit 这段关键帧', kf.trim() !== '', '');

const dim = (() => {
  const vals = [...kf.matchAll(/opacity:\s*([\d.]+)/g)].map((m) => parseFloat(m[1]));
  return vals.length ? Math.min(...vals) : null;
})();
const bright = (() => {
  const vals = [...kf.matchAll(/opacity:\s*([\d.]+)/g)].map((m) => parseFloat(m[1]));
  return vals.length ? Math.max(...vals) : null;
})();

check('亮态是全不透明', bright === 1, String(bright));
check(
  '暗态不是 0（全暗的点会在圈上留一个绕圈转的缺口）',
  dim !== null && dim > 0,
  String(dim),
);
check(
  '静态透明度与暗态一致（动画未起步时不该比暗态更亮或更暗）',
  (() => {
    const m = /opacity:\s*([\d.]+)/.exec(dot);
    return m !== null && dim !== null && Math.abs(parseFloat(m[1]) - dim) < 0.001;
  })(),
  `静态=${/opacity:\s*([\d.]+)/.exec(dot)?.[1]} 暗态=${dim}`,
);

// 衰减段要短于一整周期，否则所有点长期同亮，读不出波。
const fadeEnd = (() => {
  const m = /([\d.]+)%\s*\{\s*opacity:\s*[\d.]+\s*;?\s*\}/g;
  const stops = [...kf.matchAll(/([\d.]+)%/g)].map((x) => parseFloat(x[1]));
  return stops.length ? Math.max(...stops.filter((s) => s < 100)) : null;
})();
check(
  '衰减在一周期内结束（留出暗态，波才有头有尾）',
  fadeEnd !== null && fadeEnd > 0 && fadeEnd < 100,
  String(fadeEnd),
);

console.log('');
console.log('检查减少动效与主题：');

check(
  '减少动效时那圈点恢复成看得见的实心圈',
  /@media\s*\(prefers-reduced-motion: reduce\)[\s\S]*?\.pending-dots i,[\s\S]{0,120}opacity:\s*1/.test(css),
  '动画一关，点会停在暗态上，几乎看不见',
);

check(
  '选择器「正在确认」那个点仍有 pending-pulse 可用',
  /@keyframes pending-pulse/.test(css) &&
    /\.picker-availability-dot\.is-probing[\s\S]{0,200}pending-pulse/.test(css),
  '换指示器时若把这段关键帧删了，那个点会静默不动',
);

check(
  '点色走调色板变量',
  /\.pending-dots i\s*\{[^}]*background:\s*var\(--text-muted\)/.test(css),
  '',
);

console.log('');
console.log('检查兜底文案：');

check(
  '兜底文案已改为「正在忙着办…」',
  /function showPending\(label = '正在忙着办…'\)/.test(chatJs),
  '',
);

for (const [stage, label] of [
  ['思考', '正在思考…'],
  ['停止', '正在停止…'],
]) {
  check(
    `${stage}阶段仍用自己的文案（${label}）`,
    chatJs.includes(`'${label}'`),
    '阶段文案被兜底吞掉时，用户看不出现在在做什么',
  );
}

check(
  '调工具时仍按工具名组装文案',
  /showPending\(`正在\$\{toolLabel\(payload\.name\)\}…`\)/.test(chatJs),
  '',
);

console.log('');
console.log(`=== 处理中指示器：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
