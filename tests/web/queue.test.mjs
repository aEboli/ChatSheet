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
// 顺带锁住排队条与对话流的交接：排队中的条目只在排队条上，开跑时才挪进对话流，
// 同一条不该两处都在。
//
// 前两条都用变异验证过：把闸门条件改成永假会让本文件报「并发」「重复发送」；
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

// 假 DOM。className 与 classList 共用一个集合，remove() 真的把节点从父节点摘掉，
// 理由与 send-stop.test.mjs 里同名函数的注释一致：排队条是整条重画的，
// 这两处做虚了断言就测不出东西。
function makeNode(tag = 'div') {
  const node = {
    tag,
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
    parent: null,
    listeners: new Map(),
    classes: new Set(),
    append: (...kids) => {
      for (const kid of kids) {
        if (kid && typeof kid === 'object') { kid.parent = node; }
        node.children.push(kid);
      }
    },
    remove: () => {
      node.removed = true;
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
const transcript = nodeFor('transcript');
const strip = nodeFor('queue-strip');

// 排队条的滚动坐标系：column-reverse 下范围是负的，0 是队首（第 1 位）那一端，
// 早排的那几条在负值处。实测于 WebView2 同源的 Chromium，量具见本次改动记录。
// 重画（replaceChildren）不会清掉滚动位置，因此断言时先把它滑开再看归位。
const SCROLLED_AWAY = -63;

/** 排队条上的条目，与对话流里的用户气泡。同一条不该同时出现在两处。 */
const chips = () => strip.children.filter((n) => n.classes?.has('queue-chip'));
const chipText = (chip) =>
  chip.children.find((n) => n.classes?.has('queue-chip-text'))?.textContent ?? '';
const userBubbles = () => transcript.children.filter((n) => n.classes?.has('msg-user'));

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
check('两条排队内容都在排队条上', chips().length === 2, `排队条 ${chips().length} 条`);
check('排队条按提交顺序排列', chips().map(chipText).join('|') === '第二条|第三条',
  chips().map(chipText).join('|'));
check('位次从 1 起连续编号',
  chips().map((c) => c.children[0]?.textContent).join('|') === '1|2',
  chips().map((c) => c.children[0]?.textContent).join('|'));
// 正在跑的那条已进对话流，排队的两条还没有：同一条不该两处都在。
check('只有已开跑的那条进了对话流', userBubbles().length === 1,
  `用户气泡 ${userBubbles().length} 个`);

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
check('排空后排队条为空', chips().length === 0, `排队条 ${chips().length} 条`);
check('排空后排队条收起', strip.hidden === true, `hidden=${strip.hidden}`);
// 三条都跑过，因此三条都该在对话流里——排队条上的条目开跑时会挪进去。
check('三条都已挪进对话流', userBubbles().length === 3, `用户气泡 ${userBubbles().length} 个`);

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
console.log('三、取消不留痕，重画后视口对着队首');

// 到这里已经跑完六轮，对话流里有六个用户气泡。以此为基准，
// 后面断言的是「取消掉的条目一个都没往这里加」。
const bubblesBefore = userBubbles().length;
check('前六轮都在对话流里', bubblesBefore === 6, `用户气泡 ${bubblesBefore} 个`);

// 再占一轮，然后排四条：超过一次能显示的三条，重画后的归位才有意义。
submitText('第三节占位');
await tick();
const holdId2 = sends()[6].id;
for (const n of [1, 2, 3, 4]) {
  submitText(`排队第${n}条`);
  await tick();
}

check('四条都进了队列', chips().length === 4, `排队条 ${chips().length} 条`);
check('占位轮已上屏，排队的四条还没有', userBubbles().length === bubblesBefore + 1,
  `用户气泡 ${userBubbles().length} 个`);

// 先滑到早排的那端，再让队列变化触发重画：重画不清滚动位置，
// 不归位就会一直停在那里，用户看不到下一个要发的那条。
strip.scrollTop = SCROLLED_AWAY;
submitText('排队第5条');
await tick();

check('入队重画后视口归到队首那一端', strip.scrollTop === 0,
  `scrollTop=${strip.scrollTop}`);

check('五条都在队列里', chips().length === 5, `排队条 ${chips().length} 条`);

// 取消队列中间那条：只掉它自己，剩下的重新编号，且对话流一个字都不多。
// 同样先滑开，确认取消引起的重画也会归位。
const cancelButton = (chip) =>
  chip.children.find((n) => n.classes?.has('queue-chip-cancel'));
strip.scrollTop = SCROLLED_AWAY;
cancelButton(chips()[1])?.listeners.get('click')?.({});
await tick();

check('取消后队列少一条', chips().length === 4, `排队条 ${chips().length} 条`);
check('取消掉的正是那一条',
  chips().map(chipText).join('|') === '排队第1条|排队第3条|排队第4条|排队第5条',
  chips().map(chipText).join('|'));
check('剩下的重新连续编号',
  chips().map((c) => c.children[0]?.textContent).join('|') === '1|2|3|4',
  chips().map((c) => c.children[0]?.textContent).join('|'));
check('取消掉的没进对话流', userBubbles().length === bubblesBefore + 1,
  `用户气泡 ${userBubbles().length} 个`);
check('取消重画后视口也归到队首那一端', strip.scrollTop === 0,
  `scrollTop=${strip.scrollTop}`);

// 停止把剩下四条一并取消。同样不留痕：它们从未发出。
const sentBefore = sends().length;
click();
await tick();
respond(holdId2);
await tick();
await tick();

check('停止清空了队列', chips().length === 0, `排队条 ${chips().length} 条`);
check('停止取消的四条没进对话流', userBubbles().length === bubblesBefore + 1,
  `用户气泡 ${userBubbles().length} 个`);
check('停止取消的四条一条也没发出', sends().length === sentBefore,
  `已发 ${sends().length}，此前 ${sentBefore}`);
check('取消掉的原文不出现在对话流里',
  !transcript.children.some((n) =>
    String(n.children?.[0]?.children?.[0]?.textContent ?? '').startsWith('排队第')),
  transcript.children.map((n) => n.children?.[0]?.children?.[0]?.textContent).join('|'));

console.log('');
console.log(`=== 输入队列：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
