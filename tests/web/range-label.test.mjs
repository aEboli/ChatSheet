// A1 地址转中文行列说明的无宿主单元测试。
//
// 覆盖重点是那些容易译错、且译错后用户会照着错说明去核对数据的形态：
// 绝对引用、倒序范围、整行整列、多区域，以及不该硬猜的命名区域。
//
// 运行：node tests/web/range-label.test.mjs

import { describeRange, rangeLabel } from '../../src/web/scripts/range-label.js';

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

function equal(label, actual, expected) {
  check(label, actual === expected, `得到 ${JSON.stringify(actual)}，期望 ${JSON.stringify(expected)}`);
}

console.log('检查地址的行列说明：');

equal('单个单元格', rangeLabel('B1'), '1 行 × B 列');
equal('绝对引用同样解析', rangeLabel('$B$1'), '1 行 × B 列');
equal('矩形范围给出两段', rangeLabel('$B$2:$D$10'), '2-10 行 × B-D 列');
equal('单行多列折成一行', rangeLabel('B3:D3'), '3 行 × B-D 列');
equal('单列多行折成一列', rangeLabel('C2:C9'), '2-9 行 × C 列');
equal('两端相同视作单元格', rangeLabel('C5:C5'), '5 行 × C 列');
equal('多字母列正常解析', rangeLabel('AA2:AC4'), '2-4 行 × AA-AC 列');

// 倒序地址必须摆正：照原样输出会得到「10-2 行」，读起来像另一个范围。
equal('倒序范围摆正', rangeLabel('D10:B2'), '2-10 行 × B-D 列');
equal('倒序整列摆正', rangeLabel('D:B'), 'B-D 整列');

equal('整列不编造行号', rangeLabel('B:D'), 'B-D 整列');
equal('单列整列', rangeLabel('$B:$B'), 'B 整列');
equal('整行不编造列字母', rangeLabel('2:10'), '2-10 整行');

// 工作表前缀属于位置之外的信息，由调用方按需另附。
equal('去掉工作表前缀', rangeLabel('Sheet1!$A$1:$B$2'), '1-2 行 × A-B 列');
equal('带引号的表名同样去掉', rangeLabel("'销售 数据'!C3"), '3 行 × C 列');

equal('多区域逐段翻译', rangeLabel('B2:C3,E5'), '2-3 行 × B-C 列、5 行 × E 列');

// Excel 的 Selection.Address 对多区域选区会给每段都带上表名。
// 若在整串上按最后一个 ! 截断，前面的区域会被静默吃掉——
// 界面不会报错，只会显示成一个更小的范围。
equal(
  '多区域各自带表名时不丢区域',
  rangeLabel('Sheet1!A1,Sheet1!B2'),
  '1 行 × A 列、2 行 × B 列',
);
equal(
  '多区域混合范围与单元格',
  rangeLabel('Sheet1!$A$1:$B$2,Sheet1!$D$4'),
  '1-2 行 × A-B 列、4 行 × D 列',
);
equal(
  '超过三段折叠计数',
  rangeLabel('A1,B2,C3,D4,E5'),
  '1 行 × A 列、2 行 × B 列、3 行 × C 列、另 2 处',
);

// 猜不出含义的形态一律退回空串，让调用方显示原地址。
equal('命名区域不硬猜', rangeLabel('销售额'), '');
equal('混合形态不硬猜', rangeLabel('B2:D'), '');
// 三维引用（跨表同一位置）只描述表内坐标，表的范围由调用方另附。
equal('三维引用只译表内坐标', rangeLabel('Sheet1:Sheet2!A1'), '1 行 × A 列');
equal('空值不报错', rangeLabel(''), '');
equal('非字符串不报错', rangeLabel(null), '');
// 位数明显超过 Excel 行号上限（1048576）的一律不译。
// 注意本模块只按位数粗筛，不精确校验上限：地址来自宿主或模型，
// 真正越界的地址在解析范围时就会被宿主拒绝，面板不必重复把关。
equal('位数过长的行号不硬猜', rangeLabel('A12345678'), '');

console.log('');
console.log('检查审批卡片用的完整说明：');

equal('位置在前地址在后', describeRange('$B$2:$D$10'), '2-10 行 × B-D 列（$B$2:$D$10）');
equal('无法解析时只给原地址', describeRange('销售额'), '销售额');
equal('空地址给空串', describeRange(''), '');

console.log('');
console.log(`=== 地址行列说明：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
