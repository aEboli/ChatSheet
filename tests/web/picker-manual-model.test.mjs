// 手填模型 ID 的回归测试。
//
// 缺陷现场：选择器里有「直接填模型 ID」的表单，却没有任何脚本绑定它的
// submit——点「使用」或按 Enter 完全没反应。表单默认提交还会被页面 CSP 的
// form-action 'none' 拦掉，而面板里看不到控制台，症状就是「按了没用」。
//
// 这里用 DOM 存根驱动真实的 submit 处理函数，验证三件事：
// 阻止默认提交、模型并进列表并成为选中项、把 ID 发给加载项。

let posted = [];

// bridge.js 在导入时就取一次 chrome.webview，必须先于 picker.js 准备好。
globalThis.window = {
  chrome: {
    webview: {
      addEventListener: () => {},
      // session.update 的响应不会回来，push() 里的 await 就此挂着。
      // 测试只关心「发出去的是什么」，挂着不影响断言。
      postMessage: (message) => posted.push(message),
    },
  },
  innerWidth: 420,
};

/** 最小 DOM 节点：只实现渲染与事件绑定用到的成员。 */
function makeNode(tag = 'div') {
  const node = {
    tag,
    className: '',
    textContent: '',
    title: '',
    type: '',
    value: '',
    hidden: true,
    children: [],
    focused: 0,
    listeners: new Map(),
    classes: new Set(),
    append: (...kids) => node.children.push(...kids),
    replaceChildren: (...kids) => { node.children = [...kids]; },
    setAttribute: () => {},
    focus: () => { node.focused += 1; },
    addEventListener: (kind, handler) => node.listeners.set(kind, handler),
    classList: {
      add: (name) => node.classes.add(name),
      remove: (name) => node.classes.delete(name),
      contains: (name) => node.classes.has(name),
      toggle: (name, on) => (on ? node.classes.add(name) : node.classes.delete(name)),
    },
  };
  return node;
}

const nodes = new Map();
for (const id of ['picker-manual', 'picker-manual-input', 'picker-models',
  'picker-model', 'picker-thinking', 'picker-trigger', 'picker-refresh']) {
  nodes.set(id, makeNode());
}

globalThis.document = {
  getElementById: (id) => nodes.get(id) ?? null,
  querySelector: () => null,
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: (tag) => makeNode(tag),
};

const { initPicker, syncPicker, describePicker } = await import('../../src/web/scripts/picker.js');
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

const form = nodes.get('picker-manual');
const input = nodes.get('picker-manual-input');
const list = nodes.get('picker-models');

initPicker(() => {});

const connection = {
  mode: 'CustomApi',
  customProtocol: 'openai-chat-completions',
  customBaseUrl: 'https://no-list.example.test/v1',
};
putModelCatalog(connection, ['listed-a', 'listed-b']);
syncPicker({ ...connection, model: 'listed-a', thinking: 'High' });

console.log('检查手填模型 ID：');

check('submit 已被绑定', typeof form.listeners.get('submit') === 'function');

/** 触发一次表单提交，返回 preventDefault 是否被调用。 */
function submit(text) {
  input.value = text;
  let prevented = 0;
  form.listeners.get('submit')?.({ preventDefault: () => { prevented += 1; } });
  return prevented === 1;
}

/**
 * 深度查找带某个 class 的节点。假 DOM 没有 querySelector，只能自己走。
 *
 * 必须同时看 className 与 classList：createElement 后直接赋 className 的节点
 * 不会进 classes 集合，只看后者会把整棵树都判成没有这个 class。
 */
function findByClass(node, name) {
  if (!node) { return null; }
  const inClassName = String(node.className ?? '').split(/\s+/).includes(name);
  if (inClassName || node.classes?.has(name)) { return node; }
  for (const child of node.children ?? []) {
    const hit = findByClass(child, name);
    if (hit) { return hit; }
  }
  return null;
}

/**
 * 当前模型列里被标为选中的那一项的文字。
 *
 * 按 class 找而不是取 children[0]：模型行现在是
 * .picker-row > .picker-item > .picker-item-head > .picker-item-name，
 * 而假 DOM 的 textContent 不从后代聚合，取第一个孩子只会拿到空串。
 */
function activeRow() {
  const row = list.children
    .map((c) => findByClass(c, 'is-active'))
    .find((c) => c !== null);
  return findByClass(row, 'picker-item-name')?.textContent ?? '<无>';
}

const sessionUpdates = () => posted.filter((m) => m.channel === 'session.update');

check('提交阻止了默认表单行为', submit('gpt-manual-1'));
check(
  '手填的模型成为当前模型并并入列表',
  describePicker().includes('模型=gpt-manual-1') && describePicker().includes('模型项=3'),
  describePicker(),
);
check('手填的模型在列表里显示为选中项', activeRow() === 'gpt-manual-1', activeRow());
check(
  '手填的模型已发给加载项',
  sessionUpdates().length === 1 && sessionUpdates()[0].payload.model === 'gpt-manual-1',
  JSON.stringify(sessionUpdates()),
);
check('提交后清空输入框', input.value === '', `残留：${input.value}`);

// 空白提交：没什么可采用，不发请求、不加空条目，焦点留在输入框。
const focusedBefore = input.focused;
submit('   ');
check(
  '空白提交不生效也不发请求',
  describePicker().includes('模型项=3') && sessionUpdates().length === 1,
  describePicker(),
);
check('空白提交后焦点留在输入框', input.focused === focusedBefore + 1);

// 重复填当前模型：不该重复发请求，也不该在列表里出现两项。
submit('gpt-manual-1');
check(
  '重复填当前模型不重复发请求',
  sessionUpdates().length === 1,
  JSON.stringify(sessionUpdates()),
);
check(
  '重复填当前模型不产生重复条目',
  describePicker().includes('模型项=3') && activeRow() === 'gpt-manual-1',
  describePicker(),
);
check('重复提交同样会清空输入框', input.value === '');

// 首尾空格必须去掉：带空格的 ID 发给网关会被判成未知模型。
submit('  spaced-model  ');
check(
  '首尾空格已去掉',
  describePicker().includes('模型=spaced-model') && activeRow() === 'spaced-model',
  describePicker(),
);
check(
  '去空格后的 ID 才是发出去的那个',
  sessionUpdates().length === 2 && sessionUpdates()[1].payload.model === 'spaced-model',
  JSON.stringify(sessionUpdates()),
);

console.log('');
console.log(`=== 手填模型 ID：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
