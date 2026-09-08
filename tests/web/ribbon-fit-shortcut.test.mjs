// 功能区快捷区的结构与静默转发链路检查。
//
// 这条入口横跨 Ribbon XML、COM 回调、窗格包装、WebView2 控件和嵌入的 PNG。
// 任意一层名字写错都不会在页面测试里报错，宿主里只会表现为「点了没反应」
// 或「按钮上没有图标」。冷启动若不排队，已经打开过面板时正常，第一次点击
// 却会落在空白页上；排队若没有上限，导航失败后待执行标记会一直留着，
// 某次页面重新加载时突然排一次表。这里把这几类静默故障锁住。
//
// 运行：node tests/web/ribbon-fit-shortcut.test.mjs

import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const read = (...parts) => readFileSync(join(root, ...parts), 'utf8');

const ribbon = read('src', 'ChatSheet.AddIn', 'Resources', 'Ribbon.xml');
const addIn = read('src', 'ChatSheet.AddIn', 'ComAddIn.cs');
const images = read('src', 'ChatSheet.AddIn', 'ComAddIn.RibbonImage.cs');
const csproj = read('src', 'ChatSheet.AddIn', 'ChatSheet.AddIn.csproj');
const controller = read('src', 'ChatSheet.AddIn', 'TaskPaneController.cs');
const control = read('src', 'ChatSheet.AddIn', 'TaskPaneControl.cs');
const automation = read('src', 'ChatSheet.AddIn', 'AddInAutomation.cs');
const panel = read('src', 'web', 'index.html');
const chat = read('src', 'web', 'scripts', 'chat.js');

let passed = 0;
let failed = 0;

function check(label, condition, detail = '') {
  if (condition) {
    passed += 1;
    console.log(`  通过  ${label}`);
    return;
  }

  failed += 1;
  console.log(`  失败  ${label}`);
  if (detail) { console.log(`        ${detail}`); }
}

function block(source, start, end) {
  const from = source.indexOf(start);
  const to = source.indexOf(end, from + start.length);
  return from >= 0 && to > from ? source.slice(from, to) : '';
}

console.log('检查功能区快捷区：');
console.log('');

const settingsAt = ribbon.indexOf('id="ChatSheetSettings"');
const diagnosticsAt = ribbon.indexOf('id="ChatSheetDiagnose"');
const sep2At = ribbon.indexOf('id="ChatSheetSep2"');
const fitAt = ribbon.indexOf('id="ChatSheetFitCurrentSheet"');

const settingsButton = block(ribbon, '<button id="ChatSheetSettings"', '/>');
const diagnoseButton = block(ribbon, '<button id="ChatSheetDiagnose"', '/>');
const fitButton = block(ribbon, '<button id="ChatSheetFitCurrentSheet"', '/>');

// CustomUI 里静态属性与它的 get 回调互斥。同时写会让 Office 校验整份 XML
// 失败，然后把它整个丢掉——选项卡连同全部按钮一起消失，且不报任何错，
// 只表现为 onLoad 不触发（日志里少掉「功能区已加载」）。
// 这一条是 v0.9.0 的真实事故：撤销按钮同时写了 label 和 getLabel。
const MUTEX = [
  ['label', 'getLabel'],
  ['screentip', 'getScreentip'],
  ['supertip', 'getSupertip'],
  ['enabled', 'getEnabled'],
  ['image', 'getImage'],
  ['imageMso', 'getImage'],
];
const controlBlocks = [...ribbon.matchAll(/<(?:button|toggleButton)\b([^>]*)\/?\s*>/g)]
  .map((m) => m[1]);
const mutexHits = [];
for (const attrs of controlBlocks) {
  const id = attrs.match(/\bid="([^"]+)"/)?.[1] ?? '(无 id)';
  for (const [stat, dyn] of MUTEX) {
    const hasStat = new RegExp(`\\b${stat}="`).test(attrs);
    const hasDyn = new RegExp(`\\b${dyn}="`).test(attrs);
    if (hasStat && hasDyn) {
      mutexHits.push(`${id} 同时有 ${stat} 和 ${dyn}`);
    }
  }
}
check('没有任何控件同时写静态属性与对应的 get 回调',
  mutexHits.length === 0, mutexHits.join('；'));

check('快捷按钮位于设置和诊断之后',
  settingsAt >= 0 && settingsAt < diagnosticsAt && diagnosticsAt < fitAt,
  `位置：设置=${settingsAt}，诊断=${diagnosticsAt}，适配=${fitAt}`);
check('分隔符把快捷区与「打开某个页」的按钮分开',
  diagnosticsAt >= 0 && diagnosticsAt < sep2At && sep2At < fitAt,
  `位置：诊断=${diagnosticsAt}，分隔=${sep2At}，适配=${fitAt}`);
