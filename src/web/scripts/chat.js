import { request, on, isHosted, logToHost } from './bridge.js';
import { renderMarkdown } from './markdown.js';
import { initPicker, syncPicker } from './picker.js';
import { describeRange, rangeLabel } from './range-label.js';
import {
  initAttachments,
  getImages,
  getFiles,
  hasAttachments,
  clearAttachments,
  createFileGlyph,
} from './attachments.js';

const TOOL_LABELS = {
  get_workbook_info: '读取工作簿结构',
  get_selection: '读取当前选区',
  read_range: '读取范围',
  write_values: '写入值',
  write_formulas: '写入公式',
  format_range: '设置格式',
  set_number_format: '设置数字格式',
  autofit_range: '自动调整',
  fit_range: '适配',
  merge_cells: '合并单元格',
  unmerge_cells: '取消合并',
  clear_range: '清除',
  add_worksheet: '新增工作表',
  rename_worksheet: '重命名工作表',
  sort_range: '排序',
  create_table: '创建表格',
  create_chart: '创建图表',
};

const RISK_LABELS = {
  Read: '读取',
  Write: '修改内容',
  Structure: '改变结构',
};

/**
 * 值为 A1 地址的字段。展示时在原值后补一句「几行 × 哪几列」。
 *
 * 按字段名而非按值形态判断：像 format_code 的 0.00 或 key_column 的 B
 * 本身也能被当作地址解析，误译成位置说明反而更难懂。
 */
const ADDRESS_KEYS = new Set(['range', 'address', 'source_range']);

let transcript;
let composer;
let sendButton;
let statusLine;
let usageLine;
let queueStrip;

let busy = false;
let currentAssistant = null;
let currentThinking = null;

/**
 * 待处理的输入队列。
 *
 * 为什么要队列：加载项同一时刻只跑一轮（后端见 chat.send 的 BUSY 守卫），
 * 早先的做法是处理中直接禁用输入框，于是想到的下一步只能干等或记在别处。
 * 改为入队后，输入随时可写，上一轮结束即自动接着跑。
 *
 * 队列留在面板侧而非加载项：排队中的条目要能看见、能取消，
 * 这两件事都是界面的事；后端多一个队列反而要再开一套查询与撤单通道。
 * 代价是刷新面板会丢掉未发出的排队项，但那与丢掉输入框里的草稿同级。
 *
 * 排队条目显示在输入区上方的排队条里（见 renderQueueStrip），只有真正开跑时
 * 才进对话流。早先的做法是入队即上屏成气泡，实测的问题是：还没发生的事混在
 * 已发生的消息之间会被当成已经处理过，而且对话一长就被顶出可视区——
 * 用户想确认「刚排的那条还在不在」反而要往上翻。
 *
 * 同理，取消掉的条目直接消失（见 cancelQueued）：它从未发出，对话流里不该有它。
 */
const queue = [];
let queueSequence = 0;

/** 正在轮转队列。防止多个入口并发触发同一条队列。 */
let pumping = false;

function toolLabel(name) {
  return TOOL_LABELS[name] ?? name;
}

function scrollToBottom() {
  // 用 scrollTop 而非 scrollIntoView：后者在窄栏里会带偏水平位置。
  transcript.scrollTop = transcript.scrollHeight;
}

/** 正在处理的助手气泡。同一时刻只会有一个。 */
let pendingBubble = null;

/**
 * 在对话流末尾显示一个「正在处理」的助手气泡。
 *
 * 放在回复气泡里而不是底部状态行：答案将出现的位置就是用户目光所在，
 * 进展写在那里不必来回扫视；底部那行留给错误与配置提示。
 * 气泡本身会被后续流式文本直接接管（见 ensureAssistant），
 * 因此从「正在处理」到正文是同一个气泡在原地变化，不会闪一下再重排。
 */
function showPending(label = '正在处理…') {
  if (pendingBubble) {
    pendingBubble.label.textContent = label;
    // 重新追加到末尾：工具卡片等内容可能已插到它后面。
    transcript.append(pendingBubble.wrapper);
    scrollToBottom();
    return;
  }

  const wrapper = document.createElement('div');
  wrapper.className = 'msg msg-assistant msg-pending';

  const body = document.createElement('div');
  body.className = 'msg-body';

  const indicator = document.createElement('div');
  indicator.className = 'pending';

  const dots = document.createElement('span');
  dots.className = 'pending-dots';
  dots.setAttribute('aria-hidden', 'true');
  for (let i = 0; i < 3; i++) {
    dots.append(document.createElement('i'));
  }

  const text = document.createElement('span');
  text.className = 'pending-label';
  text.textContent = label;

  // 进展变化要让读屏软件播报，但不能打断用户当前操作。
  indicator.setAttribute('role', 'status');
  indicator.setAttribute('aria-live', 'polite');

  indicator.append(dots, text);
  body.append(indicator);
  wrapper.append(body);
  transcript.append(wrapper);
  scrollToBottom();

  pendingBubble = { wrapper, body, indicator, label: text };
}

function clearPending() {
  if (!pendingBubble) {
    return;
  }

  pendingBubble.wrapper.remove();
  pendingBubble = null;
}

/**
 * 造一个消息气泡，但不插入对话流。
 *
 * 与 addBubble 分开，是因为排队中的用户消息需要拿到气泡外框本身——
 * 队列标记与取消按钮挂在外框上，而 addBubble 只交出文本容器。
 */
function buildBubble(role, text, images = [], files = []) {
  const wrapper = document.createElement('div');
  wrapper.className = `msg msg-${role}`;

  const body = document.createElement('div');
  body.className = 'msg-body';

  // 图片放在文字之上，与发送给模型的顺序一致。
  if (images.length > 0) {
    const gallery = document.createElement('div');
    gallery.className = 'msg-images';
    for (const image of images) {
      const thumb = document.createElement('img');
      thumb.className = 'msg-image';
      thumb.src = image.dataUrl;
      thumb.alt = image.name ?? '图片';
      thumb.title = image.name ?? '图片';
      gallery.append(thumb);
    }

    body.append(gallery);
  }

  // 文件只列名字，不铺内容：一个几百行的 CSV 铺在气泡里会把对话顶走，
  // 而用户已经知道自己附了什么，需要的只是「确实附上了」这个确认。
  //
  // 带图标，且与输入区附件条用的是同一个（createFileGlyph）。此前这里只有
  // 纯文字，同一个文件发送前带图标、发送后成了灰色文字胶囊，看着像两种东西。
  if (files.length > 0) {
    const list = document.createElement('div');
    list.className = 'msg-files';
    for (const file of files) {
      const chip = document.createElement('span');
      chip.className = 'msg-file';

      const label = document.createElement('span');
      label.className = 'msg-file-name';
      label.textContent = file.name ?? '文件';

      chip.append(createFileGlyph('file-glyph'), label);
      chip.title = `${file.name ?? '文件'}（内容已随消息发送）`;
      list.append(chip);
    }

    body.append(list);
  }

  // 助手气泡即使初始为空也要建文本容器：流式增量要往里写。
  if (text || role === 'assistant') {
    const textNode = document.createElement('div');
    textNode.className = 'msg-text';
    if (role === 'user') {
      textNode.textContent = text ?? '';
    } else {
      textNode.innerHTML = renderMarkdown(text ?? '');
    }

    body.append(textNode);
  }

  wrapper.append(body);
  // 助手气泡的流式更新需要拿到文本容器本身。
  return { wrapper, body, text: body.querySelector('.msg-text') ?? body };
}

