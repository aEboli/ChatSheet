// Word/WPS 与 Excel 共用面板时的宿主文案回归测试。
//
// 这里锁住的是纯文案选择逻辑，不启动 WebView2；页面宿主探测失败时，
// Excel 文案必须保持原样，Word 文案也不能继续显示表格操作示例。

import {
  binaryAttachmentHint,
  encodingSaveHint,
  isWordOfficeHost,
  welcomeCopy,
} from '../../src/web/scripts/host-ui.js';
import fs from 'node:fs';

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

const word = { hostKind: 'MicrosoftWord' };
const writer = { hostMode: 'word' };
const excel = { hostKind: 'MicrosoftExcel' };
const excelRibbon = fs.readFileSync('src/ChatSheet.AddIn/Resources/Ribbon.xml', 'utf8');
const excelComIds = fs.readFileSync('src/ChatSheet.AddIn/ComIds.cs', 'utf8');
const wordRibbon = fs.readFileSync('src/ChatWord.AddIn/Resources/Ribbon.xml', 'utf8');
const wordComIds = fs.readFileSync('src/ChatWord.AddIn/ComIds.cs', 'utf8');

console.log('检查 Word/Excel 宿主文案：');

check('识别 Microsoft Word 宿主', isWordOfficeHost(word));
check('识别 WPS Writer 宿主模式', isWordOfficeHost(writer));
check('Excel 不被识别为 Word 宿主', !isWordOfficeHost(excel));
check('Excel Ribbon 产品入口使用 Office-helper', excelRibbon.includes('label="Office-helper"') && !excelRibbon.includes('label="ChatSheet"'));
check('Word Ribbon 产品入口使用 Office-helper', wordRibbon.includes('label="Office-helper"'));
check('旧 Excel COM 标识保持兼容', excelComIds.includes('"ChatSheet.AddIn"') && excelComIds.includes('"ChatSheet.TaskPane"'));
check('Excel 与 Word 窗格标题统一为 Office-helper',
  /PaneTitle = "Office-helper"/.test(excelComIds) && /PaneTitle = "Office-helper"/.test(wordComIds));
check('Word 欢迎标题使用文档助手', welcomeCopy(word).title === '我是 Office-helper，你的文档助手');
check(
  'Word 欢迎正文不带工作簿示例',
  welcomeCopy(word).body.includes('当前文档') && !welcomeCopy(word).body.includes('当前工作簿'),
);
check(
  'Excel 欢迎正文保持表格示例',
  welcomeCopy(excel).title === '我是 Office-helper，你的表格助手' &&
    welcomeCopy(excel).body.includes('当前工作簿'),
);
check(
  'Word 文档附件建议使用当前 Word 文档',
  binaryAttachmentHint('.docx', word).includes('Word 或 WPS Writer'),
);
check(
  'Excel 文档附件建议仍允许另存文本',
  binaryAttachmentHint('.docx', excel).includes('另存为 txt 或 md'),
);
check(
  'Excel 表格附件提示保持原文',
  binaryAttachmentHint('.xlsx', excel).includes('直接在 Excel 里打开'),
);
check(
  '编码错误提示跟随宿主',
  encodingSaveHint(word).includes('Word/WPS Writer') && encodingSaveHint(excel).includes('Excel'),
);

console.log('');
console.log(`=== Word/Excel 宿主文案：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
