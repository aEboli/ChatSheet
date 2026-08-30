// 选择器的可读性：判定要一眼看得见，次要说明要收起来。
//
// 这个文件盯的是三件在旧实现里都不成立的事：
//
//   一、判定落在模型名的颜色上。此前只有一个 7px 的点在变色，而紧挨着它的
//       模型名无论可用不可用都是同一个黑——一列几十行扫下去，「能用」与
//       「报错说没这个模型」看着完全一样。
//   二、三态的说明收进悬停，行上不再为每个模型多占一行小字；但「正在确认」
//       必须留在行上——那一态没有颜色可依，且它正是「慢网关」与「点了没反应」
//       的唯一分辨依据。
//   三、档位一行一档，说明收进悬停，会降级的仍在行上留标注。
//       降级标注不许收起：它不是解释，是「选了不生效」。
//
// 假 DOM 照 model-probe.test.mjs。末尾带变异自检：断言若对着空节点也通过，
// 那就是假绿——这个项目已经在面板单测上付过一次这种代价。
//
// 另有一段对 app.css 的静态核对：藏「试一下」靠的是算出来的 opacity，
// 假 DOM 里没有计算样式，只能静态确认规则在、且没有写死颜色。
// 真实宿主里的可见性由 scripts/verify-picker.ps1 的 probe-visible 断言兜住。

import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const posted = [];

globalThis.window = {
  chrome: {
    webview: {
      postMessage: (message) => {
        posted.push(message);
        queueMicrotask(() => {
          globalThis.window.dispatchResponse?.({
            kind: 'response', id: message.id, ok: true, data: {},
          });
        });
      },
      addEventListener: (kind, handler) => {
        if (kind === 'message') {
          globalThis.window.dispatchResponse = (data) => handler({ data });
        }
      },
    },
  },
  innerWidth: 420,
};

function makeNode(tag = 'div') {
  const node = {
    tag,
    textContent: '',
    title: '',
    value: '',
    type: '',
    hidden: true,
    disabled: false,
    attributes: {},
    children: [],
    parent: null,
    listeners: new Map(),
    classes: new Set(),
    append: (...kids) => {
      for (const kid of kids) {
        if (!kid || typeof kid !== 'object') { node.children.push(kid); continue; }
        if (kid.parent && kid.parent !== node) {
          kid.parent.children = kid.parent.children.filter((n) => n !== kid);
        } else if (kid.parent === node) {
          node.children = node.children.filter((n) => n !== kid);
        }
        kid.parent = node;
        node.children.push(kid);
      }
    },
    replaceChildren: (...kids) => {
      for (const kid of node.children) {
        if (kid && typeof kid === 'object') { kid.parent = null; }
      }
      node.children = [];
      node.append(...kids);
    },
    setAttribute: (name, value) => { node.attributes[name] = value; },
    getAttribute: (name) => node.attributes[name],
    focus: () => {},
    addEventListener: (kind, handler) => node.listeners.set(kind, handler),
    classList: {
      add: (name) => node.classes.add(name),
      remove: (name) => node.classes.delete(name),
      contains: (name) => node.classes.has(name),
      toggle: (name, on) => (on ? node.classes.add(name) : node.classes.delete(name)),
    },
  };

  Object.defineProperty(node, 'className', {
    get: () => [...node.classes].join(' '),
    set: (value) => {
      node.classes.clear();
      for (const name of String(value).split(/\s+/).filter(Boolean)) {
        node.classes.add(name);
      }
    },
  });

  return node;
}

const ids = ['picker-models', 'picker-thinkings', 'picker-model', 'picker-thinking',
  'picker-trigger', 'picker-pop', 'picker-refresh', 'picker-only-favorites',
  'picker-probe-all', 'picker-manual', 'picker-manual-input', 'model-picker'];
const nodes = new Map(ids.map((id) => [id, makeNode()]));

globalThis.document = {
  getElementById: (id) => nodes.get(id) ?? null,
  querySelector: () => null,
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: (tag) => makeNode(tag),
};

