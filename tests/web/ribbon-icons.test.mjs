// 功能区三枚图标的内容检查：文件在、尺寸对、图形真的画出来了。
//
// 为什么要按像素验：图标是二进制资源，走的是「嵌入资源 + getImage 回调」这条路。
// 生成脚本画错、保存成全透明、或者哪次改动只留下一个空文件，编译和现有测试
// 全都不会报错——功能区上只会出现一个空白按钮，而那正是这次要修的毛病。
// 名字对不对由 ribbon-fit-shortcut.test.mjs 管，这里只管「图上有没有东西」。
//
// 自己解 PNG 而不引依赖：仓库没有 package.json，node 自带 zlib 足够。
//
// 运行：node tests/web/ribbon-icons.test.mjs

import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { inflateSync } from 'node:zlib';

const here = dirname(fileURLToPath(import.meta.url));
const root = join(here, '..', '..');
const resDir = join(root, 'src', 'ChatSheet.AddIn', 'Resources');

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

/**
 * 解出 32 位 RGBA 像素。只支持颜色类型 6、位深 8、无隔行——
 * 生成脚本固定输出这一种，遇到别的形态直接报错而不是猜。
 */
function decodePng(bytes) {
  const signature = [137, 80, 78, 71, 13, 10, 26, 10];
  for (let i = 0; i < signature.length; i++) {
    if (bytes[i] !== signature[i]) { throw new Error('不是 PNG 文件'); }
  }

  let offset = 8;
  let width = 0;
  let height = 0;
  const idat = [];

  while (offset < bytes.length) {
    const length = bytes.readUInt32BE(offset);
    const type = bytes.toString('ascii', offset + 4, offset + 8);
    const data = bytes.subarray(offset + 8, offset + 8 + length);

    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      const depth = data[8];
      const colorType = data[9];
      const interlace = data[12];
      if (depth !== 8 || colorType !== 6 || interlace !== 0) {
        throw new Error(`只支持 8 位 RGBA 非隔行，实际 depth=${depth} color=${colorType} interlace=${interlace}`);
      }
    } else if (type === 'IDAT') {
      idat.push(Buffer.from(data));
    } else if (type === 'IEND') {
      break;
    }

    offset += 12 + length;
  }

  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * 4;
  const out = Buffer.alloc(stride * height);

  // 反过滤。PNG 每行首字节是过滤类型，五种都要实现：生成器用哪种由
  // 编码器自己定，只实现 0 会在换一台机器后解出花屏。
  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = raw.subarray(y * (stride + 1) + 1, y * (stride + 1) + 1 + stride);
    const cur = out.subarray(y * stride, (y + 1) * stride);
    const prev = y > 0 ? out.subarray((y - 1) * stride, y * stride) : Buffer.alloc(stride);

    for (let x = 0; x < stride; x++) {
      const a = x >= 4 ? cur[x - 4] : 0;
      const b = prev[x];
      const c = x >= 4 ? prev[x - 4] : 0;
      let value = line[x];

      switch (filter) {
        case 0: break;
        case 1: value += a; break;
        case 2: value += b; break;
        case 3: value += Math.floor((a + b) / 2); break;
        case 4: {
          const p = a + b - c;
          const pa = Math.abs(p - a);
          const pb = Math.abs(p - b);
          const pc = Math.abs(p - c);
          value += (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
          break;
        }
        default: throw new Error(`未知过滤类型 ${filter}`);
      }

      cur[x] = value & 0xff;
    }
  }

  return { width, height, pixels: out };
}

/** 不透明像素的统计：占比、外接框、四象限分布。 */
function inkStats(image) {
  const { width, height, pixels } = image;
  let ink = 0;
  let minX = width;
  let maxX = -1;
  let minY = height;
  let maxY = -1;
  const quads = [0, 0, 0, 0];
  const inks = [];

  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const alpha = pixels[(y * width + x) * 4 + 3];
      if (alpha <= 128) { continue; }

      ink += 1;
      inks.push([x, y]);
      if (x < minX) { minX = x; }
      if (x > maxX) { maxX = x; }
      if (y < minY) { minY = y; }
      if (y > maxY) { maxY = y; }

      quads[(x >= width / 2 ? 1 : 0) + (y >= height / 2 ? 2 : 0)] += 1;
    }
  }

  return {
    ink,
    ratio: ink / (width * height),
    box: [minX, minY, maxX, maxY],
    quads,
    inks,
  };
}