function addBubble(role, text, images = [], files = []) {
  const bubble = buildBubble(role, text, images, files);
  transcript.append(bubble.wrapper);
  scrollToBottom();
  return bubble.text;
}

/**
 * 取当前助手气泡，没有就建一个。
 *
 * 优先接管「正在处理」气泡：用户已经在看那个位置，就地换成正文
 * 比另起一个气泡更连贯，也少一次重排。
 */
function ensureAssistant() {
  if (currentAssistant) {
    return currentAssistant;
  }

  if (pendingBubble) {
    const { wrapper, body } = pendingBubble;
    pendingBubble = null;
    wrapper.classList.remove('msg-pending');
    body.replaceChildren();

    const textNode = document.createElement('div');
    textNode.className = 'msg-text';
    body.append(textNode);

    currentAssistant = { element: textNode, raw: '' };
    return currentAssistant;
  }

  currentAssistant = { element: addBubble('assistant', ''), raw: '' };
  return currentAssistant;
}

function appendAssistantText(delta) {
  const target = ensureAssistant();
  target.raw += delta;
  // 每次增量都整体重渲染：Markdown 结构会随新内容变化，
  // 增量拼 HTML 会在列表和表格上出错。文本量在单轮内可控。
  target.element.innerHTML = renderMarkdown(target.raw);
  scrollToBottom();
}

function appendThinking(delta) {
  if (!currentThinking) {
    const wrapper = document.createElement('details');
    wrapper.className = 'thinking';
    const summary = document.createElement('summary');
    summary.textContent = '思考过程';
    const body = document.createElement('div');
    body.className = 'thinking-body';
    wrapper.append(summary, body);
    transcript.append(wrapper);
    currentThinking = { element: body, raw: '' };
  }

  currentThinking.raw += delta;
  currentThinking.element.textContent = currentThinking.raw;
  scrollToBottom();
}

/**
 * 添加工具操作卡片，默认折叠。
 *
 * 用 details/summary 而非自制折叠：原生元素自带键盘操作与无障碍语义，
 * 且折叠状态由浏览器维护，不需要额外状态管理。
 *
 * 默认折叠的理由：一轮任务常有多次工具调用，全部展开会把对话正文
 * 挤到看不见；摘要行已包含工具名与结果，需要细节时再展开。
 */
function addToolCard(payload) {
  const card = document.createElement('details');
  card.className = payload.manual ? 'tool-card is-manual' : 'tool-card';
  card.dataset.toolId = payload.id ?? '';

  const head = document.createElement('summary');
  head.className = 'tool-head';

  const name = document.createElement('span');
  name.className = 'tool-name';
  name.textContent = toolLabel(payload.name);

  head.append(name);

  // 手动操作加一枚标记。
  //
  // 只靠边条颜色不够：颜色说不出区别在哪，色觉障碍下也可能根本看不出来。
  // 标记与卡片同宽同高，折叠时也在，是这两类操作唯一始终可读的差别。
  if (payload.manual) {
    const origin = document.createElement('span');
    origin.className = 'tool-origin';
    origin.textContent = '手动';
    origin.title = '你在面板上点按钮直接执行的，不是模型发起的';
    head.append(origin);
  }

  const state = document.createElement('span');
  state.className = 'tool-state';
  state.textContent = '执行中…';

  // 撤销按钮占位，执行成功且可撤销时才填充。
  // 放在摘要行右端：与操作本身同处一行，不必展开就能撤销。
  const actions = document.createElement('span');
  actions.className = 'tool-actions';

  head.append(state, actions);

  const body = document.createElement('div');
  body.className = 'tool-body';

  const argsTitle = document.createElement('div');
  argsTitle.className = 'tool-section-title';
  argsTitle.textContent = '参数';

  const args = document.createElement('pre');
  args.className = 'tool-args';
  args.textContent = summarizeArgs(payload.args) || '（无参数）';

  body.append(argsTitle, args);
  card.append(head, body);
  transcript.append(card);
  scrollToBottom();
  return card;
}

/**
 * 描述二维数据的尺寸。
 *
 * 一律带「共」字：尺寸与位置说明的措辞必须能区分，
 * 否则「3 行 × 2 列」既可能指第 1-3 行，也可能指三行数据。
 */
function describeMatrixSize(value) {
  if (!Array.isArray(value[0])) {
    // 一维数组不是矩阵，按行列描述会凭空造出「1 列」。
    return `共 ${value.length} 项`;
  }

  return `共 ${value.length} 行 × ${value[0].length} 列`;
}

/** 地址字段的展示文本：保留原值，并在括号里给出位置说明。 */
function describeAddressValue(text) {
  const label = rangeLabel(text);
  return label === '' ? text : `${text}（${label}）`;
}

/** 参数摘要：完整 JSON 在窄栏里会挤占太多空间，只展示关键字段。 */
function summarizeArgs(args) {
  if (!args || typeof args !== 'object') {
    return '';
  }

  const parts = [];
  for (const [key, value] of Object.entries(args)) {
    if (value === null || value === undefined || value === '') {
      continue;
    }

    if (Array.isArray(value)) {
      parts.push(`${key}: ${describeMatrixSize(value)}`);
      continue;
    }

    const text = String(value);
    if (ADDRESS_KEYS.has(key)) {
      parts.push(`${key}: ${describeAddressValue(text)}`);
      continue;
    }

    parts.push(`${key}: ${text.length > 60 ? `${text.slice(0, 60)}…` : text}`);
  }

  return parts.join('\n');
}

function finishToolCard(payload) {
  const card = transcript.querySelector(`.tool-card[data-tool-id="${payload.id}"]`);
  if (!card) {
    return;
  }

  fillToolCard(card, payload);
}

/**
 * 把结果填进一张已经在屏上的卡片。
 *
 * 与 finishToolCard 分开是为了面板直接发起的操作：它们的撤销标识要等宿主
 * 执行完才知道，没法在开跑时就按标识把卡片找回来，但卡片引用一直在手里。
 */
function fillToolCard(card, payload) {
  const state = card.querySelector('.tool-state');
  if (payload.ok) {
    state.textContent = describeSuccess(payload.data);
    state.className = 'tool-state is-ok';
  } else {
    state.textContent = payload.error ?? '失败';
    state.className = 'tool-state is-error';
    card.classList.add('is-error');
    // 失败时自动展开：出错的细节是用户当下最需要看到的。
    card.open = true;
  }

  appendToolDetail(card, payload);

  if (payload.canUndo) {
    attachUndoButton(card, payload);
  }
}

/**
 * 给操作卡片挂上撤销按钮，撤销后原地变为「恢复」。
 *
 * 同一个按钮承担两个方向，而不是并排放两个：同一时刻只有一个动作有效，
 * 放两个会让另一个始终处于禁用状态，反而不易看懂当前处于哪个状态。
 */
