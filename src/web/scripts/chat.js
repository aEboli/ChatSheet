import { request, on, isHosted, logToHost } from './bridge.js';
import { renderMarkdown } from './markdown.js';
import { initPicker, syncPicker } from './picker.js';
import {
  initAttachments,
  getAttachments,
  hasAttachments,
  clearAttachments,
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

let transcript;
let composer;
let sendButton;
let stopButton;
let statusLine;
let usageLine;

let busy = false;
let currentAssistant = null;
let currentThinking = null;

function toolLabel(name) {
  return TOOL_LABELS[name] ?? name;
}

function scrollToBottom() {
  // 用 scrollTop 而非 scrollIntoView：后者在窄栏里会带偏水平位置。
  transcript.scrollTop = transcript.scrollHeight;
}

function addBubble(role, text, images = []) {
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
  transcript.append(wrapper);
  scrollToBottom();
  // 助手气泡的流式更新需要拿到文本容器本身。
  return body.querySelector('.msg-text') ?? body;
}

function ensureAssistant() {
  if (!currentAssistant) {
    currentAssistant = { element: addBubble('assistant', ''), raw: '' };
  }
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
  card.className = 'tool-card';
  card.dataset.toolId = payload.id ?? '';

  const head = document.createElement('summary');
  head.className = 'tool-head';

  const name = document.createElement('span');
  name.className = 'tool-name';
  name.textContent = toolLabel(payload.name);

  const state = document.createElement('span');
  state.className = 'tool-state';
  state.textContent = '执行中…';

  // 撤销按钮占位，执行成功且可撤销时才填充。
  // 放在摘要行右端：与操作本身同处一行，不必展开就能撤销。
  const actions = document.createElement('span');
  actions.className = 'tool-actions';

  head.append(name, state, actions);

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
      const rows = value.length;
      const cols = Array.isArray(value[0]) ? value[0].length : 1;
      parts.push(`${key}: ${rows} 行 × ${cols} 列`);
      continue;
    }

    const text = String(value);
    parts.push(`${key}: ${text.length > 60 ? `${text.slice(0, 60)}…` : text}`);
  }

  return parts.join('\n');
}

function finishToolCard(payload) {
  const card = transcript.querySelector(`.tool-card[data-tool-id="${payload.id}"]`);
  if (!card) {
    return;
  }

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
  button.title = payload.undoSummary ? `撤销：${payload.undoSummary}` : '撤销此操作';

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
      const rows = value.length;
      const cols = Array.isArray(value[0]) ? value[0].length : 1;
      const preview = JSON.stringify(value.slice(0, 3));
      lines.push(`${key}: ${rows} 行 × ${cols} 列`);
      lines.push(`  前几行 ${preview.length > 200 ? `${preview.slice(0, 200)}…` : preview}`);
      continue;
    }

    const text = String(value);
    lines.push(`${key}: ${text.length > 200 ? `${text.slice(0, 200)}…` : text}`);
  }

  return lines.length > 0 ? lines.join('\n') : '（无返回数据）';
}

/** 成功时展示影响面而非原始数据，用户关心的是改了多少。 */
function describeSuccess(data) {
  if (!data || typeof data !== 'object') {
    return '完成';
  }

  if (typeof data.cells_written === 'number') {
    return `已写入 ${data.cells_written} 个单元格`;
  }
  if (typeof data.cells_affected === 'number') {
    return `影响 ${data.cells_affected} 个单元格`;
  }
  if (typeof data.rows === 'number' && typeof data.columns === 'number') {
    return `${data.rows} 行 × ${data.columns} 列`;
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
  return '完成';
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
  impact.textContent = message.impact ? `影响范围：${message.impact}` : '';

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
      const outcome = document.createElement('div');
      outcome.className = approved ? 'approval-outcome is-ok' : 'approval-outcome is-error';
      outcome.textContent = approved ? (approveRest ? '已允许，本轮后续不再询问' : '已允许') : '已拒绝';
      actions.replaceWith(outcome);
    } catch (error) {
      setStatus(`回复审批失败：${error.message}`, true);
    }
  };

  approve.addEventListener('click', () => void settle(true, false));
  approveAll.addEventListener('click', () => void settle(true, true));
  reject.addEventListener('click', () => void settle(false, false));

  actions.append(approve, approveAll, reject);
  card.append(title, risk);
  if (message.impact) { card.append(impact); }
  if (args.textContent) { card.append(args); }
  card.append(actions);

  transcript.append(card);
  scrollToBottom();
}

