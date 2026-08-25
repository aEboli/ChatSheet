// 输入队列的两个边界：轮转闸门与附件归属。
//
// send-stop.test.mjs 覆盖的是按钮三态与队列的可见状态，都在「一轮还没结束」时
// 就能断言完。这里补的是只有让轮次真正逐条跑完才暴露得出来的两件事：
//
// 一、轮转闸门。pumpQueue 会被多个入口触发（用户提交、上一轮结束）。闸门失效时
//    两条链会各自取出条目并发调用 chat.send，而加载项只接受一轮，第二条撞上
//    BUSY 就白丢一次输入。因此断言全程在途的 chat.send 峰值为 1，
//    且每条输入恰好发出一次、顺序与提交一致。
//
// 二、附件归属在入队时定。输入框提交后立刻清空，若发送时才去取附件，
//    排队条目就会捎上此后新加的附件——用户以为发的是当时那张图。
//
// 两条都用变异验证过：把闸门条件改成永假会让本文件报「并发」「重复发送」；
// 把入队取快照改成发送时取会让第一条带错图、第二条没图。
//
// 运行：node tests/web/queue.test.mjs

const posted = [];
let messageListener = null;

globalThis.window = {
  chrome: {
    webview: {
      addEventListener: (kind, handler) => {
        if (kind === 'message') { messageListener = handler; }
      },
      postMessage: (message) => posted.push(message),
    },
  },
  innerWidth: 420,
  location: { hash: '' },
};