const { initPicker, syncPicker } = await import('../../src/web/scripts/picker.js');
const { putModelCatalog } = await import('../../src/web/scripts/model-catalog.js');

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

const models = nodes.get('picker-models');
const thinkings = nodes.get('picker-thinkings');

function descend(node, out = []) {
  for (const kid of node.children ?? []) {
    if (!kid || typeof kid !== 'object') { continue; }
    out.push(kid);
    descend(kid, out);
  }
  return out;
}

/** 模型行里那个可点的 .picker-item。判定的 class 落在它身上。 */
function itemFor(id) {
  const name = descend(models).find(
    (n) => n.classes.has('picker-item-name') && n.textContent === id,
  );
  // .picker-item-name → .picker-item-head → .picker-item
  return name?.parent?.parent ?? null;
}

function inlineHintFor(id) {
  const item = itemFor(id);
  return item?.children.find((n) => n.classes.has('picker-item-hint'))?.textContent ?? '无';
}

/** 档位行。这一列的行本身就是 .picker-item，名字直接挂在上面。 */
function thinkingRow(label) {
  return descend(thinkings).find(
    (n) => n.classes.has('picker-item') &&
      n.children.some((c) => c.classes.has('picker-item-name') && c.textContent === label),
  ) ?? null;
}

function tagOn(label) {
  return thinkingRow(label)?.children
    .find((n) => n.classes.has('picker-thinking-tag'))?.textContent ?? '无';
}

const connection = {
  mode: 'CustomApi',
  customProtocol: 'openai-chat-completions',
  customBaseUrl: 'https://gw.example.test/v1',
};

initPicker(() => {});
putModelCatalog(connection, ['alpha', 'beta', 'gamma', 'delta']);

// Gemini 那套：不支持 XHigh 与 Max，因此这两档应当带降级标注。
const thinkingOptions = [
  { id: 'Off', label: 'Off', hint: '不思考，最快，适合简单改动' },
  { id: 'Minimal', label: 'Minimal', hint: '仅 OpenAI 与 Gemini 支持，其他协议按 Low 处理' },
  { id: 'Low', label: 'Low', hint: '速度优先，适合明确的小任务' },
  { id: 'Medium', label: 'Medium', hint: '速度与质量平衡' },
  { id: 'High', label: 'High', hint: '多数模型的默认档，适合复杂表格逻辑' },
  { id: 'XHigh', label: 'XHigh', hint: '长链路任务；不支持时按 High 处理' },
  { id: 'Max', label: 'Max', hint: '不限制思考开销；不支持时按 High 处理' },
];

syncPicker({
  ...connection,
  model: 'delta',
  thinking: 'High',
  thinkingOptions,
  thinkingSupported: ['Off', 'Minimal', 'Low', 'Medium', 'High'],
  favorites: [],
  availability: { alpha: 'Available', beta: 'Unavailable' },
  onlyFavoriteModels: false,
});

console.log('检查判定的可见性：');

// ---------- 判定落在行上，不只落在那个点上 ----------

check(
  '不可用的行带 is-unavailable（模型名会变红）',
  itemFor('beta')?.classes.has('is-unavailable') === true,
  itemFor('beta')?.className,
);

check(
  '可用的行带 is-available',
  itemFor('alpha')?.classes.has('is-available') === true,
  itemFor('alpha')?.className,
);

check(
  '未确认的行不带任何结论 class',
  itemFor('gamma') !== null &&
    !itemFor('gamma').classes.has('is-available') &&
    !itemFor('gamma').classes.has('is-unavailable'),
  itemFor('gamma')?.className,
);

check(
  '可用与不可用带的是不同的 class（否则颜色分不开）',
  itemFor('alpha')?.classes.has('is-unavailable') === false &&
    itemFor('beta')?.classes.has('is-available') === false,
  `${itemFor('alpha')?.className} / ${itemFor('beta')?.className}`,
);

