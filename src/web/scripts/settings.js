import { request, logToHost } from './bridge.js';

let form;
let statusLine;
let current = null;

const CLI_LABELS = {
  Auto: '自动（优先 Claude）',
  Claude: 'Claude CLI',
  Codex: 'Codex CLI',
};

function setStatus(text, variant = '') {
  statusLine.textContent = text ?? '';
  statusLine.className = variant ? `status is-${variant}` : 'status';
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) { node.className = className; }
  if (text !== undefined) { node.textContent = text; }
  return node;
}

function field(labelText, control, hintText) {
  const wrapper = el('div', 'field');
  const label = el('label', 'field-label', labelText);
  if (control.id) { label.htmlFor = control.id; }
  wrapper.append(label, control);
  if (hintText) { wrapper.append(el('div', 'field-hint', hintText)); }
  return wrapper;
}

function select(id, options, value) {
  const node = el('select', 'input');
  node.id = id;
  for (const option of options) {
    const item = el('option', null, option.label);
    item.value = option.value;
    if (option.value === value) { item.selected = true; }
    node.append(item);
  }
  return node;
}

function input(id, value, type = 'text', placeholder = '') {
  const node = el('input', 'input');
  node.id = id;
  node.type = type;
  node.value = value ?? '';
  node.placeholder = placeholder;
  return node;
}

function renderModeSection() {
  const options = [
    { value: 'LocalCli', label: '① 使用本机 CLI 配置' },
    { value: 'CustomApi', label: '② 自定义接口' },
    { value: 'Authorized', label: '③ 授权登录（暂未实现）' },
  ];

  const node = select('mode', options, current.mode);
  node.addEventListener('change', () => {
    current.mode = node.value;
    render();
  });

  return field('接入模式', node);
}

function renderLocalCliSection() {
  const section = el('div', 'section');
  section.append(el('div', 'section-title', '本机 CLI 配置'));

  const node = select(
    'cliSource',
    Object.entries(CLI_LABELS).map(([value, label]) => ({ value, label })),
    current.cliSource,
  );
  node.addEventListener('change', () => { current.cliSource = node.value; });
  section.append(field('使用哪个 CLI', node, '读取该 CLI 配置文件中的接口地址与密钥，不启动 CLI 进程。'));

  const probeBox = el('div', 'probe');
  probeBox.id = 'probe-result';
  probeBox.textContent = '正在检测…';
  section.append(probeBox);

  // 直接把元素传进去，不靠 getElementById 查找。
  // 此处 section 还没 append 到文档，按 id 查会取到 null 而静默返回，
  // 结果是文字永远停在「正在检测…」。
  void refreshProbe(probeBox);
  return section;
}

/**
 * 本机 CLI 配置里通常不含模型名（实测 Claude 与 Codex 的配置都只有地址与令牌），
 * 所以选了模式 ① 之后仍需指定模型。这里在检测到可用配置后自动拉取模型列表，
 * 让用户直接从下拉选择，而不是手工输入。
 */
async function autoFetchModelsForCli() {
  if (current.mode !== 'LocalCli' || current.model) {
    return;
  }

  const list = document.getElementById('model-list');
  if (!list) {
    return;
  }

  setStatus('正在从 CLI 配置的接口获取模型列表…');

  try {
    const result = await request(
      'models.list',
      { mode: current.mode, cliSource: current.cliSource },
      { timeout: 40000 },
    );
    const models = result.models ?? [];

    if (models.length === 0) {
      setStatus('该接口未返回模型列表，请手动填写模型名。', 'warn');
      return;
    }

    populateModelList(models);
    setStatus(`已获取 ${models.length} 个模型，请选择一个。`, 'ok');
  } catch (error) {
    setStatus(`自动获取模型失败：${error.message}`, 'error');
  }
}

/** 用模型列表填充下拉框。 */
function populateModelList(models) {
  const list = document.getElementById('model-list');
  const model = document.getElementById('model');
  if (!list) {
    return;
  }

  list.replaceChildren();
  const placeholder = el('option', null, `已获取 ${models.length} 个模型，选择一个`);
  placeholder.value = '';
  list.append(placeholder);

  for (const id of models) {
    const item = el('option', null, id);
    item.value = id;
    if (id === current.model) { item.selected = true; }
    list.append(item);
  }

  list.hidden = false;

  // 只有一个模型时直接选中，省掉一次点击。
  if (models.length === 1 && model) {
    model.value = models[0];
    current.model = models[0];
  }
}