check('快捷按钮显示「适配当前表」', fitButton.includes('label="适配当前表"'));
check('快捷按钮绑定唯一回调 OnFitCurrentSheet',
  fitButton.includes('onAction="OnFitCurrentSheet"') &&
    (ribbon.match(/onAction="OnFitCurrentSheet"/g) ?? []).length === 1);

check('设置按钮有独立的图标回调',
  settingsButton.includes('getImage="OnGetSettingsImage"'));
check('诊断按钮有独立的图标回调',
  diagnoseButton.includes('getImage="OnGetDiagnoseImage"'));
check('适配按钮有独立的图标回调',
  fitButton.includes('getImage="OnGetFitImage"'));
check('三个图标回调互不相同，不会串图',
  new Set([
    'OnGetSettingsImage',
    'OnGetDiagnoseImage',
    'OnGetFitImage',
  ]).size === 3);

check('设置图标回调实现存在', images.includes('public object OnGetSettingsImage(object control)'));
check('诊断图标回调实现存在', images.includes('public object OnGetDiagnoseImage(object control)'));
check('适配图标回调实现存在', images.includes('public object OnGetFitImage(object control)'));
check('面板按钮图标回调仍在', images.includes('public object OnGetPaneImage(object control)'));

check('工程嵌入设置图标', csproj.includes('Resources\\RibbonSettings.png'));
check('工程嵌入诊断图标', csproj.includes('Resources\\RibbonDiagnose.png'));
check('工程嵌入适配图标', csproj.includes('Resources\\RibbonFit.png'));

const callback = block(
  addIn,
  'public void OnFitCurrentSheet(object control)',
  'private void ShowPaneRoute',
);
check('COM 回调实现存在', callback.length > 0);
check('回调不显示面板、不切页',
  !callback.includes('ApplyPaneVisibility') &&
    !callback.includes('"chat"'),
  '回调里仍有显示面板或切到对话页的痕迹');
check('回调不弹模态框',
  !callback.includes('interactive: true') &&
    !callback.includes('ShowBlockerPrompt'),
  '回调里仍有弹框路径');
check('回调先确保面板存在再转发适配',
  callback.includes('EnsurePane()') &&
    callback.includes('_pane.FitCurrentSheet()'));
check('面板不可用时放弃而不是在背后另写一套适配',
  callback.includes('本次不执行'));

const controllerMethod = block(
  controller,
  'internal void FitCurrentSheet()',
  'internal string SendChat',
);
check('窗格包装转发到控件',
  controllerMethod.includes('_control.FitCurrentSheet();'));

const controlMethod = block(
  control,
  'internal void FitCurrentSheet()',
  'private bool DispatchPendingFitCurrentSheet()',
);
const dispatchMethod = block(
  control,
  'private bool DispatchPendingFitCurrentSheet()',
  'private void StartFitRetry()',
);

check('控件先登记一项待执行适配',
  controlMethod.includes('_fitCurrentSheetPending = true;'));
check('控件监听页面加载完成事件',
  control.includes('core.NavigationCompleted += OnNavigationCompleted;'));
check('加载成功后释放待执行动作',
  /_pageLoaded\s*=\s*e\.IsSuccess;[\s\S]*?DispatchPendingFitCurrentSheet\(\);/.test(control));
check('页面未就绪时不会提前投递',
  dispatchMethod.includes('!_pageLoaded') &&
    dispatchMethod.includes('return false;'));
check('最终经页面入口点面板现有适配按钮',
  dispatchMethod.includes('__chatsheetRibbonFit') &&
    chat.includes("window.__chatsheetRibbonFit = () =>") &&
    chat.includes("button.click();"));
check('投递前先清待执行标记，避免重试再点一次',
  dispatchMethod.indexOf('_fitCurrentSheetPending = false;') <
    dispatchMethod.indexOf('ExecuteScriptAsync') &&
    dispatchMethod.indexOf('_fitCurrentSheetPending = false;') >= 0);
check('脚本回报点到了、正忙、或找不到入口',
  chat.includes("return 'clicked'") &&
    chat.includes("return 'busy'") &&
    chat.includes("return 'no-button'") &&
    dispatchMethod.includes('no-hook'));
check('冷启动有有界重试，导航失败不会把适配留到下次',
  control.includes('FitRetryMaxTicks') &&
    control.includes('StartFitRetry()') &&
    control.includes('本次放弃'));
check('释放面板时停掉重试定时器',
  /_fitRetryTimer[\s\S]*?Tick -= OnFitRetryTick[\s\S]*?Dispose\(\)/.test(control));