function attachUndoButton(card, payload) {
  const actions = card.querySelector('.tool-actions');
  if (!actions) {
    return;
  }

  actions.replaceChildren();

  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'tool-undo';
  button.dataset.undone = 'false';
  button.textContent = '撤销';

  // 撤销说明里带的是原始地址，同样补上行列位置：
  // 悬停就能确认要还原的是哪几行哪几列，不必先展开卡片。
  const where = rangeLabel(payload.data?.address ?? payload.data?.source_range ?? '');
  if (payload.undoSummary) {
    button.title = where === ''
      ? `撤销：${payload.undoSummary}`
      : `撤销：${payload.undoSummary}（${where}）`;
  } else {
    button.title = '撤销此操作';
  }

  button.addEventListener('click', async (event) => {
    // 阻止冒泡：按钮在 summary 内，点击会连带折叠或展开卡片。
    event.preventDefault();
    event.stopPropagation();

    const redo = button.dataset.undone === 'true';
    button.disabled = true;
    const original = button.textContent;
    button.textContent = redo ? '恢复中…' : '撤销中…';

    try {
      const result = await request('undo.apply', { id: payload.id, redo });

      if (!result?.ok) {
        button.textContent = original;
        addNotice(result?.message ?? '操作失败。', 'error');
        return;
      }

      const undone = result.undone === true;
      button.dataset.undone = undone ? 'true' : 'false';
      button.textContent = undone ? '恢复' : '撤销';
      card.classList.toggle('is-undone', undone);

      const badge = card.querySelector('.tool-state');
      if (badge) {
        if (undone) {
          badge.dataset.original = badge.dataset.original ?? badge.textContent;
          badge.textContent = '已撤销';
          badge.className = 'tool-state is-undone';
        } else if (badge.dataset.original) {
          badge.textContent = badge.dataset.original;
          badge.className = 'tool-state is-ok';
        }
      }
    } catch (error) {
      button.textContent = original;
      addNotice(`操作失败：${error.message}`, 'error');
    } finally {
      button.disabled = false;
    }
  });

  actions.append(button);
}

/** 把执行结果追加到卡片折叠区，展开后可看到完整细节。 */
function appendToolDetail(card, payload) {
  const body = card.querySelector('.tool-body');
  if (!body) {
    return;
  }

  const title = document.createElement('div');
  title.className = 'tool-section-title';
  title.textContent = payload.ok ? '结果' : '失败原因';

  const detail = document.createElement('pre');
  detail.className = 'tool-args';

  if (payload.ok) {
    detail.textContent = describeResultDetail(payload.data);
  } else {
    detail.textContent = payload.errorCode
      ? `${payload.errorCode}\n${payload.error ?? ''}`
      : (payload.error ?? '未提供原因');
  }

  body.append(title, detail);
}

/**
 * 展开后展示的结果细节。
 * 原始 JSON 里的读回校验数据可能很长，这里按字段整理并截断，
 * 既能看清改了什么，又不会让卡片撑满整屏。
 */
function describeResultDetail(data) {
  if (!data || typeof data !== 'object') {
    return '（无返回数据）';
  }

  const lines = [];
  for (const [key, value] of Object.entries(data)) {
    if (value === null || value === undefined) {
      continue;
    }

    if (Array.isArray(value)) {
      const preview = JSON.stringify(value.slice(0, 3));
      lines.push(`${key}: ${describeMatrixSize(value)}`);
      lines.push(`  前几行 ${preview.length > 200 ? `${preview.slice(0, 200)}…` : preview}`);
      continue;
    }

    const text = String(value);
    if (ADDRESS_KEYS.has(key)) {
      lines.push(`${key}: ${describeAddressValue(text)}`);
      continue;
    }

    lines.push(`${key}: ${text.length > 200 ? `${text.slice(0, 200)}…` : text}`);
  }

  return lines.length > 0 ? lines.join('\n') : '（无返回数据）';
}

/**
 * 成功时展示影响面而非原始数据，用户关心的是改了哪里、改了多少。
 *
 * 位置放在前面：单元格数量能说明规模，但说不清落在哪几行哪几列，
 * 而后者才是用户核对结果时第一眼要找的。
 */
function describeSuccess(data) {
  if (!data || typeof data !== 'object') {
    return '完成';
  }

  const where = rangeLabel(data.address ?? data.source_range ?? '');

  if (typeof data.cells_written === 'number') {
    return where === ''
      ? `已写入 ${data.cells_written} 个单元格`
      : `已写入 ${where}，共 ${data.cells_written} 个单元格`;
  }
  if (typeof data.cells_affected === 'number') {
    return where === ''
      ? `影响 ${data.cells_affected} 个单元格`
      : `影响 ${where}，共 ${data.cells_affected} 个单元格`;
  }
  if (typeof data.rows === 'number' && typeof data.columns === 'number') {
    return where === ''
      ? `共 ${data.rows} 行 × ${data.columns} 列`
      : `${where}，共 ${data.rows * data.columns} 个单元格`;
  }
  if (data.created_sheet) {
    return `已创建 ${data.created_sheet}`;
  }
  if (data.new_name) {
    return `已改名为 ${data.new_name}`;
  }
  if (data.table_name) {
    return `已创建表格 ${data.table_name}`;
  }
  // 自动调整、创建图表这类结果没有计数字段，至少说明作用位置。
  return where === '' ? '完成' : where;
}

/**
 * 审批卡片的影响范围说明。
 *
 * 加载项探到具体范围时回传 impactRange（表名、地址、单元格数），
 * 由这里组装文案：位置说明与工具卡片同源，两处措辞才不会一处说位置、
 * 一处说尺寸。探测失败或操作本就没有范围（如新增工作表）时，
 * 退回加载项给的 impact 文本。
 */
function describeImpact(message) {
  const target = message.impactRange;
  if (!target || !target.address) {
    return message.impact ?? '';
  }

  const where = describeRange(target.address);
  const prefix = target.sheet ? `${target.sheet} 的 ` : '';
  const size = typeof target.cells === 'number' ? `，共 ${target.cells} 个单元格` : '';
  return `${prefix}${where}${size}`;
}

function addApprovalCard(message) {
  const card = document.createElement('div');
  card.className = 'approval';

  const title = document.createElement('div');
  title.className = 'approval-title';
  title.textContent = `需要确认：${toolLabel(message.tool)}`;

  const risk = document.createElement('div');
  risk.className = 'approval-risk';
  risk.textContent = RISK_LABELS[message.risk] ?? message.risk;

  const impact = document.createElement('div');
  impact.className = 'approval-impact';
  const impactText = describeImpact(message);
  impact.textContent = impactText ? `影响范围：${impactText}` : '';

  const args = document.createElement('pre');
  args.className = 'approval-args';
  args.textContent = summarizeArgs(message.args);

  const actions = document.createElement('div');
  actions.className = 'approval-actions';

  const approve = document.createElement('button');
  approve.type = 'button';
  approve.className = 'btn btn-primary';
  approve.textContent = '允许';

  const approveAll = document.createElement('button');
  approveAll.type = 'button';
  approveAll.className = 'btn';
  approveAll.textContent = '本轮全部允许';

  const reject = document.createElement('button');
  reject.type = 'button';
  reject.className = 'btn btn-danger';
  reject.textContent = '拒绝';

  const settle = async (approved, approveRest) => {
    approve.disabled = true;
    approveAll.disabled = true;
    reject.disabled = true;

    try {
      await request('approval.respond', { id: message.id, approved, approveRest });
      // 用户已决定，处理随即继续，重新显示进展。
      showPending();
      const outcome = document.createElement('div');
      outcome.className = approved ? 'approval-outcome is-ok' : 'approval-outcome is-error';
      outcome.textContent = approved ? (approveRest ? '已允许，本轮后续不再询问' : '已允许') : '已拒绝';
      actions.replaceWith(outcome);
    } catch (error) {
      addNotice(`回复审批失败：${error.message}`, 'error');
    }
  };

  approve.addEventListener('click', () => void settle(true, false));
  approveAll.addEventListener('click', () => void settle(true, true));
  reject.addEventListener('click', () => void settle(false, false));

  actions.append(approve, approveAll, reject);

  // 等用户决定期间撤掉进展指示器：此刻真正在等的是用户，
  // 旁边还跳着「正在处理」既不准确，也会分散对审批卡片的注意力。
  clearPending();

  card.append(title, risk);
  if (impactText) { card.append(impact); }
  if (args.textContent) { card.append(args); }
  card.append(actions);

  transcript.append(card);
  scrollToBottom();
}

