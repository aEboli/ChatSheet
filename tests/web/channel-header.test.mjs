import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const web = join(here, '..', '..', 'src', 'web');
const html = readFileSync(join(web, 'index.html'), 'utf8');
const css = readFileSync(join(web, 'styles', 'app.css'), 'utf8');

const attributes = new Map();
const node = {
  textContent: '',
  title: '',
  hidden: true,
  setAttribute(name, value) { attributes.set(name, value); },
};
globalThis.document = {
  getElementById(id) { return id === 'app-channel' ? node : null; },
};

const { updateChannelHeader } = await import('../../src/web/scripts/channel-header.js');

let failed = 0;
function check(label, condition) {
  console.log(`  ${condition ? '通过' : '失败'}  ${label}`);
  if (!condition) { failed += 1; }
}

console.log('检查标题栏渠道显示：');
updateChannelHeader({ channelLabel: 'codex cli · Moon Stars' });
check('显示后端返回的实际渠道', node.textContent === 'codex cli · Moon Stars' && !node.hidden);
check('悬停与读屏取得完整文本', node.title === node.textContent &&
  attributes.get('aria-label') === `当前渠道：${node.textContent}`);

updateChannelHeader({ channelLabel: '  DIY  ' });
check('自定义渠道文本按原样安全写入', node.textContent === 'DIY');
updateChannelHeader({});
check('缺失渠道时不占标题空间', node.hidden && node.textContent === '');

check('标题节点位于版本和导航之间',
  html.indexOf('id="app-version"') < html.indexOf('id="app-channel"') &&
  html.indexOf('id="app-channel"') < html.indexOf('<nav class="app-nav">'));
check('窄栏使用省略号且导航不可收缩',
  /\.app-channel\s*\{[\s\S]*?min-width:\s*0;[\s\S]*?text-overflow:\s*ellipsis;[\s\S]*?\}/.test(css) &&
  /\.app-nav\s*\{[\s\S]*?flex:\s*0 0 auto;[\s\S]*?\}/.test(css));

console.log(`\n=== 标题栏渠道显示：失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