/**
 * 圆心附近半径 fraction×宽 之内的不透明像素数。
 *
 * 取样半径要按各自的空心大小给，不能共用一个：齿轮的内圈是描边而非填充，
 * 空心只到半径 5.5px（这是 lucide 同款比例：孔径约为整幅的 17%），
 * 取样半径给大一点就会取到内圈的描边本身，于是把一枚画对了的齿轮判成实心圆饼。
 */
function centerInk(image, fraction) {
  const { width, height, pixels } = image;
  const limit = width * fraction;
  let count = 0;

  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      if (pixels[(y * width + x) * 4 + 3] <= 128) { continue; }
      const dx = x - width / 2;
      const dy = y - height / 2;
      if (Math.sqrt(dx * dx + dy * dy) < limit) { count += 1; }
    }
  }

  return count;
}

/**
 * 取不透明像素里出现次数最多的 RGB。
 *
 * 不用「最深」：描边交叠处的反锯齿会把同一近黑舍入成 16,23,22 这类邻近值，
 * 用最深就会把一枚画对了的图标判成配色错了。众数才是整幅的墨色。
 */
function dominantInk(image) {
  const { width, height, pixels } = image;
  const counts = new Map();
  for (let y = 0; y < height; y++) {
    for (let x = 0; x < width; x++) {
      const i = (y * width + x) * 4;
      if (pixels[i + 3] < 250) { continue; }
      const key = `${pixels[i]},${pixels[i + 1]},${pixels[i + 2]}`;
      counts.set(key, (counts.get(key) ?? 0) + 1);
    }
  }

  let best = null;
  let bestCount = 0;
  for (const [key, count] of counts) {
    if (count > bestCount) {
      bestCount = count;
      const [r, g, b] = key.split(',').map(Number);
      best = { r, g, b, count };
    }
  }

  return best;
}

console.log('检查功能区图标：');
console.log('');

const ICONS = ['RibbonSettings', 'RibbonDiagnose', 'RibbonFit', 'RibbonUndo'];
const loaded = new Map();

for (const name of ICONS) {
  let image = null;
  try {
    image = decodePng(readFileSync(join(resDir, `${name}.png`)));
  } catch (error) {
    check(`${name}.png 能解出像素`, false, error.message);
    continue;
  }

  loaded.set(name, image);
  const stats = inkStats(image);

  check(`${name}.png 是 64×64`,
    image.width === 64 && image.height === 64,
    `实际 ${image.width}×${image.height}`);

  // 空白按钮是这套图标最可能的失败形态，而它不报任何错。
  // 上界同样要卡：整片涂黑也是「有内容」，但那不是线条图标。
  check(`${name}.png 画上了线条，不是空白`,
    stats.ratio > 0.04 && stats.ratio < 0.45,
    `不透明占比 ${(stats.ratio * 100).toFixed(1)}%`);

  // 图形要铺开占住画面，缩到 32px 才认得出；又不能贴边，否则 Office
  // 缩放时边缘被切。
  const [minX, minY, maxX, maxY] = stats.box;
  check(`${name}.png 图形铺满画面且不贴边`,
    minX >= 2 && minY >= 2 && maxX <= 61 && maxY <= 61 &&
      (maxX - minX) >= 40 && (maxY - minY) >= 28,
    `外接框 (${minX},${minY})-(${maxX},${maxY})`);

  // 四个象限都得有笔画：漏画一笔（例如四角图标少一个角）会让图形失衡，
  // 而总占比几乎不变，只看占比是看不出来的。
  check(`${name}.png 四个象限都有笔画`,
    stats.quads.every((q) => q > 0),
    `象限计数 ${stats.quads.join('、')}`);

  const ink = dominantInk(image);
  check(`${name}.png 用的是面板 Logo 的近黑 #111817`,
    ink !== null && ink.r === 17 && ink.g === 24 && ink.b === 23,
    ink === null ? '没有实心像素' : `实际 rgb(${ink.r},${ink.g},${ink.b})`);
}