/**
 * 在对话流中间插入一条居中的胶囊提示。
 *
 * 不做成气泡：错误与系统提示不是对话中任何一方说的话，套上气泡会让人
 * 误当成模型的回复——错误文本里常常就带着模型口吻。居中胶囊是系统级
 * 消息的通行表达，与左右两侧的对话一眼可分。
 */
function addNotice(text, variant = 'info') {
  const notice = document.createElement('div');
  notice.className = `notice notice-${variant}`;
  notice.textContent = text;
  transcript.append(notice);

  // 纯圆角只适合单行：文字换行后首末行会被挤进弧内。
  // 必须等插入文档后才能量到真实行数。
  markMultiline(notice);

  scrollToBottom();
  return notice;
}

/** 水平对齐的三个选项。顺序与浮层里的 DOM 一致，默认取 center。 */
const FIT_ALIGNMENTS = {
  left: '靠左',
  center: '居中',
  right: '靠右',
};

/**
 * 当前选中的水平对齐。只存在会话内：适配是即时动作，
 * 记住上次选择方便连续排版，但不值得为它写一条持久设置。
 */
let fitAlignment = 'center';

/**
 * 适配按钮与对齐浮层。
 *
 * 浮层而非「先适配再调整」：对齐要在动手前定，事后改就变成两次操作、
 * 两条撤销记录。悬停与点击都能展开——悬停顺手，点击则照顾键盘与触摸。
 */
function initFit() {
  const wrap = document.getElementById('fit-wrap');
  const button = document.getElementById('fit');
  const pop = document.getElementById('fit-pop');
  if (!wrap || !button || !pop) {
    return;
  }

  const items = [...pop.querySelectorAll('.fit-item')];

  const isOpen = () => !pop.hidden;

  const setOpen = (open) => {
    pop.hidden = !open;
    button.setAttribute('aria-expanded', open ? 'true' : 'false');
    if (open) {
      markActive();
    }
  };

  // 标出当前选项。三选一里「现在是哪个」是用户最先要看的信息。
  const markActive = () => {
    for (const item of items) {
      const active = item.dataset.align === fitAlignment;
      item.classList.toggle('is-active', active);
      item.setAttribute('aria-checked', active ? 'true' : 'false');
    }
  };

  markActive();

  // 悬停展开。离开容器才收起：浮层紧贴按钮且有透明补丁接缝，
  // 鼠标从按钮移向选项的途中不会误判为移开。
  wrap.addEventListener('mouseenter', () => setOpen(true));
  wrap.addEventListener('mouseleave', () => setOpen(false));

  button.addEventListener('click', () => setOpen(!isOpen()));

  for (const item of items) {
    item.addEventListener('click', () => {
      const align = item.dataset.align;
      if (!FIT_ALIGNMENTS[align]) {
        return;
      }

      fitAlignment = align;
      markActive();
      setOpen(false);
      void runFit(align);
    });
  }

  // 点击浮层外部关闭。捕获阶段以免被内部 stopPropagation 阻断。
  document.addEventListener(
    'click',
    (event) => {
      if (isOpen() && !wrap.contains(event.target)) {
        setOpen(false);
      }
    },
    true,
  );

  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape' && isOpen()) {
      setOpen(false);
      button.focus();
    }
  });
}

/** 手动操作卡片的临时标识计数。撤销标识要等宿主执行完才知道。 */
let fitSequence = 0;

/**
 * 执行一次适配。
 *
 * 不进对话历史：这是确定性的排版动作，点按钮已经表达了意图，模型不必知道。
 * 但在对话流里按工具卡片呈现——它和模型发起的写入是同一类事（改了哪个范围、
 * 影响多少格、能不能撤销），拆成两种样式只会让人对着两处找同一种信息。
 * 区别靠「手动」标记和边条颜色说明，而不是靠换一套结构。
 *
 * 撤销入口是必须的：加载项经 COM 的写入会清空 Excel 自己的撤销栈，
 * 用户按 Ctrl+Z 是拿不回来的。
 */
async function runFit(alignment) {
  const button = document.getElementById('fit');
  const label = FIT_ALIGNMENTS[alignment] ?? '居中';

  if (button) { button.disabled = true; }
  setStatus(`正在适配当前表（${label}）…`);

  // 卡片先上屏再发请求。整表适配可能跑上一两分钟，这段时间里
  // 摘要行的「执行中…」就是进度反馈，与模型发起的工具一致。
  const card = addToolCard({
    id: `fit-pending-${++fitSequence}`,
    name: 'fit_range',
    args: { horizontal_alignment: alignment },
    manual: true,
  });

  try {
    // 默认 30 秒会误报超时，而宿主那边其实还在正常执行——这种失败最难排查。
    const result = await request(
      'sheet.fit',
      { horizontalAlignment: alignment },
      { timeout: 300000 },
    );

    if (!result?.ok) {
      fillToolCard(card, { ok: false, error: result?.message ?? '适配失败。' });
      return;
    }

    // 撤销要按宿主登记的记录标识来点，卡片的标识随之改写。
    if (result.undoId) {
      card.dataset.toolId = result.undoId;
    }

    const where = rangeLabel(result.address);

    fillToolCard(card, {
      id: result.undoId,
      ok: true,
      data: result,
      canUndo: Boolean(result.undoId),
      undoSummary: `适配 ${where === '' ? (result.address ?? '') : where}`.trim(),
    });

    // 没有撤销入口时把原因写进卡片：只是少个按钮的话看起来像功能坏了，
    // 而它其实是保不住完整快照时的有意取舍。
    if (!result.undoId && result.undoUnavailableReason) {
      appendToolNote(card, result.undoUnavailableReason);
    }
  } catch (error) {
    fillToolCard(card, { ok: false, error: error.message });
  } finally {
    // 结果已经写在卡片上，状态行不再复述一遍。
    setStatus('');
    if (button) { button.disabled = false; }
  }
}

/**
 * 往卡片折叠区追加一段说明文字。
 * 展开才看得到，因此只放「为什么没有某个按钮」这类不影响判断结果的补充。
 */
function appendToolNote(card, text) {
  const body = card.querySelector('.tool-body');
  if (!body) {
    return;
  }

  const note = document.createElement('div');
  note.className = 'tool-note';
  note.textContent = text;
  body.append(note);
}