async function refreshProbe(target) {
  // 优先用传入的元素；重新渲染后按 id 查找作为兜底。
  const box = target ?? document.getElementById('probe-result');
  if (!box) { return; }

  try {
    const result = await request('cli.probe');
    box.replaceChildren();

    for (const candidate of result.candidates ?? []) {
      const row = el('div', 'probe-row');
      const dot = el('span', candidate.usable ? 'probe-dot is-ok' : 'probe-dot is-error');
      const name = el('span', 'probe-name', candidate.displayName);
      const detail = el('span', 'probe-detail', candidate.usable
        ? `${candidate.baseUrl}${candidate.model ? ` · ${candidate.model}` : ''}`
        : candidate.detail);
      row.append(dot, name, detail);
      box.append(row);
    }

    const usable = (result.candidates ?? []).filter((c) => c.usable);
    if (usable.length === 0) {
      box.append(el('div', 'field-hint', '未检测到可用配置。若 CLI 使用订阅登录而非 API 密钥，请改用「自定义接口」模式。'));
      return;
    }

    // CLI 配置通常不含模型名，检测通过后顺手把模型列表拉下来。
    if (!usable.some((c) => c.model)) {
      box.append(el('div', 'field-hint', '该 CLI 配置未指定模型，已自动获取可用模型供选择。'));
    }

    void autoFetchModelsForCli();
  } catch (error) {
    box.textContent = `检测失败：${error.message}`;
  }
}

function renderCustomApiSection() {
  const section = el('div', 'section');
  section.append(el('div', 'section-title', '自定义接口'));

  const protocolOptions = (current.protocols ?? []).map((p) => ({ value: p.id, label: p.label }));
  const protocol = select('customProtocol', protocolOptions, current.customProtocol);
  protocol.addEventListener('change', () => { current.customProtocol = protocol.value; });
  section.append(field('接口协议', protocol));

  const baseUrl = input('customBaseUrl', current.customBaseUrl, 'text', 'https://api.example.com/v1');
  baseUrl.addEventListener('input', () => { current.customBaseUrl = baseUrl.value; });
  section.append(field('接口地址', baseUrl, '可填根地址或完整端点，会自动规范化。'));

  const token = input('customToken', '', 'password',
    current.hasCustomToken ? `已保存 ${current.maskedToken}，留空则不修改` : '填入密钥');
  section.append(field('密钥', token, '使用 Windows DPAPI 加密保存在本机，不会明文落盘，也不会发送给面板以外的任何地方。'));

  return section;
}

function renderModelSection() {
  const section = el('div', 'section');
  section.append(el('div', 'section-title', '模型'));

  const row = el('div', 'row');

  const model = input('model', current.model, 'text', '例如 gpt-4o');
  model.addEventListener('input', () => { current.model = model.value; });

  const fetchButton = el('button', 'btn', '自动获取');
  fetchButton.type = 'button';

  const list = el('select', 'input');
  list.id = 'model-list';
  list.hidden = true;
  list.addEventListener('change', () => {
    if (list.value) {
      model.value = list.value;
      current.model = list.value;
    }
  });

  fetchButton.addEventListener('click', async () => {
    fetchButton.disabled = true;
    fetchButton.textContent = '获取中…';
    try {
      // 必须带上当前选择的模式：用户可能刚切换但还没保存，
      // 后端若按已保存的旧模式解析就会连错地址。
      const payload = { mode: current.mode, cliSource: current.cliSource };
      if (current.mode === 'CustomApi') {
        payload.protocol = current.customProtocol;
        payload.baseUrl = current.customBaseUrl;
        payload.token = document.getElementById('customToken')?.value ?? '';
      }

      const result = await request('models.list', payload, { timeout: 40000 });
      const models = result.models ?? [];

      if (models.length === 0) {
        setStatus('该接口未返回模型列表，请手动填写模型名。', 'warn');
        list.hidden = true;
      } else {
        populateModelList(models);
        setStatus(`已获取 ${models.length} 个模型。`, 'ok');
      }
    } catch (error) {
      setStatus(`获取模型失败：${error.message}`, 'error');
    } finally {
      fetchButton.disabled = false;
      fetchButton.textContent = '自动获取';
    }
  });

  row.append(model, fetchButton);
  section.append(field('模型名', row));
  section.append(list);

  section.append(el('div', 'field-hint',
    '思考档位与处理方式已移到对话页输入框上方，那里切换更顺手，无需回到本页。'));

  return section;
}