// ---------- 结论的说明收进悬停，行上不再多占一行 ----------

check(
  '可用的行上没有说明文字',
  inlineHintFor('alpha') === '无',
  inlineHintFor('alpha'),
);

check(
  '不可用的行上也没有说明文字',
  inlineHintFor('beta') === '无',
  inlineHintFor('beta'),
);

check(
  '可用的说明在悬停里',
  itemFor('alpha')?.title.includes('能用') === true,
  itemFor('alpha')?.title,
);

check(
  '不可用的说明在悬停里',
  itemFor('beta')?.title.includes('报错说没这个模型') === true,
  itemFor('beta')?.title,
);

check(
  '不可用的悬停说明讲清它仍然可选',
  itemFor('beta')?.title.includes('仍可点击使用') === true,
  itemFor('beta')?.title,
);

check(
  '未确认的悬停说明指路到「试一下」',
  itemFor('gamma')?.title.includes('试一下') === true,
  itemFor('gamma')?.title,
);

check(
  '悬停说明里带着模型 ID（列窄时名字可能被折断）',
  itemFor('beta')?.title.startsWith('beta') === true,
  itemFor('beta')?.title,
);

// ---------- 「正在确认」仍然留在行上 ----------

const probe = itemFor('gamma')?.parent?.children.find((n) => n.classes.has('picker-probe'));
probe?.listeners.get('click')({ stopPropagation: () => {} });

check(
  '正在确认时行上有文字（这一态没有颜色可依）',
  inlineHintFor('gamma').includes('正在确认'),
  inlineHintFor('gamma'),
);

check(
  '正在确认的行带 is-probing',
  itemFor('gamma')?.classes.has('is-probing') === true,
  itemFor('gamma')?.className,
);

console.log('');
console.log('检查思考等级一行一档：');

// ---------- 档位：一行一档，说明进悬停 ----------

check('七档都在', thinkingOptions.every((o) => thinkingRow(o.label) !== null), '');

check(
  '档位行用一行一项的排布（picker-item-line）',
  thinkingRow('High')?.classes.has('picker-item-line') === true,
  thinkingRow('High')?.className,
);

for (const label of ['Off', 'Minimal', 'High']) {
  check(
    `${label} 行上没有说明文字`,
    thinkingRow(label)?.children.every((n) => !n.classes.has('picker-item-hint')) === true,
    thinkingRow(label)?.children.map((n) => n.className).join('+'),
  );
}

check(
  'Off 的说明在悬停里',
  thinkingRow('Off')?.title.includes('不思考') === true,
  thinkingRow('Off')?.title,
);

check(
  'Minimal 的说明在悬停里',
  thinkingRow('Minimal')?.title.includes('仅 OpenAI 与 Gemini 支持') === true,
  thinkingRow('Minimal')?.title,
);

// ---------- 降级标注不许收起 ----------

check(
  '不支持的档位在行上留标注',
  tagOn('XHigh') === '会降级' && tagOn('Max') === '会降级',
  `XHigh=${tagOn('XHigh')} Max=${tagOn('Max')}`,
);

check(
  '支持的档位没有标注',
  tagOn('High') === '无' && tagOn('Low') === '无',
  `High=${tagOn('High')} Low=${tagOn('Low')}`,
);

check(
  '降级的行也带 is-downgraded',
  thinkingRow('XHigh')?.classes.has('is-downgraded') === true,
  thinkingRow('XHigh')?.className,
);

check(
  '降级的悬停说明讲清会降到哪',
  thinkingRow('XHigh')?.title.includes('就近降级') === true,
  thinkingRow('XHigh')?.title,
);

check(
  '当前档位是选中态',
  thinkingRow('High')?.classes.has('is-active') === true,
  thinkingRow('High')?.className,
);

console.log('');
console.log('检查 app.css 的对应规则：');