/** 超过一行时换成圆角块。用行高判断，避免把圆角开得过大导致文字压边。 */
function markMultiline(node) {
  const lineHeight = parseFloat(getComputedStyle(node).lineHeight);
  if (Number.isFinite(lineHeight) && node.clientHeight > lineHeight * 1.6) {
    node.classList.add('is-multiline');
  }
}

/**
 * 底部那行短暂状态。只承担「刚做了什么」这类过渡说明。
 *
 * 错误不走这里：这一行与本轮用量并排，宽度只有半栏，稍长的报错就被挤成
 * 一截看不全的文字；它还会被下一次状态更新直接覆盖，用户回头找不到。
 * 错误一律用 addNotice 插进对话流，与出错的那一步留在同一位置。
 */
function setStatus(text) {
  statusLine.textContent = text ?? '';
  statusLine.className = 'status';
  // 与排队条争位时这一行会被压到只剩两三个字（实测 320px 栏宽下如此），
  // 压缩优先让给状态是有意的——排队项上有取消按钮，裁掉就没法点了。
  // 代价是状态可能读不全，所以原文放进悬停说明。
  statusLine.title = text ?? '';
}

/**
 * 切换忙闲。
 *
 * 发送按钮在忙时不禁用而是改变含义（见 updateSendAffordance）。禁用会让
 * 唯一的中断入口在最需要它的时候没法点——上一版另有一个停止按钮，合并后
 * 若照旧禁用，运行中就完全无从中断了。
 *
 * 输入框也不再禁用：处理中写下一步是常态，写好的内容进队列，
 * 上一轮结束自动接着跑。禁用会把用户逼成「干等」或「记在别处」。
 */
function setBusy(value) {
  busy = value;
  sendButton.classList.toggle('is-busy', value);
  updateSendAffordance();
  if (!value) {
    currentAssistant = null;
    currentThinking = null;
    // 兜底清理：异常路径可能不会走到 turn-complete。
    clearPending();
  }
}

/**
 * 按当前状态给发送按钮定含义。三种：
 *   空闲            → 发送
 *   处理中 + 有输入  → 加入队列
 *   处理中 + 输入为空 → 停止
 *
 * 有输入时让「加入队列」压过「停止」：输入框里有字说明用户正打算安排下一步，
 * 此时点按钮几乎不可能是想中断。而要停止只需清空输入框，代价很小——
 * 反过来把排队藏起来则没有同样便宜的替代入口。
 *
 * 图形跟着含义换（见 app.css 的 is-queueing）：按钮上画什么，必须和点下去
 * 会发生的事一致，否则用户是照着图标点的，含义写在 title 里也来不及看。
 */
function updateSendAffordance() {
  const willQueue = busy && hasComposerContent();
  sendButton.classList.toggle('is-queueing', willQueue);

  if (!busy) {
    sendButton.title = '发送（Enter）';
    sendButton.setAttribute('aria-label', '发送');
    return;
  }

  if (willQueue) {
    const ahead = queue.length;
    sendButton.title = ahead === 0
      ? '正在处理，点击排到下一个（Enter 同样入队）'
      : `正在处理，点击排到第 ${ahead + 1} 位（Enter 同样入队）`;
    sendButton.setAttribute('aria-label', '加入队列');
    return;
  }

  sendButton.title = '正在处理，点击停止';
  sendButton.setAttribute('aria-label', '停止');
}

function updateUsage(payload) {
  if (!payload) { return; }
  const { promptTokens, completionTokens } = payload;
  usageLine.textContent = `本轮 ${promptTokens ?? 0} 入 / ${completionTokens ?? 0} 出`;
}

// 圆环周长，用于按占比设置 stroke-dashoffset。半径 9 → 2πr。
const RING_CIRCUMFERENCE = 2 * Math.PI * 9;

let contextState = null;
let compactPrompted = false;

/**
 * 更新上下文进度圆环。
 *
 * 到达阈值（后端判定，当前为 90%）时变色并提示是否压缩。
 * 阈值判断放在后端，避免界面与压缩逻辑各算一套导致不一致。
 */
function updateContextRing(payload, source = '未标注') {
  if (!payload) { return; }
  contextState = payload;

  const ring = document.getElementById('ring-value');
  const text = document.getElementById('context-percent');
  const button = document.getElementById('context-ring');
  if (!ring || !text || !button) { return; }

  const percent = payload.percent ?? 0;
  const ratio = Math.max(0, Math.min(1, payload.ratio ?? 0));

  ring.style.strokeDasharray = `${RING_CIRCUMFERENCE}`;
  ring.style.strokeDashoffset = `${RING_CIRCUMFERENCE * (1 - ratio)}`;
  text.textContent = String(percent);

  button.classList.toggle('is-near-limit', payload.nearLimit === true);
  button.title =
    `上下文 ${payload.used ?? 0} / ${payload.budget ?? 0} tokens（${percent}%）\n` +
    `达到 ${payload.threshold ?? 90}% 会自动压缩，也可点击立即压缩`;

  // 记录圆环的实际渲染值与来源：
  // 「圆环不动」需要对比推送值与渲染值，
  // 「同一状态重复渲染」则需要知道是哪条路径触发的。
  void logToHost(
    `上下文圆环[${source}]：${payload.used ?? 0}/${payload.budget ?? 0} tokens ` +
      `= ${percent}%（阈值 ${payload.threshold ?? 90}%，` +
      `dashoffset=${Math.round(RING_CIRCUMFERENCE * (1 - ratio))}/${Math.round(RING_CIRCUMFERENCE)}）` +
      `${payload.nearLimit ? ' 已达阈值' : ''}`,
  );

  // 达到阈值时只示警一次，不弹询问。
  //
  // 不问「是否压缩」的原因：系统在阈值处已自动压缩，此时再问已无实际选择，
  // 徒增一次打断。圆环变色说明正在临界，压缩完成后另有说明消息；
  // 想提前压缩可以直接点圆环。
  if (payload.nearLimit && !compactPrompted) {
    compactPrompted = true;
    addNotice(
      `上下文已达 ${payload.percent}%（${payload.used} / ${payload.budget} tokens），` +
        `超过 ${payload.threshold}% 阈值，正在自动压缩较早的记录。` +
        `若想保留完整上下文，可开新会话。`,
      'warn',
    );
  }

  // 回落到阈值以下后重置，下次再次接近时仍会示警。
  if (!payload.nearLimit) {
    compactPrompted = false;
  }
}

async function refreshContext(source = '主动查询') {
  try {
    updateContextRing(await request('context.state'), source);
  } catch (error) {
    // 取不到不影响对话，圆环保持上一次的值。
    // 但必须记录：静默吞掉异常会让「圆环一直是 0」变成无从排查的问题。
    void logToHost(`刷新上下文状态失败：${error.message}`, 'warn');
  }
}