function renderBehaviorSection() {
  const section = el('div', 'section');
  section.append(el('div', 'section-title', '行为'));

  const selection = el('input');
  selection.type = 'checkbox';
  selection.id = 'autoIncludeSelection';
  selection.checked = current.autoIncludeSelection !== false;
  selection.addEventListener('change', () => { current.autoIncludeSelection = selection.checked; });

  const selectionRow = el('label', 'checkbox-row');
  selectionRow.append(selection, el('span', null, '每轮自动附带当前选区信息'));
  section.append(selectionRow);
  section.append(el('div', 'field-hint',
    '关闭后，你说「这一列」时我需要先调用工具确认，会多一次往返。'));

  // 三个数值项折叠起来：它们有合理默认值，多数用户不需要调整，
  // 平铺会让设置页显得冗长，掩盖真正需要填的接入信息。
  const advanced = el('details', 'advanced');
  const summary = el('summary', 'advanced-summary', '高级参数');
  advanced.append(summary);

  const budget = input('contextBudgetTokens', current.contextBudgetTokens, 'number');
  budget.min = '8000';
  budget.addEventListener('input', () => { current.contextBudgetTokens = Number(budget.value); });
  advanced.append(field('上下文预算（tokens）', budget,
    '达到 90% 时自动压缩较早的工具结果，必要时移除最早的记录。'));

  const maxTokens = input('maxOutputTokens', current.maxOutputTokens, 'number');
  maxTokens.min = '256';
  maxTokens.addEventListener('input', () => { current.maxOutputTokens = Number(maxTokens.value); });
  advanced.append(field('单次回复上限（tokens）', maxTokens,
    '思考模式下这个值也约束思考长度，设太小会让模型来不及给出结论。'));

  const steps = input('maxSteps', current.maxSteps, 'number');
  steps.min = '1';
  steps.addEventListener('input', () => { current.maxSteps = Number(steps.value); });
  advanced.append(field('单轮最多工具步数', steps, '防止模型陷入循环。达到上限会明确告知。'));

  section.append(advanced);
  return section;
}

/** 诊断入口：把日志与安装信息集中到设置页底部，便于排查。 */
function renderDiagnosticsSection() {
  const section = el('div', 'section');
  section.append(el('div', 'section-title', '排查'));
  section.append(el('div', 'field-hint',
    '遇到问题时，「诊断」页可查看宿主版本、注册状态与 WebView2 版本；' +
    '详细日志在 %LOCALAPPDATA%\\ChatSheet\\logs，面板自身的状态也会写入同一份。'));
  return section;
}

/** 校验当前配置是否足以发起对话，返回问题清单。 */
function validate() {
  const problems = [];

  if (current.mode === 'CustomApi') {
    if (!current.customBaseUrl || current.customBaseUrl.trim() === '') {
      problems.push('请填写接口地址');
    }

    const tokenInput = document.getElementById('customToken');
    const hasNewToken = tokenInput && tokenInput.value.trim() !== '';
    if (!hasNewToken && !current.hasCustomToken) {
      problems.push('请填写接口密钥');
    }
  }

  if (current.mode === 'Authorized') {
    problems.push('授权登录模式尚未实现，请改用其他模式');
  }

  // 模式 ① 下 CLI 配置可能自带模型，此时不必强制填写。
  // effectiveModel 由后端解析得出，是权威值。
  const hasModel = (current.model && current.model.trim() !== '') ||
    (current.mode === 'LocalCli' && current.effectiveModel);
  if (!hasModel) {
    problems.push('请填写或选择模型');
  }

  return problems;
}