// 假 DOM 没有计算样式，这一段只静态确认规则在、且都走调色板变量。
// 真实宿主里「不悬停时透明度为 0」由 verify-picker.ps1 断言。
const here = dirname(fileURLToPath(import.meta.url));
const css = readFileSync(join(here, '..', '..', 'src', 'web', 'styles', 'app.css'), 'utf8');
const html = readFileSync(
  join(here, '..', '..', 'src', 'web', 'index.html'), 'utf8');
const pickerJs = readFileSync(
  join(here, '..', '..', 'src', 'web', 'scripts', 'picker.js'), 'utf8');

/*
  去掉注释后的 CSS，专供「某条声明在不在」这类断言。

  必要性是踩出来的：本文件的注释里写着「min-width: 0 是关键」，于是断言
  /min-width:\s*0/ 对着注释就通过了——把声明删掉照样绿。凡是断言声明存在，
  一律用这一份。
*/
const cssBare = css.replace(/\/\*[\s\S]*?\*\//g, '');

/** 取某条规则的声明块。 */
function rule(selector) {
  const start = css.indexOf(`\n${selector} {`);
  if (start === -1) { return ''; }
  const open = css.indexOf('{', start);
  const close = css.indexOf('}', open);
  return css.slice(open + 1, close);
}

const probeRule = rule('.picker-probe');
check(
  '「试一下」默认 opacity: 0',
  /opacity:\s*0\s*;/.test(probeRule),
  probeRule.trim().split('\n').slice(0, 3).join(' '),
);
check(
  '「试一下」默认不接收点击（否则会挡住行的点击区）',
  /pointer-events:\s*none/.test(probeRule),
  '',
);
check(
  '「试一下」绝对定位（用 display: none 会让浮出时整行重排）',
  /position:\s*absolute/.test(probeRule),
  '',
);
check(
  '悬停与键盘聚焦都会让它显形',
  /\.picker-row:hover\s+\.picker-probe,\s*\n\.picker-row:focus-within\s+\.picker-probe/.test(css),
  '缺 :focus-within 时键盘用户永远拿不到这个按钮',
);
check(
  '没有悬停的设备上常显',
  /@media\s*\(hover:\s*none\)[\s\S]{0,200}\.picker-probe/.test(css),
  '触屏上藏在悬停后面等于这个入口不存在',
);

check(
  '不可用的模型名用 --error 上色',
  /\.picker-item\.is-unavailable\s+\.picker-item-name\s*\{[^}]*var\(--error\)/.test(css),
  '',
);
check(
  '选中态盖过不可用的红字（否则选中的行读不出是选中的）',
  /\.picker-item\.is-unavailable\.is-active\s+\.picker-item-name/.test(css),
  '',
);
check(
  '摘要行的不可用也用 --error（与列表里同一个颜色）',
  /\.picker-model\.is-unavailable\s*\{[^}]*var\(--error\)/.test(css),
  '同一件事换个颜色，用户要学两遍',
);
check(
  '降级标注走调色板的琥珀',
  /\.picker-thinking-tag\s*\{[^}]*var\(--warn-bg\)[\s\S]*?var\(--warn-text\)/.test(css),
  '',
);
check(
  '浮层限了总高（向上弹出，超出视口顶端会被静默裁掉）',
  /\.picker-pop\s*\{[^}]*max-height:\s*min\(60vh,\s*calc\(100vh/.test(css),
  '上限要按视口比例算一份：输入框能长到 200px，那时浮层可用的高度更小，'
    + '而纯减法里的常数是固定的',
);
check(
  '模型列表有高度下限（否则矮视口下会被压到空白）',
  /\.picker-list\s*\{[^}]*min-height:\s*80px/.test(css),
  '',
);
check(
  '浮层宽度按内容取（max-content），短目录不留空、长 ID 不截断',
  /\.picker-pop\s*\{[^}]*width:\s*max-content/.test(cssBare),
  '定值两头都不理想：ID 短时右边空一块，ID 长时又被截断',
);
check(
  '宽度不再用 min-width（它永远赢过 max-width，窄栏下会出界）',
  !/\.picker-pop\s*\{[^}]*min-width:/.test(cssBare),
  '',
);
check(
  '仍有 max-width 兜住窄面板',
  /\.picker-pop\s*\{[^}]*max-width:\s*calc\(100vw - 24px\)/.test(css),
  '写 width 之后 max-width 才真正管得住它',
);
check(
  '档位列表在矮视口下肯让高度并自己滚',
  /\.picker-list-thinking\s*\{[^}]*min-height:\s*0[\s\S]*?overflow-y:\s*auto/.test(css),
  '不肯让的话被压的只有模型段',
);
check(
  '浮层左右分两列',
  /\.picker-pop\s*\{[^}]*flex-direction:\s*row/.test(css),
  '',
);
check(
  '模型列吃掉剩余宽度且 min-width: 0（否则长模型 ID 会把档位列顶出去）',
  /\.picker-col-models\s*\{[^}]*flex:\s*1 1 auto[\s\S]*?min-width:\s*0/.test(css),
  '',
);
check(
  '档位列宽由内容决定（max-content），不写死数值',
  /\.picker-col-thinking\s*\{[^}]*flex:\s*0 0 auto[\s\S]*?width:\s*max-content/.test(cssBare),
  '写死数值只对当前字体与文案成立，换一个就要么折行要么多出空白',
);
check(
  '档位行硬禁止折行（列宽算得再准，字体一换余量就没了）',
  /\.picker-item-line\s*\{[^}]*flex-wrap:\s*nowrap/.test(cssBare),
  '折行不报错，只是让「一行一档」静默失效',
);
check(
  '档位列的分隔线在左边（分栏而非上下排）',
  /\.picker-col-thinking\s*\{[^}]*border-left:/.test(css) &&
    !/\.picker-col-thinking\s*\{[^}]*border-top:/.test(css),
  '',
);
// ---------- 档位列：居中、不留空白 ----------

check(
  '档位行居中，不靠左（窄列靠左会读成贴着分隔线的一竖条）',
  /\.picker-item-line\s*\{[^}]*justify-content:\s*center/.test(cssBare),
  'space-between 在只有一个子元素时等于靠左，有标注时又把两者推到两端',
);
check(
  '档位列的列头也居中（否则标题贴左、档位居中，一列两种对齐）',
  /\.picker-col-thinking \.picker-col-head\s*\{[^}]*justify-content:\s*center/.test(cssBare),
  '',
);
check(
  '档位列头不再挂「悬停看说明」那句小字',
  !/picker-col-note/.test(html) ||
    !/picker-col-thinking[\s\S]{0,400}picker-col-note/.test(html),
  '这一列按 max-content 定宽，那句小字比最长档位名还宽，会一个人把整列撑宽六十来像素',
);
check(
  '档位列头补了与滚动条槽等宽的右内边距（否则标题与档位名差 3px）',
  /\.picker-col-thinking \.picker-col-head\s*\{[^}]*padding-right:\s*12px/.test(cssBare),
  '列表恒占一条 6px 的槽而列头没有，两边都居中时中心就差半条槽',
);
check(
  '选择器的滚动条收窄到 6px（默认 15px 会把这个差放大到 7.5px）',
  /\.picker-list::-webkit-scrollbar\s*\{[^}]*width:\s*6px/.test(cssBare),
  '照 .queue-strip 的先例：只有 webkit 伪元素能真收到 6px',
);
check(
  '模型列表给滚动条预留槽位',
  /\.picker-list\s*\{[^}]*scrollbar-gutter:\s*stable/.test(cssBare),
  '不预留时 max-content 算不到滚动条那 15px，最长 ID 的尾巴正好被裁掉',
);

// ---------- 一行显示：模型 ID、列头、档位行 ----------

check(
  '模型名一行显示、放不下截断成省略号（不再折行）',
  /^\.picker-item-name\s*\{[^}]*white-space:\s*nowrap[\s\S]*?text-overflow:\s*ellipsis/m.test(cssBare),
  '折行会把一列几十个模型翻成上百行，翻找更难；完整 ID 在悬停第一行',
);
check(
  '模型名所在层允许收缩（否则 text-overflow 永远不触发）',
  /^\.picker-item-name\s*\{[^}]*min-width:\s*0/m.test(cssBare) &&
    /\.picker-item-head\s*\{[^}]*overflow:\s*hidden/.test(cssBare),
  'flex 项默认不小于内容宽度，不写 min-width: 0 时长名照旧把行顶宽',
);
check(
  '模型名吃掉状态点之外的宽度',
  /\.picker-item-head \.picker-item-name\s*\{[^}]*flex:\s*1 1 auto/.test(css),
  '',
);
check(
  '列头装不下时折行而不是溢出（溢出会让最右边的按钮点不到）',
  /\.picker-col-head\s*\{[^}]*flex-wrap:\s*wrap/.test(cssBare),
  'nowrap 在窄面板上把「刷新」整个裁掉，且不产生滚动条',
);
check(
  '列头里的文字本身不换行（按钮名折断比折行更难读）',
  /\.picker-col-head\s*\{[^}]*white-space:\s*nowrap/.test(cssBare),
  '',
);
for (const [sel, why] of [
  ['picker-only-favorites', '名单开关'],
  ['picker-probe-all', '确认按钮'],
  ['picker-refresh', '刷新按钮'],
]) {
  check(
    `列头的${why}不参与收缩、不换行`,
    // 模板字符串里 \. 会先被它自己吃掉变成 .，所以这里必须写 \\.
    new RegExp(`\\.${sel}\\s*\\{[^}]*flex:\\s*0 0 auto`).test(css) &&
      new RegExp(`\\.${sel}\\s*\\{[^}]*white-space:\\s*nowrap`).test(css),
    '列头挤时它会被压变形',
  );
}
check(
  '「开着但没在筛」用虚线描边表现，不靠加长文字',
  /\.picker-only-favorites\.is-suspended\s*\{[^}]*border-style:\s*dashed/.test(css),
  '加长文字（只看名单（本次先不收起））有 130px，一出现列头就折',
);
check(
  '列头右端的小字不参与列宽',
  /\.picker-col-note\s*\{[^}]*min-width:\s*0[\s\S]*?text-overflow:\s*ellipsis/.test(css) ||
    /\.picker-col-note\s*\{[^}]*text-overflow:\s*ellipsis[\s\S]*?min-width:\s*0/.test(css),
  '档位列按 max-content 定宽，这行小字比最宽档位行长就会白撑宽整列',
);

check(
  '名单开关的文字缩成「名单」',
  /toggle\.textContent = '名单'/.test(pickerJs),
  '',
);
check(
  '批量确认的文字缩成「确认」',
  /button\.textContent = '确认'/.test(pickerJs),
  '',
);
check(
  '缩短后的说法进了悬停（作用范围不能因为缩字而丢掉）',
  /逐个确认名单里的/.test(pickerJs) && /只看名单：/.test(pickerJs),
  '',
);

check(
  '减少动效时「正在确认」的点仍看得见',
  /\.picker-availability-dot\.is-probing\s*\{\s*opacity:\s*1/.test(css.replace(/\n\s*/g, '\n')) ||
    /\.pending-dots i,\s*\n\s*\.picker-availability-dot\.is-probing\s*\{\s*opacity:\s*1/.test(css),
  '动画关掉后它会停在动画起始的低透明度上',
);

// ---------- 变异自检 ----------

console.log('');
models.replaceChildren();
thinkings.replaceChildren();
const blind = itemFor('beta') !== null || thinkingRow('High') !== null;
check(
  '清空两列后断言会失败（说明断言真的在看渲染结果）',
  !blind,
  '断言对着空节点也通过，是假绿',
);

console.log('');
console.log(`=== 选择器可读性：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
