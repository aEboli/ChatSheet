// Markdown 渲染器验证。
// 重点覆盖两类风险：HTML 注入（模型输出直接进 innerHTML）
// 与流式未闭合输入（代码块只开了 ``` 还没收尾）。
//
// 运行：node tests/web/markdown.test.mjs

import { renderMarkdown, escapeHtml } from '../../src/web/scripts/markdown.js';

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

// ---- 转义与注入 ----

check('转义尖括号', escapeHtml('<script>') === '&lt;script&gt;', escapeHtml('<script>'));
check('转义引号', escapeHtml(`"'`) === '&quot;&#39;', escapeHtml(`"'`));

const injection = renderMarkdown('<img src=x onerror=alert(1)>');
check(
  '正文中的 HTML 被转义',
  !injection.includes('<img') && injection.includes('&lt;img'),
  injection,
);

const codeInjection = renderMarkdown('```\n<script>alert(1)</script>\n```');
check(
  '代码块中的 HTML 被转义',
  !codeInjection.includes('<script>') && codeInjection.includes('&lt;script&gt;'),
  codeInjection,
);

const tableInjection = renderMarkdown('| 列 |\n| --- |\n| <b>x</b> |');
check(
  '表格单元格中的 HTML 被转义',
  !tableInjection.includes('<b>') && tableInjection.includes('&lt;b&gt;'),
  tableInjection,
);

// 链接不应产生可点击的 href，避免面板内跳转到外部地址。
const link = renderMarkdown('[点我](javascript:alert(1))');
check('链接不生成 href', !link.includes('href'), link);

// ---- 基本结构 ----

check('段落', renderMarkdown('普通文本') === '<p>普通文本</p>', renderMarkdown('普通文本'));

const heading = renderMarkdown('## 标题');
check('标题', heading.includes('标题') && heading.includes('<h4'), heading);

const bold = renderMarkdown('**粗体**与*斜体*');
check('粗体与斜体', bold.includes('<strong>粗体</strong>') && bold.includes('<em>斜体</em>'), bold);

const inlineCode = renderMarkdown('用 `A1:B2` 范围');
check('行内代码', inlineCode.includes('<code>A1:B2</code>'), inlineCode);

// 行内代码中的星号不应被当成强调语法。
const codeWithStars = renderMarkdown('公式 `=A1*B1` 结果');
check('行内代码内的星号不被解析', codeWithStars.includes('=A1*B1'), codeWithStars);

const ul = renderMarkdown('- 甲\n- 乙');
check('无序列表', ul.includes('<ul>') && ul.includes('<li>甲</li>') && ul.includes('<li>乙</li>'), ul);

const ol = renderMarkdown('1. 第一\n2. 第二');
check('有序列表', ol.includes('<ol>') && ol.includes('<li>第一</li>'), ol);

const table = renderMarkdown('| 名称 | 数量 |\n| --- | --- |\n| 铅笔 | 10 |');
check(
  '表格',
  table.includes('<th>名称</th>') && table.includes('<td>铅笔</td>') && !table.includes('---'),
  table,
);

const code = renderMarkdown('```js\nconst a = 1;\n```');
check('代码块保留内容', code.includes('const a = 1;') && code.includes('data-lang="js"'), code);

const quote = renderMarkdown('> 引用内容');
check('引用', quote.includes('<blockquote>引用内容</blockquote>'), quote);

check('分隔线', renderMarkdown('---').includes('<hr'), renderMarkdown('---'));

// ---- 流式未闭合 ----

const streamingCode = renderMarkdown('```python\nprint(1)');
check(
  '未闭合代码块仍渲染内容',
  streamingCode.includes('print(1)') && streamingCode.includes('md-code-streaming'),
  streamingCode,
);

const streamingBold = renderMarkdown('这是 **未闭合');
check('未闭合粗体不破坏输出', streamingBold.includes('未闭合'), streamingBold);

const streamingTable = renderMarkdown('| 名称 |');
check('单行表格降级为段落', streamingTable.includes('<p>'), streamingTable);

// 逐字符递增渲染不应抛异常——流式过程中每个前缀都会被渲染一次。
const full = '## 标题\n\n- 列表 `代码`\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n\n```js\nx=1\n```\n';
let crashed = null;
for (let i = 1; i <= full.length; i += 1) {
  try {
    renderMarkdown(full.slice(0, i));
  } catch (error) {
    crashed = `位置 ${i}：${error.message}`;
    break;
  }
}
check('逐字符流式渲染不抛异常', crashed === null, crashed ?? '');

// ---- 边界 ----

check('空输入', renderMarkdown('') === '', renderMarkdown(''));
check('null 输入', renderMarkdown(null) === '', renderMarkdown(null));
check('undefined 输入', renderMarkdown(undefined) === '', renderMarkdown(undefined));

console.log('');
console.log(`=== Markdown：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