check('面板内原适配入口仍保留',
  /<button[^>]*id="fit"/.test(panel), 'index.html 中找不到 id="fit" 的按钮');

// ---- 快捷区的撤销按钮 ----

const undoAt = ribbon.indexOf('id="ChatSheetUndoRibbon"');
const undoButton = block(ribbon, '<button id="ChatSheetUndoRibbon"', '/>');

check('撤销按钮排在功能区最后',
  undoAt > fitAt && ribbon.indexOf('<button', undoAt + 10) === -1,
  `位置：适配=${fitAt}，撤销=${undoAt}`);
check('撤销按钮有图标回调', undoButton.includes('getImage="OnGetUndoImage"'));
check('撤销按钮的启用态由回调决定',
  undoButton.includes('getEnabled="OnGetRibbonUndoEnabled"'));
check('撤销按钮的文字由回调决定（重叠时改叫「仍然撤销」）',
  undoButton.includes('getLabel="OnGetRibbonUndoLabel"') &&
    addIn.includes('仍然撤销'));
check('撤销按钮的悬停说明由回调决定',
  undoButton.includes('getSupertip="OnGetRibbonUndoSupertip"'));
check('工程嵌入撤销图标', csproj.includes('Resources\\RibbonUndo.png'));
check('撤销图标回调实现存在', images.includes('public object OnGetUndoImage(object control)'));

const undoCallback = block(
  addIn,
  'public void OnUndoRibbonAction(object control)',
  'public bool OnGetRibbonUndoEnabled',
);
check('撤销回调实现存在', undoCallback.length > 0);
check('撤销回调不显示面板、不弹框',
  !undoCallback.includes('ApplyPaneVisibility') &&
    !undoCallback.includes('ShowBlockerPrompt'));
check('撤销回调转发到窗格包装',
  undoCallback.includes('_pane.UndoLastRibbonAction()'));

check('四个按钮都有悬停说明',
  [settingsButton, diagnoseButton, fitButton].every(
    (b) => b.includes('screentip=') && b.includes('supertip=')) &&
    undoButton.includes('screentip=') && undoButton.includes('getSupertip='),
  '有按钮缺 screentip 或 supertip');

// 只撤功能区的操作，是这个按钮的全部意义。面板里点的、模型做的都不能碰——
// 那两类在对话流里各有卡片和审批记录，那才是撤它们的地方。
check('适配给卡片打上来源标记',
  chat.includes("card.dataset.source = payload.source") &&
    chat.includes("window.__chatsheetFitSource = 'ribbon'"));
check('撤销只找带功能区标记的卡片',
  chat.includes("card.dataset.source === 'ribbon'") &&
    chat.includes('function ribbonFitCards()'));
check('撤销复用卡片上现有的撤销按钮，不另写还原逻辑',
  chat.includes("querySelector('.tool-undo')") &&
    !control.includes('undo.apply'));
check('撤销跳过已经撤过的卡片',
  chat.includes("button.dataset.undone !== 'true'"));
check('重叠警告仍要二次确认，不静默盖掉后续改动',
  chat.includes("dataset.overlapWarned === 'true'") &&
    chat.includes("return warnedBefore ? 'forced' : 'clicked';"));

check('功能区撤销状态由异步回读写入缓存，回调只读缓存',
  addIn.includes('private void ApplyRibbonUndoState(string state)') &&
    addIn.includes('_ribbonUndoCount') &&
    !addIn.includes('RunScriptSync'));
check('面板释放时清零撤销状态',
  /_pane = null;[\s\S]*?_ribbonUndoCount = 0;/.test(addIn));
check('断开连接时停掉回读定时器',
  /_ribbonUndoWatch[\s\S]*?Tick -= OnRibbonUndoWatchTick[\s\S]*?Dispose\(\)/.test(addIn));

check('自动化接口暴露与功能区相同的静默入口',
  automation.includes('void FitCurrentSheetForTest()') &&
    addIn.includes('internal void FitCurrentSheetForAutomation()') &&
    addIn.includes('OnFitCurrentSheet(null)'));
check('自动化接口也能走功能区撤销并读按钮状态',
  automation.includes('void UndoRibbonActionForTest()') &&
    automation.includes('string ReadRibbonUndoButtonForTest()') &&
    addIn.includes('OnUndoRibbonAction(null)'));
check('自动化入口不另写一份适配实现',
  /FitCurrentSheetForAutomation\(\)[\s\S]*?OnFitCurrentSheet\(null\)/.test(addIn) &&
    !addIn.includes('sheet.fit') &&
    !control.includes('fit_range'));

console.log('');
console.log(`=== 功能区快捷区：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