// 各自的形状特征。这几条挡的是「三个文件都有内容，但画的是同一个东西」
// 或者生成脚本改坏了某一枚。
const settings = loaded.get('RibbonSettings');
if (settings) {
  // 齿轮内圈只描边不填充，孔内必须干净。把 DrawPath 改成 FillPath
  // 这条就会失败——那是缩小后最容易发生的退化：糊成一个实心圆点。
  const inner = centerInk(settings, 0.06);
  check('齿轮中心留空，不是实心圆饼', inner === 0, `孔内不透明像素 ${inner}`);

  // 但孔不能大到看不出内圈：稍外一点必须有描边。
  const ring = centerInk(settings, 0.14) - inner;
  check('齿轮有内圈描边', ring > 40, `内圈环带像素 ${ring}`);
}

const diagnose = loaded.get('RibbonDiagnose');
if (diagnose) {
  const stats = inkStats(diagnose);
  // 脉搏线是一条横贯的折线：竖向瘦、横向宽。
  const [minX, minY, maxX, maxY] = stats.box;
  check('脉搏线横向铺开而竖向不满幅',
    (maxX - minX) > (maxY - minY),
    `宽 ${maxX - minX} 高 ${maxY - minY}`);
}

const fit = loaded.get('RibbonFit');
if (fit) {
  const stats = inkStats(fit);
  // 四角图标的中间是真空的一大片，取样半径可以给得比齿轮大。
  const middle = centerInk(fit, 0.2);
  check('四角图标中间是空的', middle === 0, `中心区不透明像素 ${middle}`);

  const corners = [0, 0, 0, 0];
  for (const [x, y] of stats.inks) {
    const nearLeft = x < 26;
    const nearRight = x > 38;
    const nearTop = y < 26;
    const nearBottom = y > 38;
    if (nearLeft && nearTop) { corners[0] += 1; }
    if (nearRight && nearTop) { corners[1] += 1; }
    if (nearLeft && nearBottom) { corners[2] += 1; }
    if (nearRight && nearBottom) { corners[3] += 1; }
  }

  check('四个角各有一簇笔画', corners.every((c) => c > 20), `四角计数 ${corners.join('、')}`);

  // 四角之间不相连：中线附近应当没有笔画，否则画出来是个方框而不是四个角。
  // 取 ±2 而不是更宽：臂长 14 时对臂之间只留 8.7px 空隙，band 再宽就会
  // 把边缘反锯齿的半透明像素算进来，变成与几何无关的抖动。
  let onMidline = 0;
  for (const [x, y] of stats.inks) {
    if (Math.abs(x - 32) <= 2 || Math.abs(y - 32) <= 2) { onMidline += 1; }
  }
  check('四角互不相连，中线上没有笔画', onMidline === 0, `中线附近像素 ${onMidline}`);
}

const undo = loaded.get('RibbonUndo');
if (undo) {
  const stats = inkStats(undo);

  // 撤销箭头有方向，形状断言必须把方向钉住，否则左右画反了也全绿。
  // 箭头尖在左侧偏上，弧线朝右鼓出，左下角内侧是空的。
  let head = 0;
  let rightBulge = 0;
  let lowerLeftInner = 0;
  for (const [x, y] of stats.inks) {
    if (x < 22 && y < 42) { head += 1; }
    if (x > 44) { rightBulge += 1; }
    if (x < 22 && y > 46) { lowerLeftInner += 1; }
  }

  check('撤销图标的箭头尖在左侧', head > 30, `左上区像素 ${head}`);
  check('撤销图标的弧线朝右鼓出', rightBulge > 20, `右侧区像素 ${rightBulge}`);
  check('撤销图标左下角是空的（箭头朝左而非朝右）',
    lowerLeftInner === 0, `左下区像素 ${lowerLeftInner}`);
}

console.log('');
console.log(`=== 功能区图标：通过 ${passed}，失败 ${failed} ===`);
process.exit(failed === 0 ? 0 : 1);