function addNotice(text, variant = 'info') {
  const notice = document.createElement('div');
  notice.className = `notice notice-${variant}`;
  notice.textContent = text;
  transcript.append(notice);
  scrollToBottom();
  return notice;
}

function setStatus(text, isError = false) {
  statusLine.textContent = text ?? '';
  statusLine.className = isError ? 'status is-error' : 'status';
}

function setBusy(value) {
  busy = value;
  sendButton.disabled = value;
  stopButton.hidden = !value;
  composer.disabled = value;
  if (!value) {
    currentAssistant = null;
    currentThinking = null;
  }
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
      break;
    case 'tool-start':
      // 工具开始时结束当前助手气泡，让后续文本另起一段。
      currentAssistant = null;
      addToolCard(message.payload ?? {});
      break;
    case 'tool-result':
      finishToolCard(message.payload ?? {});
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
      addNotice(message.text ?? '已达步数上限。', 'warn');
      break;
    case 'stopped':
      addNotice(message.text ?? '已停止。', 'warn');
      break;
    case 'error':
      addNotice(message.text ?? '发生错误。', 'error');
      break;
    case 'turn-complete':
      setStatus('');
      // 本轮结束后上报一次布局：此时工具卡片已生成，
      // 才能核对它们是否默认折叠、有没有把正文挤出可视区。
      void logToHost(describeChatLayout());
      break;
    default:
      break;
  }
}

async function send() {
  const text = composer.value.trim();

  // 只有图片没有文字也允许发送：贴张截图问「这个怎么填」是常见用法。
  if ((!text && !hasAttachments()) || busy) {
    return;
  }

  if (!isHosted()) {
    setStatus('未运行在 Excel 中，无法发送。', true);
    return;
  }

  const images = getAttachments();
  addBubble('user', text, images);
  composer.value = '';
  clearAttachments();
  autoGrow();
  setBusy(true);
  setStatus('正在处理…');

  try {
    const result = await request('chat.send', { text, images }, { timeout: 0 });
    if (result?.error) {
      setStatus(result.error, true);
    } else {
      setStatus('');
    }
  } catch (error) {
    setStatus(error.message, true);
    addNotice(error.message, 'error');
  } finally {
    setBusy(false);
  }
}

function autoGrow() {
  composer.style.height = 'auto';
  composer.style.height = `${Math.min(composer.scrollHeight, 160)}px`;
}

export function initChat() {
  transcript = document.getElementById('transcript');
  composer = document.getElementById('composer');
  sendButton = document.getElementById('send');
  stopButton = document.getElementById('stop');
  statusLine = document.getElementById('status');
  usageLine = document.getElementById('usage');

  sendButton.addEventListener('click', () => void send());

  composer.addEventListener('keydown', (event) => {
    // Enter 发送，Shift+Enter 换行——与主流对话界面一致。
    if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
      event.preventDefault();
      void send();
    }
  });

  composer.addEventListener('input', autoGrow);

  stopButton.addEventListener('click', async () => {
    try {
      await request('chat.stop');
      setStatus('正在停止…');
    } catch (error) {
      setStatus(error.message, true);
    }
  });

  document.getElementById('approval-icon')?.addEventListener('click', () => void cycleApproval());

  // 图片附件：粘贴、拖入、按钮选择三种入口。
  initAttachments((message, variant) => addNotice(message, variant));

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
      setStatus(`压缩失败：${error.message}`, true);
    }
  });

  document.getElementById('reset').addEventListener('click', async () => {
    try {
      await request('chat.reset');
      transcript.replaceChildren();
      usageLine.textContent = '';
      compactPrompted = false;
      // 重新走一次就绪检查：既刷新上下文圆环，也让欢迎语重新出现。
      await checkReady('新会话');
      setStatus('已开始新会话。');
    } catch (error) {
      setStatus(error.message, true);
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
    setStatus(`调整失败：${error.message}`, true);
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
    ` 欢迎语 ${transcript.querySelectorAll('.welcome').length} 个`;
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
      '写操作默认逐项征求你同意，读操作直接执行。' +
      '可在下方切换处理方式，也能随时点「停止」中断。\n\n' +
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
    setStatus('配置未完成', true);
  } catch (error) {
    setStatus(`读取配置失败：${error.message}`, true);
  }
}