function makeNode(tag = 'div') {
  const node = {
    tag,
    className: '',
    textContent: '',
    innerHTML: '',
    title: '',
    value: '',
    type: '',
    src: '',
    alt: '',
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
    listeners: new Map(),
    classes: new Set(),
    append: (...kids) => node.children.push(...kids),
    remove: () => { node.removed = true; },
    replaceChildren: (...kids) => { node.children = [...kids]; },
    setAttribute: (name, value) => { node.attributes[name] = value; },
    getAttribute: (name) => node.attributes[name],
    focus: () => {},
    querySelector: () => null,
    querySelectorAll: () => [],
    addEventListener: (kind, handler) => node.listeners.set(kind, handler),
    dispatchEvent: (event) => node.listeners.get(event.type)?.(event),
    getBoundingClientRect: () => ({ width: 400, height: 200 }),
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
function nodeFor(id) {
  if (!nodes.has(id)) { nodes.set(id, makeNode()); }
  return nodes.get(id);
}

globalThis.document = {
  getElementById: (id) => nodeFor(id),
  querySelector: (sel) => nodeFor(`sel:${sel}`),
  querySelectorAll: () => [],
  addEventListener: () => {},
  createElement: (tag) => makeNode(tag),
};

globalThis.getComputedStyle = () => ({ lineHeight: '17' });

// 附件模块用 FileReader 解码粘贴进来的文件。同步回调即可，
// 本文件关心的是「哪张图跟着哪条消息走」，不是解码本身。
globalThis.FileReader = class {
  readAsDataURL(file) {
    this.result = `data:image/png;base64,${file.name}`;
    this.onload?.({ target: this });
  }

  readAsText(file) {
    this.result = file.textContent ?? '';
    this.onload?.({ target: this });
  }
};

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

const send = nodeFor('send');
const composer = nodeFor('composer');

const tick = () => new Promise((resolve) => setImmediate(resolve));
const click = () => send.listeners.get('click')?.({});
const notifyInput = () => composer.listeners.get('input')?.({});

/** 已发出的 chat.send，按顺序。 */
const sends = () => posted.filter((m) => m.channel === 'chat.send');

/** 已回应的请求标识。桥按 id 配对，未回应的就是还在途的那一轮。 */
const answered = new Set();

/** 让某个请求收束，模拟加载项跑完一轮。 */
function respond(id, data = { completed: true }) {
  answered.add(id);
  messageListener?.({ data: { kind: 'response', id, ok: true, data } });
}

/** 在途的 chat.send 条数。闸门有效时它永远不该超过 1。 */
const inFlight = () => sends().filter((m) => !answered.has(m.id)).length;

/** 提交一句话。走的是输入框加按钮，与用户操作同一路径。 */
function submitText(text) {
  composer.value = text;
  notifyInput();
  click();
}

/** 造一个假的图片文件。名字同时充当解码后的 dataUrl 尾巴，便于断言是哪张。 */
const pngFile = (name) => ({
  name,
  type: 'image/png',
  size: 1024,
  lastModified: 0,
});

/** 粘贴一张图进输入框。附件模块监听的是 composer 的 paste。 */
async function pasteImage(name) {
  composer.listeners.get('paste')?.({
    clipboardData: { files: [pngFile(name)] },
    preventDefault: () => {},
  });
  // 附件受理是异步的（读文件、查上限），等它落地。
  await tick();
  await tick();
}

console.log('一、轮转闸门：任何时刻只有一轮在途');

const peak = { value: 0 };
const sample = () => { peak.value = Math.max(peak.value, inFlight()); };

submitText('第一条');
await tick();
sample();

check('第一条立即发出', sends().length === 1, `已发 ${sends().length}`);
check('第一条在途', inFlight() === 1, `在途 ${inFlight()}`);

// 处理中连投两条：都该排队，不该并发。
submitText('第二条');
await tick();
sample();
submitText('第三条');
await tick();
sample();

check('排队期间没有并发发送', sends().length === 1, `已发 ${sends().length}`);

// 逐条放行，每次只让一轮收束，并在每步取样在途数。
// 闸门失效时，一轮收束会让两条链同时往下走，这里就会量到 2。
respond(sends()[0].id);
await tick();
sample();
await tick();
sample();

check('第一轮结束后第二条自动发出', sends().length === 2, `已发 ${sends().length}`);
check('第二条发出时第一条已收束', inFlight() === 1, `在途 ${inFlight()}`);

respond(sends()[1].id);
await tick();
sample();
await tick();
sample();

check('第二轮结束后第三条自动发出', sends().length === 3, `已发 ${sends().length}`);

respond(sends()[2].id);
await tick();
await tick();
sample();

check('队列排空后不再发送', sends().length === 3, `已发 ${sends().length}`);
check('全程在途峰值为 1', peak.value === 1, `峰值 ${peak.value}`);

const texts = sends().map((m) => m.payload.text);
check('三条按提交顺序发出', texts.join('|') === '第一条|第二条|第三条', texts.join('|'));
check('没有重复发送', new Set(texts).size === texts.length, texts.join('|'));
check('排空后按钮回到发送', send.getAttribute('aria-label') === '发送', send.getAttribute('aria-label'));
check('排空后不再是忙态', !send.classes.has('is-busy'));

console.log('');
console.log('二、附件归属在入队时就定下来');

// 先占住一轮，让后续提交必然走排队路径。
submitText('占位的一轮');
await tick();
const holdId = sends()[3].id;
check('占位轮已在途', inFlight() === 1, `在途 ${inFlight()}`);

// 甲图进输入框，连同文字入队。
await pasteImage('甲.png');
submitText('带甲图');
await tick();

check('带甲图这条已入队而非发出', sends().length === 4, `已发 ${sends().length}`);

// 入队后再粘乙图。它属于下一条，绝不能追加到已排队的那条上。
await pasteImage('乙.png');
submitText('带乙图');
await tick();

check('带乙图这条也已入队', sends().length === 4, `已发 ${sends().length}`);

// 放行占位轮，让两条排队的依次发出。
respond(holdId);
await tick();
await tick();
respond(sends()[4].id);
await tick();
await tick();
respond(sends()[5].id);
await tick();

const byText = (text) => sends().find((m) => m.payload.text === text)?.payload;
const first = byText('带甲图');
const second = byText('带乙图');

check('两条排队消息都已发出', Boolean(first) && Boolean(second),
  `甲=${Boolean(first)} 乙=${Boolean(second)}`);
check('带甲图这条只带一张图', first?.images.length === 1, JSON.stringify(first?.images?.length));
check('带甲图这条带的正是甲图', first?.images[0]?.name === '甲.png', JSON.stringify(first?.images));
check('入队后新粘的乙图没被追加进甲那条',
  (first?.images ?? []).every((i) => i.name !== '乙.png'), JSON.stringify(first?.images));
check('带乙图这条只带一张图', second?.images.length === 1, JSON.stringify(second?.images?.length));
check('带乙图这条带的正是乙图', second?.images[0]?.name === '乙.png', JSON.stringify(second?.images));
check('每条都带 files 字段（即便为空）',
  Array.isArray(first?.files) && Array.isArray(second?.files),
  `${JSON.stringify(first?.files)} / ${JSON.stringify(second?.files)}`);

console.log('');
console.log(`=== 输入队列：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
