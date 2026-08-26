// 主题适配的静态检查。
//
// 存在的理由：漏一处适配不会报错、不会让任何功能失效，只会在深色主题下
// 出现一块白底、一行看不见的字或一个刺眼的色块。发现途径只有「切到深色，
// 逐屏逐状态地看」——而错误提示条、降级警告、已撤销的卡片这类状态本来就
// 难复现，肉眼过一遍根本盖不全。所以把规矩锁在这里：
//
//   一、样式规则里不许出现颜色字面量，一律走调色板变量。
//       多一个字面量就多一处只在浅色下成立的地方。
//   二、两套调色板的变量名必须完全一致。
//       在一套里加了变量、另一套忘了加，var() 取不到值，
//       该处会退化成浏览器默认色（通常是黑字白底）。
//   三、两套都必须声明 color-scheme。
//       原生部件（滚动条、<select> 下拉、数字输入框的步进器）不受 CSS 变量
//       管辖，只认这一个属性。不写的话深色主题里设置页的下拉框仍是白底黑字。
//   四、主题脚本必须在 <head> 里同步加载。
//       改成模块或挪到页尾都会晚于首屏，深色下开面板先闪一下白。
//
// 运行：node tests/web/theme.test.mjs

import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const webDir = join(here, '..', '..', 'src', 'web');
const css = readFileSync(join(webDir, 'styles', 'app.css'), 'utf8');
const html = readFileSync(join(webDir, 'index.html'), 'utf8');
const themeJs = readFileSync(join(webDir, 'scripts', 'theme.js'), 'utf8');

let passed = 0;
let failed = 0;

function check(label, condition, detail = '') {
  if (condition) {
    passed += 1;
    console.log(`  通过  ${label}`);
  } else {
    failed += 1;
    console.log(`  失败  ${label}`);
    if (detail) { console.log(`        ${detail}`); }
  }
}

