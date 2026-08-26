// 能力回退的两条提示。
//
// 为什么值得锁：这两条是「这个模型能做什么」发生了变化的唯一告知。工具形态从
// 原生降到文本指令，用户看不出区别（操作卡片照样出现），但顾问模式下就完全不同
// 了——它再也改不了表格。图片那条更要紧：图片被去掉却不说，用户会以为模型看过
// 自己的截图，从而相信一个从未基于它的回答。
//
// 因此断言分两半：
//   一、两条都必须落成对话流里的胶囊，而不只是写进处理指示器（指示器一闪而过，
//       事后回看什么都没有）；
//   二、两条都不能影响本轮的收尾判定——降级之后这一轮仍会正常跑完，
//       所以「已完成」照样要有，不能因为插了提示就把收尾吞掉。
//
// 运行：node tests/web/capability-fallback.test.mjs

const posted = [];
let hostHandler = null;

globalThis.window = {
  chrome: {
    webview: {
      addEventListener: (kind, handler) => {
        if (kind === 'message') { hostHandler = handler; }
      },
      postMessage: (message) => posted.push(message),
    },
  },
  innerWidth: 420,
  location: { hash: '' },
};

// 假 DOM。append 摘走原父节点是必需的：本文件要看提示落在对话流的哪个位置，
// 做虚了顺序断言会对着空节点通过（见 chatsheet-fake-dom-silently-passes）。
function makeNode(tag = 'div') {
  const node = {
    tag,
    textContent: '',
    innerHTML: '',
    title: '',
    value: '',
    type: '',
    disabled: false,
    hidden: false,
    open: false,
    scrollTop: 0,
    scrollHeight: 40,
    clientHeight: 17,
    style: {},
    dataset: {},
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
    remove: () => {
      const parent = node.parent;
      if (!parent) { return; }
      parent.children = parent.children.filter((n) => n !== node);
      node.parent = null;
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
    querySelector: (selector) => descendants(node).find((n) => matches(n, selector)) ?? null,
    querySelectorAll: (selector) => descendants(node).filter((n) => matches(n, selector)),
    getBoundingClientRect: () => ({ width: 400, height: 200 }),
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

function descendants(node) {
  const out = [];
  for (const kid of node.children) {
    if (!kid || typeof kid !== 'object') { continue; }
    out.push(kid);
    if (Array.isArray(kid.children)) { out.push(...descendants(kid)); }
  }
  return out;
}

function matches(node, selector) {
  for (const part of String(selector).split(',')) {
    const one = part.trim();
    const attr = one.match(/^\.([\w-]+)\[([\w-]+)="(.*)"\]$/);
    if (attr) {
      const [, cls, name, value] = attr;
      const key = name.startsWith('data-')
        ? name.slice(5).replace(/-(\w)/g, (_, c) => c.toUpperCase())
        : name;
      if (node.classes?.has(cls) && node.dataset?.[key] === value) { return true; }
      continue;
    }
    if (one.startsWith('.')) {
      if (node.classes?.has(one.slice(1))) { return true; }
    } else if (one && node.tag === one) {
      return true;
    }
  }
  return false;
}

const nodes = new Map();
function nodeFor(id) {
  if (!nodes.has(id)) { nodes.set(id, makeNode()); }
  return nodes.get(id);
}

globalThis.document = {
  getElementById: (id) => nodeFor(id),
  querySelector: () => makeNode(),
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: (tag) => makeNode(tag),
};

globalThis.getComputedStyle = () => ({ lineHeight: '17' });

const { initChat } = await import('../../src/web/scripts/chat.js');

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

initChat();

const transcript = nodeFor('transcript');
const composer = nodeFor('composer');
const sendButton = nodeFor('send');
const tick = () => new Promise((resolve) => setImmediate(resolve));

const topLevel = (cls) => transcript.children.filter((n) => n.classes?.has(cls));
const notices = () => topLevel('notice');
const marks = () => topLevel('notice-complete');

function reply(channel, data, ok = true) {
  const message = [...posted].reverse().find((m) => m.channel === channel);
  if (!message) { throw new Error(`没有发出 ${channel}`); }
  hostHandler({
    data: { kind: 'response', id: message.id, ok, data, error: ok ? undefined : '宿主拒绝' },
  });
  return message;
}

const agent = (payload) => hostHandler({ data: { kind: 'agent', ...payload } });

async function startTurn(text) {
  composer.value = text;
  sendButton.listeners.get('click')?.({});
  await tick();
}

console.log('检查能力回退的两条提示：');
console.log('');

// ---- 一、工具形态降级 ----

const toolNoticeText = '该模型不支持原生工具调用（接口回复：model does not support tools）。' +
  '已改用文本指令方式，功能不变。';

await startTurn('把 B 列改成日期格式');

const beforeTool = notices().length;
agent({ stage: 'tool-fallback', text: toolNoticeText, payload: { mode: 'Text' } });
await tick();

check('工具降级落成一条胶囊', notices().length === beforeTool + 1,
  `${beforeTool} → ${notices().length}`);

const toolNotice = notices()[notices().length - 1] ?? makeNode();
check('提示原文来自加载项，不在面板另写一套',
  toolNotice.textContent === toolNoticeText, toolNotice.textContent);
check('用告警档，不是错误档：功能没丢，只是换了方式',
  toolNotice.classes.has('notice-warn') && !toolNotice.classes.has('notice-error'),
  toolNotice.className);

// 降级之后这一轮照常跑：操作卡片仍要出现，收尾仍要判定为正常。
agent({ stage: 'tool-start', payload: { id: 't1', name: 'format_range', risk: 'Write', args: {} } });
await tick();
agent({ stage: 'tool-result', payload: { id: 't1', name: 'format_range', ok: true, data: { cells_affected: 20 } } });
await tick();

check('文本协议下的操作照样出卡片', topLevel('tool-card').length >= 1,
  `${topLevel('tool-card').length} 张`);

agent({ stage: 'text', text: '已把 B 列设为日期格式。' });
await tick();

const marksBefore = marks().length;
agent({ stage: 'turn-complete' });
reply('chat.send', { ok: true });
await tick();

check('降级过的一轮仍算正常收尾', marks().length === marksBefore + 1,
  `${marksBefore} → ${marks().length}`);

// ---- 二、顾问模式 ----

const advisorText = '该模型既不支持原生工具调用，也未能按格式发出指令块，无法直接操作表格。' +
  '已切换为顾问模式：它会给出公式与操作步骤，由你在表格里执行。';

await startTurn('帮我算一下同比');

const beforeAdvisor = notices().length;
agent({ stage: 'tool-fallback', text: advisorText, payload: { mode: 'None' } });
await tick();

check('顾问模式也落成胶囊', notices().length === beforeAdvisor + 1,
  `${beforeAdvisor} → ${notices().length}`);
check('顾问模式的提示说明它只能给方案',
  (notices()[notices().length - 1]?.textContent ?? '').includes('顾问模式'),
  notices()[notices().length - 1]?.textContent);

agent({ stage: 'text', text: '在 C2 填 =B2/A2-1，然后向下填充。' });
await tick();
agent({ stage: 'turn-complete' });
reply('chat.send', { ok: true });
await tick();

// ---- 三、视觉回退 ----

const visionText = '当前模型没有视觉能力，2 张图片未能送达，已去掉图片继续这一轮。' +
  '可在设置页把模型换成带视觉的型号，或填写「视觉中转模型」让另一个模型先把图转成文字。';

await startTurn('这张截图里的报错怎么解决');

const beforeVision = notices().length;
agent({
  stage: 'vision-fallback',
  text: visionText,
  payload: { images: 2, relayModel: null, relayed: false },
});
await tick();

check('视觉回退落成一条胶囊', notices().length === beforeVision + 1,
  `${beforeVision} → ${notices().length}`);

const visionNotice = notices()[notices().length - 1] ?? makeNode();
check('视觉提示说清图片没送达', visionNotice.textContent.includes('未能送达'),
  visionNotice.textContent);
check('视觉提示给出下一步怎么办',
  visionNotice.textContent.includes('视觉中转模型') || visionNotice.textContent.includes('换成带视觉'),
  visionNotice.textContent);
check('视觉提示用告警档', visionNotice.classes.has('notice-warn'), visionNotice.className);

agent({ stage: 'text', text: '我看不到图片，请把报错文字贴给我。' });
await tick();

const marksBeforeVision = marks().length;
agent({ stage: 'turn-complete' });
reply('chat.send', { ok: true });
await tick();

check('视觉回退过的一轮仍算正常收尾', marks().length === marksBeforeVision + 1,
  `${marksBeforeVision} → ${marks().length}`);

// ---- 四、中转成功时的措辞不同 ----

await startTurn('再看这张');

const relayText = '当前模型没有视觉能力，已用 gpt-4o-mini 把 1 张图片转写成文字后交给它。';
agent({
  stage: 'vision-fallback',
  text: relayText,
  payload: { images: 1, relayModel: 'gpt-4o-mini', relayed: true },
});
await tick();

const relayNotice = notices()[notices().length - 1] ?? makeNode();
check('中转成功时说的是「已转写」而非「未能送达」',
  relayNotice.textContent.includes('转写') && !relayNotice.textContent.includes('未能送达'),
  relayNotice.textContent);

agent({ stage: 'turn-complete' });
reply('chat.send', { ok: true });
await tick();

console.log('');
console.log(`结果：通过 ${passed}，失败 ${failed}`);
process.exit(failed === 0 ? 0 : 1);