function handleAgent(message) {
  switch (message.stage) {
    case 'text':
      appendAssistantText(message.text ?? '');
      break;
    case 'thinking':
      appendThinking(message.text ?? '');
      // 思考中还没有正文，气泡里改成对应说明，让用户知道不是卡住了。
      if (!currentAssistant) { showPending('正在思考…'); }
      break;
    case 'tool-start': {
      // 工具开始时结束当前助手气泡，让后续文本另起一段。
      currentAssistant = null;
      const payload = message.payload ?? {};
      addToolCard(payload);
      // 指示器重新落到末尾，并说明正在做什么。
      showPending(`正在${toolLabel(payload.name)}…`);
      break;
    }
    case 'tool-result':
      finishToolCard(message.payload ?? {});
      showPending();
      break;
    case 'retry':
      // 重试提示写进同一个气泡：这是「还在处理」的一种，不必单独占一条消息。
      showPending(message.text ?? '正在重试…');
      break;
    case 'usage':
      updateUsage(message.payload);
      break;
    case 'context':
      updateContextRing(message.payload, 'agent推送');
      break;
    case 'context-trimmed': {
      const p = message.payload ?? {};
      addNotice(
        `上下文超出预算，已压缩 ${p.compressed ?? 0} 条工具结果、移除 ${p.dropped ?? 0} 条早期记录（${p.before} → ${p.after} tokens）。`,
        'warn',
      );
      break;
    }
    case 'step-limit':
      clearPending();
      addNotice(message.text ?? '已达步数上限。', 'warn');
      break;
    case 'stopped':
      clearPending();
      addNotice(message.text ?? '已停止。', 'warn');
      break;
    case 'error':
      clearPending();
      addNotice(message.text ?? '发生错误。', 'error');
      break;
    case 'turn-complete':
      setStatus('');
      clearPending();
      // 本轮结束后上报一次布局：此时工具卡片已生成，
      // 才能核对它们是否默认折叠、有没有把正文挤出可视区。
      void logToHost(describeChatLayout());
      break;
    default:
      break;
  }
}

/** 输入区是否有可提交的内容。附件单独也算：贴张截图问「这个怎么填」很常见。 */
function hasComposerContent() {
  return composer.value.trim() !== '' || hasAttachments();
}

/**
 * 提交输入框里的内容。
 *
 * 空闲时立刻开跑，处理中则排到队尾，上一轮结束后自动接着跑。
 * 两条路合成一个入口：用户按 Enter 时不必先判断「现在能不能发」，
 * 界面也不必再靠禁用输入框来表达「等一下」。
 */
function submit() {
  if (!hasComposerContent()) {
    return;
  }

  if (!isHosted()) {
    addNotice('未运行在 Excel 中，无法发送。', 'error');
    return;
  }

  const entry = {
    id: `q${++queueSequence}`,
    text: composer.value.trim(),
    // 附件在入队时就取出快照：输入框随即清空，之后加的附件属于下一条。
    images: getImages(),
    files: getFiles(),
  };

  composer.value = '';
  clearAttachments();
  autoGrow();

  queue.push(entry);
  renderQueueStrip();
  updateSendAffordance();

  void pumpQueue();
}

/** 排队项在排队条上显示的一行字。没有正文时用附件充当标题。 */
function entrySummary(entry) {
  if (entry.text) {
    return entry.text;
  }

  const parts = [];
  if (entry.images.length > 0) { parts.push(`${entry.images.length} 张图片`); }
  if (entry.files.length > 0) { parts.push(`${entry.files.length} 个文件`); }
  return parts.join('、') || '（空）';
}

/**
 * 重画排队条。
 *
 * 整条重画而不是增量改：队列一动（入队、开跑、取消）位次就要全部重排，
 * 逐个节点去改反而要多维护一份「哪个节点对应哪条」的对应关系。
 * 队列最多也就几条，重画的代价可以忽略。
 */
function renderQueueStrip() {
  if (!queueStrip) {
    return;
  }

  queueStrip.replaceChildren();
  queueStrip.hidden = queue.length === 0;

  queue.forEach((entry, index) => {
    const chip = document.createElement('div');
    chip.className = 'queue-chip';
    chip.setAttribute('role', 'listitem');

    const position = document.createElement('span');
    position.className = 'queue-chip-pos';
    position.setAttribute('aria-hidden', 'true');
    position.textContent = String(index + 1);

    const text = document.createElement('span');
    text.className = 'queue-chip-text';
    text.textContent = entrySummary(entry);

    chip.append(position, text);

    const attachments = entry.images.length + entry.files.length;
    if (attachments > 0) {
      const mark = document.createElement('span');
      mark.className = 'queue-chip-files';
      const count = document.createElement('span');
      count.textContent = String(attachments);
      mark.append(createFileGlyph('file-glyph'), count);
      chip.append(mark);
    }

    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.className = 'queue-chip-cancel';
    cancel.textContent = '×';
    cancel.title = '把这条从队列里去掉，不发送';
    cancel.setAttribute('aria-label', `取消排队中的第 ${index + 1} 条`);
    cancel.addEventListener('click', () => cancelQueued(entry.id));
    chip.append(cancel);

    // 位次与完整正文都在悬停说明里：条上只有数字与截断的一行，
    // 图标没有文字标签，title 是它唯一的自解释途径。
    const ordinal = index === 0 ? '下一个就发这条' : `排在第 ${index + 1} 位`;
    chip.title = `${ordinal}\n${entrySummary(entry)}` +
      (attachments > 0 ? `\n带 ${attachments} 个附件` : '');

    queueStrip.append(chip);
  });

  // 排队条最多显示三条（高度上限在 app.css），其余靠滑动看。每次重画后把视口
  // 归到队首那一端：replaceChildren 不会清掉滚动位置，用户若滑上去看早排的那几条，
  // 之后每次队列变化都会仍停在那里，而他要确认的永远是「下一个发哪条」。
  //
  // 归位给 0：column-reverse 的滚动范围是负的，0 是队首（第 1 位）那一端，
  // 滑到早排的那端是负值（实测 Chromium/WebView2 如此）。
  queueStrip.scrollTop = 0;
}

/**
 * 把条目的气泡插进对话流。真正开跑时才调用。
 *
 * 排队期间不进对话流：对话流记录的是已经发生的事，
 * 待办由排队条负责（见 renderQueueStrip）。
 */
function mountEntryBubble(entry) {
  const bubble = buildBubble('user', entry.text, entry.images, entry.files);
  transcript.append(bubble.wrapper);
  scrollToBottom();
}

/**
 * 取消一条排队中的输入：出队、重画，不留痕。
 *
 * 不往对话流里留划掉的气泡：对话流记的是已经发生的事，而这条从未发出。
 * 取消几次就积几条无法重发、也不进上下文的噪声，读起来只是干扰。
 * 代价是那段文字就此没了——按下取消时的意思本就是「这条不要了」。
 */
function cancelQueued(id) {
  const index = queue.findIndex((entry) => entry.id === id);
  // 找不到说明它已经开跑了，此刻要停只能用停止按钮。
  if (index < 0) {
    return;
  }

  queue.splice(index, 1);
  renderQueueStrip();
  updateSendAffordance();
}

/** 清空队列，返回被取消的条数。与单条取消一样不往对话流留痕。 */
function clearQueue() {
  const dropped = queue.splice(0, queue.length);
  renderQueueStrip();
  updateSendAffordance();
  return dropped.length;
}

/**
 * 依次跑完队列。
 *
 * 单实例轮转：pumping 作为闸门，多个入口（提交、上一轮结束）同时触发时
 * 也只有一条链在跑。否则两条链会各自 shift 出条目并发调用 chat.send，
 * 而加载项只接受一轮，第二条会撞上 BUSY 而白丢一次输入。
 */
