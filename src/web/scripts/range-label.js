// 把 Excel 的 A1 地址翻译成「行号 × 列字母」的中文说明。
//
// 为什么需要：面板里原本直接显示 $B$2:$D$10 这类地址，而它把行与列混在
// 一个字符串里，判断「改的是哪几行、哪几列」得逐字符拆。另一处显示
// 「3 行 × 2 列」，那是尺寸而非位置，两者措辞相同却含义不同，容易混淆。
// 这里统一给出位置说明（2-10 行 × B-D 列），尺寸一律另加「共」字区分。
//
// 为什么不放在加载项侧：面板还要给尚未执行的参数加说明，那时地址只是
// 模型给的字符串，加载项并未解析过；解析放在展示侧，两处措辞才一致。

/** 单元格地址，如 B2、$B$2。 */
const CELL = /^\$?([A-Za-z]{1,3})\$?([1-9][0-9]{0,6})$/;

/** 整列地址，如 B、$B。 */
const COLUMN = /^\$?([A-Za-z]{1,3})$/;

/** 整行地址，如 2、$2。 */
const ROW = /^\$?([1-9][0-9]{0,6})$/;

/** 列字母转序号，A→1。用于把倒序范围（D10:B2）摆正。 */
function columnIndex(letters) {
  let index = 0;
  for (const char of letters.toUpperCase()) {
    index = index * 26 + (char.charCodeAt(0) - 64);
  }
  return index;
}

/** 把区域两端解析成 { rows, columns } 两段范围，任一端不合法则返回 null。 */
function parseArea(area) {
  const parts = area.split(':');
  if (parts.length > 2) {
    return null;
  }

  const ends = parts.map((part) => {
    const text = part.trim();
    const cell = CELL.exec(text);
    if (cell) {
      return { column: cell[1].toUpperCase(), row: Number(cell[2]) };
    }

    const column = COLUMN.exec(text);
    if (column) {
      return { column: column[1].toUpperCase(), row: null };
    }

    const row = ROW.exec(text);
    if (row) {
      return { column: null, row: Number(row[1]) };
    }

    return null;
  });

  if (ends.some((end) => end === null)) {
    return null;
  }

  // 两端形态必须一致：B2:D 这类混合写法无法确定含义，交回原文更诚实。
  const [start, end = ends[0]] = ends;
  if ((start.column === null) !== (end.column === null)) {
    return null;
  }
  if ((start.row === null) !== (end.row === null)) {
    return null;
  }

  return {
    columns: start.column === null
      ? null
      : [start.column, end.column].sort((a, b) => columnIndex(a) - columnIndex(b)),
    rows: start.row === null ? null : [start.row, end.row].sort((a, b) => a - b),
  };
}

/** 把首尾相同的一段折成单值，如 [2, 2] → 2。 */
function span(pair) {
  const [from, to] = pair;
  return from === to ? String(from) : `${from}-${to}`;
}

/**
 * 单个区域的中文说明。
 *
 * 工作表前缀在这里去掉，而不是在整串上去一次：多区域地址的每段都可能
 * 自带前缀（Excel 的 Selection.Address 就是这样返回的），
 * 在整串上按最后一个 ! 截断会把前面的区域一并吃掉。
 */
function areaLabel(area) {
  const cut = area.lastIndexOf('!');
  const parsed = parseArea(cut >= 0 ? area.slice(cut + 1) : area);
  if (parsed === null) {
    return null;
  }

  if (parsed.rows === null) {
    return `${span(parsed.columns)} 整列`;
  }
  if (parsed.columns === null) {
    return `${span(parsed.rows)} 整行`;
  }

  // 行在前列在后：用户读地址时先定位行号，再看列字母。
  return `${span(parsed.rows)} 行 × ${span(parsed.columns)} 列`;
}

/**
 * 地址的中文位置说明，例如 $B$2:$D$10 → 2-10 行 × B-D 列。
 * 只描述表内坐标，工作表名由调用方按需另附。
 *
 * 无法解析时返回空串，由调用方决定退回显示原地址。
 * 命名区域这类形态本就不该硬猜，猜错比不译更糟。
 */
export function rangeLabel(address) {
  if (typeof address !== 'string' || address.trim() === '') {
    return '';
  }

  const areas = address.split(',').map((part) => part.trim()).filter((part) => part !== '');
  if (areas.length === 0) {
    return '';
  }

  const labels = [];
  // 多区域选区（B2:C3,E5）逐段翻译，任一段不可解析就整体放弃：
  // 只译一半会让人误以为另一半不在范围内。
  for (const area of areas.slice(0, 3)) {
    const label = areaLabel(area);
    if (label === null) {
      return '';
    }
    labels.push(label);
  }

  if (areas.length > 3) {
    labels.push(`另 ${areas.length - 3} 处`);
  }

  return labels.join('、');
}

/**
 * 位置说明加上原地址，例如 2-10 行 × B-D 列（$B$2:$D$10）。
 *
 * 审批卡片用这个形态：批准前既要看懂改哪里，也要能与编辑栏里的地址对上。
 */
export function describeRange(address) {
  const label = rangeLabel(address);
  const raw = typeof address === 'string' ? address.trim() : '';
  if (label === '') {
    return raw;
  }

  return `${label}（${raw}）`;
}