/** 就绪状态条：让用户一眼看出当前配置能否发起对话。 */
function renderReadyBanner() {
  const banner = el('div', current.ready ? 'ready-banner is-ok' : 'ready-banner is-warn');
  const icon = el('span', 'ready-dot');
  const text = el('span', 'ready-text', current.ready
    ? `配置就绪：${current.readyDetail}`
    : `配置未完成：${current.readyDetail || '请填写下方必填项'}`);
  banner.append(icon, text);
  return banner;
}

function render() {
  form.replaceChildren();
  form.append(renderReadyBanner());
  form.append(renderModeSection());

  if (current.mode === 'LocalCli') {
    form.append(renderLocalCliSection());
  } else if (current.mode === 'CustomApi') {
    form.append(renderCustomApiSection());
  } else {
    const notice = el('div', 'notice notice-warn',
      '授权登录模式尚未实现。请使用「本机 CLI 配置」或「自定义接口」。');
    form.append(notice);
  }

  form.append(renderModelSection());
  form.append(renderBehaviorSection());
  form.append(renderDiagnosticsSection());

  // 保存按钮吸附在底部：设置页内容较长，滚到中途也能随时保存，
  // 不必回到页尾找按钮。
  const actions = el('div', 'settings-actions');
  const save = el('button', 'btn btn-primary', '保存设置');
  save.type = 'button';
  save.addEventListener('click', async () => {
    // 保存前校验必填项。否则配置不完整也能存下，
    // 用户要到发送消息时才发现问题，反馈链路太长。
    const problems = validate();
    if (problems.length > 0) {
      setStatus(problems.join('；'), 'error');
      return;
    }

    save.disabled = true;
    try {
      const payload = { ...current };
      const tokenInput = document.getElementById('customToken');
      // 留空表示不修改已保存的密钥，不能传空串（那会清除密钥）。
      if (tokenInput && tokenInput.value.trim() !== '') {
        payload.customToken = tokenInput.value.trim();
      }
      // 这些是后端计算出的只读字段，不参与保存。
      for (const key of [
        'protocols', 'maskedToken', 'hasCustomToken',
        'ready', 'readyDetail', 'effectiveModel',
        'thinkingLevels', 'approvalPolicies',
      ]) {
        delete payload[key];
      }

      current = await request('settings.save', payload);
      setStatus('已保存。', 'ok');
      render();
    } catch (error) {
      setStatus(`保存失败：${error.message}`, 'error');
    } finally {
      save.disabled = false;
    }
  });

  actions.append(save);
  form.append(actions);

  // 渲染完成后上报布局：设置页控件多，窄栏下最容易横向溢出，
  // 需要在真实宿主宽度下实测而不是靠肉眼。
  void reportLayout();
}

async function reportLayout() {
  // 等一帧，确保浏览器已完成布局计算。
  await new Promise((resolve) => requestAnimationFrame(() => resolve()));

  const overflowing = [];
  for (const node of form.querySelectorAll('.input, .btn, .row, .probe')) {
    if (node.scrollWidth > node.clientWidth + 1) {
      overflowing.push(`${node.className}(${node.scrollWidth}>${node.clientWidth})`);
    }
  }

  const docOverflow = document.documentElement.scrollWidth > document.documentElement.clientWidth;

  await logToHost(
    `设置页布局：视口宽 ${window.innerWidth} 表单宽 ${Math.round(form.getBoundingClientRect().width)} ` +
      `控件 ${form.querySelectorAll('.input').length} 个 ` +
      `页面横向溢出=${docOverflow ? '有' : '无'} ` +
      `溢出控件=${overflowing.length === 0 ? '无' : overflowing.join(', ')}`,
    overflowing.length > 0 || docOverflow ? 'warn' : 'info',
  );
}

export async function initSettings() {
  form = document.getElementById('settings-form');
  statusLine = document.getElementById('settings-status');

  try {
    current = await request('settings.get');
    render();
  } catch (error) {
    setStatus(`读取设置失败：${error.message}`, 'error');
  }
}
