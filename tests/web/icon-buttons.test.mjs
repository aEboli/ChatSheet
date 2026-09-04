// 纯图标按钮的自解释性检查。
//
// 存在的理由：页签与控件行的按钮都没有文字标签，悬停说明是它们唯一的
// 自解释途径。删掉一个 title 不会报错、不会让任何功能失效，只会让按钮
// 变成一个谁也认不出的图形——这类缺陷除了逐个悬停试，没有别的发现途径，
// 所以在这里静态锁住。
//
// 同时锁住两件容易在改图标时被漏掉的事：
//   一、页签里不能留文字节点，否则窄栏又会被标签挤开；
//   二、24 网格的图标必须挂 glyph-24，否则线宽仍按 16 网格算，
//       显示出来比邻居细一圈（成因见 app.css 里的同名规则）。
//
// 运行：node tests/web/icon-buttons.test.mjs

import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const webDir = join(here, '..', '..', 'src', 'web');
const html = readFileSync(join(webDir, 'index.html'), 'utf8');
const css = readFileSync(join(webDir, 'styles', 'app.css'), 'utf8');

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

/** 取出所有 <button …> 起始标签及其到 </button> 的内容。 */
function buttons() {
  const found = [];
  for (const m of html.matchAll(/<button\b([^>]*)>([\s\S]*?)<\/button>/g)) {
    found.push({ attrs: m[1], inner: m[2] });
  }
  return found;
}

const attr = (attrs, name) => attrs.match(new RegExp(`${name}="([^"]*)"`))?.[1];

/** 去掉注释、SVG 与嵌套标签后剩下的可见文字。 */
function visibleText(inner) {
  return inner
    .replace(/<!--[\s\S]*?-->/g, '')
    .replace(/<svg[\s\S]*?<\/svg>/g, '')
    .replace(/<[^>]+>/g, '')
    .trim();
}

const all = buttons();
console.log(`检查 index.html 里的 ${all.length} 个按钮：`);
console.log('');

// 一、页签：只有图标，且必须带 title 与 aria-label。
//     按 data-route 筛，不按 .nav-btn：栏上还有主题切换按钮，
//     它同为 .nav-btn 但不是页签（点了不换页），另有专门的检查，
//     见 theme.test.mjs。
const barButtons = all.filter((b) => b.attrs.includes('nav-btn'));
const navButtons = barButtons.filter((b) => b.attrs.includes('data-route'));
const routes = navButtons.map((b) => attr(b.attrs, 'data-route'));
check('页签只有对话与设置两个', navButtons.length === 2, `找到 ${navButtons.length} 个：${routes.join('、')}`);
check('诊断没有页签', !routes.includes('diagnostics'), '诊断又出现在栏上了');

// 主题切换按钮与页签同属应用栏，纯图标，同样必须能悬停读懂。
const themeToggle = barButtons.filter((b) => !b.attrs.includes('data-route'));
check('应用栏上只有主题切换一个非页签按钮', themeToggle.length === 1,
  `找到 ${themeToggle.length} 个`);

// 诊断视图与路由必须留着：功能区的「诊断」按钮会推路由过来，
// 视图被顺手删掉的话那个按钮就点了没反应。
check('诊断视图仍在', /data-view="diagnostics"/.test(html), '视图被删了，功能区按钮会失效');

for (const button of barButtons) {
  const route = attr(button.attrs, 'data-route') ?? '主题切换';
  const title = attr(button.attrs, 'title');
  const label = attr(button.attrs, 'aria-label');
  const text = visibleText(button.inner);

  check(`页签 ${route} 带悬停说明`, Boolean(title), 'title 缺失或为空');
  check(`页签 ${route} 带 aria-label`, Boolean(label), 'aria-label 缺失或为空');
  check(`页签 ${route} 不显示文字`, text === '', `残留文字：${text}`);
  check(`页签 ${route} 有图标`, button.inner.includes('<svg'), '没有 svg');
}

// 二、控件行的图标按钮：没有文字，所以同样必须有 title。
//     模型选择器（picker-trigger）自己显示模型名，不在此列。
const iconButtons = all.filter((b) => (
  /class="[^"]*\b(icon-btn|approval-icon|context-ring)\b/.test(b.attrs)
));

check('控件行图标按钮都找到了', iconButtons.length >= 4, `找到 ${iconButtons.length} 个`);

for (const button of iconButtons) {
  const id = attr(button.attrs, 'id') ?? '<无 id>';
  check(`图标按钮 ${id} 带悬停说明`, Boolean(attr(button.attrs, 'title')), 'title 缺失');
  check(`图标按钮 ${id} 带 aria-label`, Boolean(attr(button.attrs, 'aria-label')), 'aria-label 缺失');
}

// 二之二、授权芯片必须是真的 button。
//
// 它同时承担两件事：显示本轮放行了哪张表的哪一类，以及点一下收回。
// 做成 div 挂 click 的话，键盘到不了、读屏也不报它可操作——而「收回授权」
// 是这块界面上唯一能中途收紧权限的入口。这条只能在真实 HTML 上验：
// 面板单测的假 DOM 一律建 div，在那里断言标签等于自问自答。
const grantChip = all.find((b) => attr(b.attrs, 'id') === 'approval-grants');
check('授权芯片是 button 而不是 div', Boolean(grantChip),
  '在 index.html 的 <button> 里找不到 id=approval-grants');

// 三、24 网格的 viewBox 必须配 glyph-24，否则线宽按 16 网格算会偏细。
const mismatched = [];
for (const m of html.matchAll(/<svg\b([^>]*)>/g)) {
  const attrs = m[1];
  if (!attrs.includes('viewBox="0 0 24 24"')) { continue; }
  // 上下文圆环是自绘的 24 网格图形，线宽由 .context-ring 单独定义。
  if (attrs.includes('class="ring') || html.slice(m.index, m.index + 400).includes('ring-track')) {
    continue;
  }
  if (!attrs.includes('glyph-24')) {
    mismatched.push(attrs.trim().slice(0, 60));
  }
}

check(
  '24 网格的图标都挂了 glyph-24',
  mismatched.length === 0,
  mismatched.length > 0 ? `漏挂：${mismatched.join(' / ')}` : '',
);

check(
  'app.css 定义了 glyph-24 的线宽',
  /\.(nav-btn|icon-btn)\s+\.glyph-24[\s\S]{0,120}stroke-width:\s*2/.test(css),
  '缺少 glyph-24 的 stroke-width 规则',
);

// 四、页签收成方形后不能再有横向内边距，否则窄栏里三个页签会顶掉标题。
check(
  '页签是固定宽高的方块',
  /\.nav-btn\s*\{[^}]*width:\s*24px[^}]*height:\s*24px[^}]*\}/.test(css),
  '.nav-btn 没有固定宽高',
);

console.log('');
console.log(`=== 图标按钮检查：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