async function pumpQueue() {
  if (pumping) {
    return;
  }

  pumping = true;
  try {
    while (queue.length > 0) {
      const entry = queue.shift();
      // 轮到它才上屏：从排队条挪进对话流，两处不会同时出现同一条。
      mountEntryBubble(entry);
      renderQueueStrip();
      await runTurn(entry);
    }
  } finally {
    pumping = false;
    updateSendAffordance();
  }
}

/** 跑一轮。气泡已由 pumpQueue 上屏，这里只负责请求与收尾。 */
async function runTurn(entry) {
  setBusy(true);
  // 进展显示在回复气泡里；状态行清空，免得上一轮的短暂提示看着像本轮的。
  setStatus('');
  showPending();

  try {
    const result = await request(
      'chat.send',
      { text: entry.text, images: entry.images, files: entry.files },
      { timeout: 0 },
    );

    // 不再把 result.error 显示出来：加载项在返回这个字段之前，
    // 已经把同一条消息作为 error 推给了面板并渲染成胶囊，
    // 这里再显示一次就是同样的话说两遍。只留一条日志便于事后对账。
    if (result?.error) {
      void logToHost(`本轮以失败结束：${result.error}`, 'warn');
    }
  } catch (error) {
    // 走到这里是通道本身失败（超时、桥断开、加载项抛异常），
    // 加载项没有机会推送，必须由面板自己插一条。
    addNotice(error.message, 'error');
  } finally {
    setBusy(false);
  }
}

function autoGrow() {
  composer.style.height = 'auto';
  composer.style.height = `${Math.min(composer.scrollHeight, 160)}px`;
}

/**
 * 请求中断当前一轮。
 *
 * 停止不是瞬时的：正在进行的请求要收束。指示器改文案，
 * 让用户知道已经收到而不是没反应。
 *
 * 连带清空队列：点停止的意思是「别再往下做了」，若停完当前一轮又自动
 * 开跑下一条排队输入，那就等于没停。被取消的条目不进对话流，只用一行
 * 系统提示告知取消了几条——那是对「停止」这个动作的回执。
 */
async function stopRun() {
  const dropped = clearQueue();

  try {
    await request('chat.stop');
    showPending('正在停止…');
    if (dropped > 0) {
      addNotice(`已请求停止，并取消了 ${dropped} 条排队中的输入。`, 'warn');
    }
  } catch (error) {
    addNotice(`停止失败：${error.message}`, 'error');
  }
}

export function initChat() {
  transcript = document.getElementById('transcript');
  composer = document.getElementById('composer');
  sendButton = document.getElementById('send');
  statusLine = document.getElementById('status');
  usageLine = document.getElementById('usage');
  queueStrip = document.getElementById('queue-strip');

  // 显式落一次空闲态。index.html 里的 title 只是脚本就位前的兜底，
  // 真正的文案由 setBusy 统一给——两处各写一份的话，改了一处就会不一致。
  setBusy(false);

  // 同一个按钮三种含义，见 updateSendAffordance。
  // 这里按点击时的实时状态分派，不看按钮上的类名——附件可能刚被粘进来。
  sendButton.addEventListener('click', () => {
    if (busy && !hasComposerContent()) {
      void stopRun();
      return;
    }

    submit();
  });

  composer.addEventListener('keydown', (event) => {
    // Enter 发送，Shift+Enter 换行——与主流对话界面一致。
    // 处理中按 Enter 同样受理，只是排到队尾而不是立刻发。
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      submit();
    }
  });

  composer.addEventListener('input', () => {
    autoGrow();
    // 输入框由空变非空会把按钮从「停止」换成「加入队列」，必须实时跟随。
    updateSendAffordance();
  });

  document.getElementById('approval-icon')?.addEventListener('click', () => void cycleApproval());

  // 图片与文本文件附件：粘贴、拖入两种入口。
  // 附件变化也要刷新按钮含义：处理中只贴了张图、一个字没打，
  // 按钮同样该是「加入队列」，而粘贴不触发输入框的 input 事件。
  initAttachments(
    (message, variant) => addNotice(message, variant),
    () => updateSendAffordance(),
  );

  // 模型与思考等级由 picker 模块处理，切换后同步一次上下文占用，
  // 因为换模型可能改变协议，进而影响上下文预算的解释。
  initPicker(() => void refreshContext('切换模型'));

  document.getElementById('context-ring')?.addEventListener('click', async () => {
    if (!contextState) {
      await refreshContext('圆环点击');
      return;
    }

    try {
      const result = await request('context.compact');
      addNotice(
        result.trimmed
          ? `已压缩上下文：${result.before} → ${result.after} tokens`
          : '当前无可压缩内容。',
        result.trimmed ? 'info' : 'warn',
      );
      await refreshContext('圆环压缩后');
    } catch (error) {
      addNotice(`压缩失败：${error.message}`, 'error');
    }
  });

  initFit();

  document.getElementById('reset').addEventListener('click', async () => {
    // 先清队列：留着它们就会在新会话里悄悄开跑，而用户已看不到任何痕迹。
    clearQueue();

    try {
      await request('chat.reset');
      transcript.replaceChildren();
      // 清空 DOM 不会清掉这个引用，漏掉会让下一轮往已移除的节点里写。
      pendingBubble = null;
      usageLine.textContent = '';
      compactPrompted = false;
      // 重新走一次就绪检查：既刷新上下文圆环，也让欢迎语重新出现。
      await checkReady('新会话');
      setStatus('已开始新会话。');
    } catch (error) {
      addNotice(`开始新会话失败：${error.message}`, 'error');
    }
  });

  on('agent', handleAgent);
  on('approval-request', addApprovalCard);

  // 就绪检查交由路由切换统一触发，此处不再调用，避免启动时重复请求。
}

/**
 * 重新检查配置就绪状态。
 *
 * 必须可重复调用：用户在设置页改完再回到对话页时，
 * 旧的判断结果已经过期。只在初始化时查一次会一直显示陈旧的错误。
 */
export async function refreshReady(source = '路由切换') {
  await checkReady(source);
}

/**
 * 处理方式的图标定义。
 *
 * 用盾牌的填充程度表达防护强度：
 *   逐项审批 = 实心盾（最严），每轮确认 = 半填充，全自动 = 空心带感叹号。
 * 颜色同步变化，绿→琥珀→红，与风险递增一致。
 */
const APPROVAL_ICONS = {
  PerWrite: {
    // 实心盾
    path: 'M8 1.5 3 3.3v4.2c0 3 2.1 5.6 5 6.5 2.9-.9 5-3.5 5-6.5V3.3L8 1.5z',
    variant: 'strict',
    label: '逐项审批',
  },
  PerTurn: {
    // 盾牌轮廓 + 下半填充，表示只在开头确认一次
    path: 'M8 1.5 3 3.3v4.2c0 3 2.1 5.6 5 6.5 2.9-.9 5-3.5 5-6.5V3.3L8 1.5zm0 1.6 3.6 1.3v3.1c0 2.2-1.5 4.2-3.6 5V3.1z',
    variant: 'medium',
    label: '每轮确认',
  },
  Automatic: {
    // 空心盾 + 感叹号，提示无人工把关
    path: 'M8 1.5 3 3.3v4.2c0 3 2.1 5.6 5 6.5 2.9-.9 5-3.5 5-6.5V3.3L8 1.5zm0 1.6 3.6 1.3v3.1c0 2.2-1.5 4.2-3.6 5-2.1-.8-3.6-2.8-3.6-5V4.4L8 3.1zM7.3 5h1.4v3.6H7.3V5zm0 4.4h1.4v1.4H7.3V9.4z',
    variant: 'auto',
    label: '全自动',
  },
};