/** 去掉块注释，避免注释里举例用的色值被当成实际用色。 */
const stripComments = (text) => text.replace(/\/\*[\s\S]*?\*\//g, '');

/**
 * 取出某个选择器下声明块的正文。
 * 调色板里没有嵌套，扫到第一个右花括号即可。
 */
function block(source, selector) {
  const start = source.indexOf(selector);
  if (start === -1) { return ''; }
  const open = source.indexOf('{', start);
  const close = source.indexOf('}', open);
  if (open === -1 || close === -1) { return ''; }
  return source.slice(open + 1, close);
}

/** 收集声明块里定义的自定义属性名。 */
function variables(text) {
  const names = new Set();
  for (const m of text.matchAll(/(--[\w-]+)\s*:/g)) {
    names.add(m[1]);
  }
  return names;
}

const cleanCss = stripComments(css);

const lightBlock = block(cleanCss, ':root,');
const darkBlock = block(cleanCss, ":root[data-theme='dark']");

console.log('检查 app.css 的两套调色板：');
console.log('');

check('浅色调色板存在', lightBlock.trim() !== '', '没找到 :root 的声明块');
check('深色调色板存在', darkBlock.trim() !== '', "没找到 :root[data-theme='dark'] 的声明块");

const lightVars = variables(lightBlock);
const darkVars = variables(darkBlock);

// --radius 是尺寸不是颜色，深色不必重复定义（继承浅色那份即可）。
const sizeOnly = new Set(['--radius']);
const expected = new Set([...lightVars].filter((name) => !sizeOnly.has(name)));

const missingInDark = [...expected].filter((name) => !darkVars.has(name));
const extraInDark = [...darkVars].filter((name) => !lightVars.has(name));

check(
  `深色补齐了全部 ${expected.size} 个变量`,
  missingInDark.length === 0,
  missingInDark.length > 0 ? `深色缺少：${missingInDark.join('、')}` : '',
);

check(
  '深色没有多出浅色没有的变量',
  extraInDark.length === 0,
  extraInDark.length > 0 ? `浅色缺少：${extraInDark.join('、')}` : '',
);

// 两套都要声明 color-scheme，否则原生部件不跟着主题走。
check('浅色声明了 color-scheme: light', /color-scheme:\s*light/.test(lightBlock));
check('深色声明了 color-scheme: dark', /color-scheme:\s*dark/.test(darkBlock));

// 调色板之外不许出现颜色字面量。
const paletteRanges = [];
for (const selector of [':root,', ":root[data-theme='dark']"]) {
  const start = cleanCss.indexOf(selector);
  if (start === -1) { continue; }
  const close = cleanCss.indexOf('}', cleanCss.indexOf('{', start));
  paletteRanges.push([start, close]);
}

const inPalette = (index) => paletteRanges.some(([start, end]) => index >= start && index <= end);

const literals = [];
// transparent、currentColor、none 是关键字而非具体颜色，两套主题下都成立。
const colorPattern = /#[0-9a-fA-F]{3,8}\b|\brgba?\(|\bhsla?\(/g;
for (const m of cleanCss.matchAll(colorPattern)) {
  if (inPalette(m.index)) { continue; }
  // 报出所在行，便于直接定位。
  const line = cleanCss.slice(0, m.index).split('\n').length;
  literals.push(`第 ${line} 行附近的 ${m[0]}`);
}

check(
  '调色板之外没有颜色字面量',
  literals.length === 0,
  literals.length > 0
    ? `共 ${literals.length} 处：${literals.slice(0, 8).join('、')}${literals.length > 8 ? ' …' : ''}`
    : '',
);

console.log('');
console.log('检查主题的加载方式与切换按钮：');
console.log('');

// 主题脚本必须同步、且排在 body 之前。
const headEnd = html.indexOf('</head>');
const themeTag = html.match(/<script[^>]*src="scripts\/theme\.js"[^>]*>/);

check('index.html 引入了 theme.js', Boolean(themeTag));
check(
  'theme.js 在 </head> 之前加载',
  Boolean(themeTag) && themeTag.index < headEnd,
  '挪到 body 里就晚于首屏，深色下会闪白',
);
check(
  'theme.js 不带 type="module" 或 defer',
  Boolean(themeTag) && !/\b(defer|type="module")/.test(themeTag[0]),
  `当前标签：${themeTag?.[0] ?? '<无>'}——模块与 defer 都会延迟到文档解析完才执行`,
);

// 切换按钮：不能带 data-route，否则会被 app.js 当成页签。
const toggle = html.match(/<button[^>]*id="theme-toggle"[\s\S]*?<\/button>/);
check('存在主题切换按钮', Boolean(toggle));
check(
  '切换按钮不带 data-route',
  Boolean(toggle) && !/data-route/.test(toggle[0]),
  '带了就会被当成第三个页签，点一下还会跳路由',
);
check(
  '切换按钮带 title 与 aria-label',
  Boolean(toggle) && /title="/.test(toggle[0]) && /aria-label="/.test(toggle[0]),
  '纯图标按钮的悬停说明是它唯一的自解释途径',
);
check(
  '切换按钮同时含太阳与月亮两个图标',
  Boolean(toggle) &&
    /theme-glyph-sun/.test(toggle[0]) &&
    /theme-glyph-moon/.test(toggle[0]),
  '两个图标都要在 DOM 里，由 CSS 按当前主题显示其一',
);
check(
  'app.css 定义了两个图标的显隐',
  /\.theme-glyph-sun[\s\S]{0,200}display:\s*none/.test(css) &&
    /\[data-theme='dark'\][\s\S]{0,120}\.theme-glyph-sun[\s\S]{0,80}display:\s*block/.test(css),
  '缺少按 data-theme 切换图标的规则，会两个图标同时显示',
);

// app.js 按 [data-route] 筛页签，漏掉这个限定就会把切换按钮当页签。
const appJs = readFileSync(join(webDir, 'scripts', 'app.js'), 'utf8');
const bareNavQuery = appJs.match(/querySelectorAll\('\.nav-btn'\)/g);
check(
  'app.js 只按 .nav-btn[data-route] 选页签',
  bareNavQuery === null,
  '还有按 .nav-btn 全选的地方，主题按钮会被当成页签',
);

// 跟随系统只在用户没手动选过时生效。
check(
  'theme.js 手动选过之后不再跟随系统',
  /if\s*\(\s*!stored\(\)\s*\)/.test(themeJs),
  '缺少这层判断，用户选的浅色会被系统的夜间模式覆盖掉',
);

console.log('');
console.log('检查文字对比度（WCAG AA 要求正文 4.5:1）：');
console.log('');

/** #rrggbb / #rgb 转 [r,g,b]。调色板里只有这两种写法。 */
function parseHex(value) {
  const text = (value ?? '').trim();
  const long = text.match(/^#([0-9a-fA-F]{6})$/);
  if (long) {
    return [0, 2, 4].map((i) => parseInt(long[1].slice(i, i + 2), 16));
  }
  const short = text.match(/^#([0-9a-fA-F]{3})$/);
  if (short) {
    return [...short[1]].map((c) => parseInt(c + c, 16));
  }
  return null;
}

/** WCAG 相对亮度。 */
function luminance([r, g, b]) {
  const channel = (raw) => {
    const v = raw / 255;
    return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  };
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}

function contrast(fg, bg) {
  const a = luminance(fg);
  const b = luminance(bg);
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

/** 取调色板里的变量值。深色没定义的（如 --radius）落回浅色那份。 */
function declarations(text) {
  const map = new Map();
  for (const m of text.matchAll(/(--[\w-]+)\s*:\s*([^;]+);/g)) {
    map.set(m[1], m[2].trim());
  }
  return map;
}

const lightDecls = declarations(lightBlock);
const darkDecls = declarations(darkBlock);

/*
  必须能读的文字组合。深色调色板是新加的，其中的绿、琥珀、红都必须重新定值——
  浅色那几个色号直接搬到深底上全都不够亮（主色 #107c41 只有 2.6:1）。
  这张表把「够不够亮」变成可以跑的断言，而不是靠改完之后眯着眼睛看。
*/
const textPairs = [
  ['正文', '--text', '--bg'],
  ['正文（次级面）', '--text', '--bg-subtle'],
  ['弱化文字', '--text-muted', '--bg'],
  ['弱化文字（次级面）', '--text-muted', '--bg-subtle'],
  ['主色文字', '--accent', '--bg'],
  ['来源标记', '--accent-text', '--accent-bg'],
  ['实心按钮上的字', '--text-on-solid', '--accent-solid'],
  ['错误文字', '--error', '--bg'],
  ['错误提示条', '--error-text', '--error-bg'],
  ['警告状态', '--warn-fg', '--bg'],
  ['警告提示条', '--warn-text', '--warn-bg'],
  ['代码块', '--text', '--bg-code'],
  ['行内代码', '--text', '--bg-inline-code'],
  ['思考区', '--text-muted', '--bg-thinking'],
  ['用户气泡', '--text', '--bubble-user-bg'],
  ['排队条', '--text-muted', '--queue-bg'],
];

for (const [themeName, decls] of [['浅色', lightDecls], ['深色', darkDecls]]) {
  const failures = [];
  let lowest = Infinity;
  let lowestLabel = '';

  for (const [label, fgName, bgName] of textPairs) {
    const fg = parseHex(decls.get(fgName) ?? lightDecls.get(fgName));
    const bg = parseHex(decls.get(bgName) ?? lightDecls.get(bgName));

    if (!fg || !bg) {
      failures.push(`${label}：取不到色值（${fgName} 或 ${bgName}）`);
      continue;
    }

    const ratio = contrast(fg, bg);
    if (ratio < lowest) {
      lowest = ratio;
      lowestLabel = label;
    }
    if (ratio < 4.5) {
      failures.push(`${label} ${ratio.toFixed(2)}:1`);
    }
  }

  check(
    `${themeName}：${textPairs.length} 组文字全部达到 4.5:1`,
    failures.length === 0,
    failures.length > 0 ? `未达标：${failures.join('、')}` : '',
  );
  console.log(`        最低的是「${lowestLabel}」${lowest.toFixed(2)}:1`);
}

console.log('');
console.log(`=== 主题检查：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