const APPROVAL_ORDER = ['PerWrite', 'PerTurn', 'Automatic'];

let approvalOptions = [];
let currentApproval = 'PerWrite';

/**
 * 填充输入区控件：模型与思考等级由 picker 模块统一负责，
 * 这里只处理处理方式图标。
 */
function fillQuickControls(settings) {
  approvalOptions = settings.approvalOptions ?? approvalOptions;
  currentApproval = settings.approval ?? currentApproval;
  renderApprovalIcon();
  syncPicker(settings);
}

/** 按当前策略绘制图标与颜色。 */
function renderApprovalIcon() {
  const button = document.getElementById('approval-icon');
  const glyph = document.getElementById('approval-glyph');
  if (!button || !glyph) {
    return;
  }

  const icon = APPROVAL_ICONS[currentApproval] ?? APPROVAL_ICONS.PerWrite;
  glyph.setAttribute('d', icon.path);

  for (const name of ['strict', 'medium', 'auto']) {
    button.classList.toggle(`is-${name}`, icon.variant === name);
  }

  const option = approvalOptions.find((o) => o.id === currentApproval);
  const hint = option?.hint ?? '';
  const label = option?.label ?? icon.label;
  button.title = `处理方式：${label}${hint ? `\n${hint}` : ''}\n点击切换`;
  button.setAttribute('aria-label', `处理方式：${label}，点击切换`);
}

/** 点击循环切换处理方式并立即保存。 */
async function cycleApproval() {
  const index = APPROVAL_ORDER.indexOf(currentApproval);
  currentApproval = APPROVAL_ORDER[(index + 1) % APPROVAL_ORDER.length];
  renderApprovalIcon();

  try {
    await request('session.update', { approval: currentApproval });
    const option = approvalOptions.find((o) => o.id === currentApproval);
    setStatus(`处理方式：${option?.label ?? currentApproval}`);
  } catch (error) {
    addNotice(`调整处理方式失败：${error.message}`, 'error');
  }
}

/**
 * 对话区布局摘要，在一轮结束后上报。
 *
 * 与 app.js 的整页布局自检互补：这里关注对话内容本身
 * （工具卡片是否折叠、消息是否按 4/5 宽度分列），
 * 那些元素只有在对话产生内容后才存在。
 */
function describeChatLayout() {
  const cards = transcript.querySelectorAll('.tool-card');
  const opened = transcript.querySelectorAll('.tool-card[open]');

  const widthOf = (selector) => {
    const node = transcript.querySelector(`${selector} .msg-body`);
    if (!node) { return '无'; }
    const own = node.getBoundingClientRect().width;
    const parent = transcript.getBoundingClientRect().width;
    return parent > 0 ? `${Math.round((own / parent) * 100)}%` : '?';
  };

  return `对话布局：工具卡片 ${cards.length} 个（展开 ${opened.length}）` +
    ` 助手消息宽 ${widthOf('.msg-assistant')} 用户消息宽 ${widthOf('.msg-user')}` +
    ` 欢迎语 ${transcript.querySelectorAll('.welcome').length} 个` +
    // 本轮已结束，指示器必须已清除；留下就说明有路径漏了清理。
    ` 处理指示器 ${transcript.querySelectorAll('.msg-pending').length} 个` +
    // 队列状态一并记录：排队条上的条目与内部队列必须数目一致，
    // 对不上就说明有条目丢了显示或显示丢了条目。
    ` 队列 ${queue.length} 条（排队条 ${queueStrip?.querySelectorAll('.queue-chip').length ?? 0} 个）`;
}

/**
 * 显示欢迎语，说明身份、能力与边界。
 *
 * 只在对话流为空时显示，避免每次切回对话页都插一遍。
 * 配置有问题时追加一段说明，让用户知道该去哪里处理，
 * 而不是发了消息才发现不能用。
 */
function showWelcome(settings) {
  // 已有内容（含上一次的欢迎语）就不再插入。
  if (transcript.querySelector('.welcome') || transcript.children.length > 0) {
    return;
  }

  const card = document.createElement('div');
  card.className = 'welcome';

  const title = document.createElement('div');
  title.className = 'welcome-title';
  title.textContent = '我是 ChatSheet，Excel 里的表格助手';

  const body = document.createElement('div');
  body.className = 'welcome-body';
  body.innerHTML = renderMarkdown(
    '我能直接读写你当前打开的工作簿：读取范围、写入值和公式、调整格式、' +
      '管理工作表、建表格和图表、排序。\n\n' +
      '**只能操作表格** —— 没有文件系统、命令行或联网能力。\n\n' +
      '写操作默认逐项征求你同意，读操作直接执行。可在下方切换处理方式。\n\n' +
      '处理中也可以继续输入：新消息会排队，上一条做完自动接着做。' +
      '想中断就清空输入框，再点发送按钮的位置即可停止。\n\n' +
      '图片和文本文件可以直接粘贴或拖进输入框。\n\n' +
      '试试这样说：\n' +
      '- 把 A 列的日期格式改成 2026-08-23 这种\n' +
      '- 按销售额降序排列，标题行保留\n' +
      '- 在 D 列加一列毛利率，用 B 列减 C 列再除以 B 列\n' +
      '- 根据 B 到 C 列做个柱状图',
  );

  card.append(title, body);

  if (!settings.ready) {
    const warn = document.createElement('div');
    warn.className = 'welcome-warn';
    const detail = (settings.readyDetail ?? '').replace(/[。.]+$/, '');
    warn.textContent = `暂时还不能开始：${detail || '配置未完成'}。请到上方「设置」页完成配置。`;
    card.append(warn);
  }

  transcript.append(card);
}

/**
 * 进入对话页时检查配置是否可用。
 * 否则用户要先发一条消息才知道没配好，反馈太晚。
 */
async function checkReady(source = '未标注') {
  if (!isHosted()) {
    return;
  }

  try {
    // 就绪判断由后端给出：只有它知道 CLI 配置里有没有模型、密钥能否解开。
    const settings = await request('settings.get');
    fillQuickControls(settings);
    await refreshContext(source);

    // 移除上一次的未就绪提示，否则配置修好后旧提示仍留在对话流里，
    // 看起来像是「配置完成了但还在报错」。
    for (const stale of transcript.querySelectorAll('.notice-config')) {
      stale.remove();
    }

    showWelcome(settings);

    if (settings.ready) {
      // 就绪时不显示任何文字：模型与接入信息已在输入区的选择器上可见，
      // 再占一行重复展示 url 只会挤压对话空间。
      setStatus('');
      return;
    }

    // 去掉尾部句号再拼接：后端的错误消息自带句号，直接拼会出现「。。」。
    const detail = (settings.readyDetail ?? '').replace(/[。.]+$/, '');
    const notice = addNotice(`还不能开始对话：${detail}。请到「设置」页完成配置。`, 'warn');
    // 打标记以便下次检查时清理。
    notice.classList.add('notice-config');
    // 状态行不再重复一遍「配置未完成」：上面那条胶囊已经把原因和去处说清了。
    setStatus('');
  } catch (error) {
    addNotice(`读取配置失败：${error.message}`, 'error');
  }
}
