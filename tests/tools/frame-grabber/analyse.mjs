// 逐像素分析取出来的帧，用来量动效参数。
//
// 本机没有任何图像库，Read 工具也显示不了图片，所以「看视频」只能变成
// 「解码 PNG 自己算」。zlib 是 node 标准库，PNG 的 IDAT 就是 zlib 流。
// canvas 导出的是 8 位 RGBA、非隔行，只需处理这一种。
//
// 用法：
//   node analyse.mjs <帧目录> diff          相邻帧的整体差异（定位动画区间）
//   node analyse.mjs <帧目录> box <x,y,w,h> 只看某个区域的差异
//   node analyse.mjs <帧目录> ink <x,y,w,h> 报区域内墨的重心与包围盒（量位移）
//   node analyse.mjs <帧目录> ascii <文件名> <x,y,w,h> [宽]  渲染成字符画

import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { inflateSync } from 'node:zlib';

/** 解 PNG，返回 {width, height, rgba}。只支持 8 位真彩非隔行。 */
function decodePng(buffer) {
  if (buffer.readUInt32BE(0) !== 0x89504e47) { throw new Error('不是 PNG'); }

  let pos = 8;
  let width = 0;
  let height = 0;
  let bitDepth = 0;
  let colorType = 0;
  const idat = [];

  while (pos < buffer.length) {
    const len = buffer.readUInt32BE(pos);
    const type = buffer.toString('ascii', pos + 4, pos + 8);
    const data = buffer.subarray(pos + 8, pos + 8 + len);

    if (type === 'IHDR') {
      width = data.readUInt32BE(0);
      height = data.readUInt32BE(4);
      bitDepth = data[8];
      colorType = data[9];
      if (data[12] !== 0) { throw new Error('隔行 PNG 不支持'); }
    } else if (type === 'IDAT') {
      idat.push(data);
    } else if (type === 'IEND') {
      break;
    }

    pos += 12 + len;
  }

  if (bitDepth !== 8) { throw new Error(`位深 ${bitDepth} 不支持`); }
  const channels = colorType === 6 ? 4 : colorType === 2 ? 3 : 0;
  if (!channels) { throw new Error(`颜色类型 ${colorType} 不支持`); }

  const raw = inflateSync(Buffer.concat(idat));
  const stride = width * channels;
  const out = Buffer.alloc(width * height * 4);
  let prev = Buffer.alloc(stride);

  for (let y = 0; y < height; y++) {
    const filter = raw[y * (stride + 1)];
    const line = Buffer.from(raw.subarray(y * (stride + 1) + 1, (y + 1) * (stride + 1)));

    // PNG 的五种行过滤器，逐字节还原。
    for (let i = 0; i < stride; i++) {
      const a = i >= channels ? line[i - channels] : 0;
      const b = prev[i];
      const c = i >= channels ? prev[i - channels] : 0;

      switch (filter) {
        case 0: break;
        case 1: line[i] = (line[i] + a) & 0xff; break;
        case 2: line[i] = (line[i] + b) & 0xff; break;
        case 3: line[i] = (line[i] + ((a + b) >> 1)) & 0xff; break;
        case 4: {
          const p = a + b - c;
          const pa = Math.abs(p - a);
          const pb = Math.abs(p - b);
          const pc = Math.abs(p - c);
          const pred = pa <= pb && pa <= pc ? a : pb <= pc ? b : c;
          line[i] = (line[i] + pred) & 0xff;
          break;
        }
        default: throw new Error(`未知过滤器 ${filter}`);
      }
    }

    for (let x = 0; x < width; x++) {
      const from = x * channels;
      const to = (y * width + x) * 4;
      out[to] = line[from];
      out[to + 1] = line[from + 1];
      out[to + 2] = line[from + 2];
      out[to + 3] = channels === 4 ? line[from + 3] : 255;
    }

    prev = line;
  }

  return { width, height, rgba: out };
}

const dir = process.argv[2];
const mode = process.argv[3] ?? 'diff';

const files = readdirSync(dir).filter((f) => f.endsWith('.png')).sort();
if (files.length === 0) { throw new Error('目录里没有 PNG'); }

/** 从文件名里取毫秒数（f012_3456ms.png）。 */
const msOf = (name) => {
  const m = /_(\d+)ms\.png$/.exec(name);
  return m ? parseInt(m[1], 10) : -1;
};

const parseBox = (text) => {
  const [x, y, w, h] = (text ?? '').split(',').map((n) => parseInt(n, 10));
  return { x: x || 0, y: y || 0, w: w || 0, h: h || 0 };
};

/** 灰度值。 */
const gray = (img, x, y) => {
  const i = (y * img.width + x) * 4;
  return (img.rgba[i] * 299 + img.rgba[i + 1] * 587 + img.rgba[i + 2] * 114) / 1000;
};

if (mode === 'diff' || mode === 'box') {
  const box = mode === 'box' ? parseBox(process.argv[4]) : null;
  let prev = null;
  let prevName = '';

  console.log(mode === 'box'
    ? `区域 ${box.x},${box.y} ${box.w}x${box.h} 的相邻帧差异：`
    : '相邻帧的整体差异（找动画区间）：');
  console.log('');

  for (const file of files) {
    const img = decodePng(readFileSync(join(dir, file)));
    const x0 = box ? box.x : 0;
    const y0 = box ? box.y : 0;
    const x1 = box ? Math.min(box.x + box.w, img.width) : img.width;
    const y1 = box ? Math.min(box.y + box.h, img.height) : img.height;

    if (prev) {
      let changed = 0;
      let total = 0;
      let sum = 0;
      // 抽样：全像素太慢，每 2 像素取一个足够定位区间。
      for (let y = y0; y < y1; y += 2) {
        for (let x = x0; x < x1; x += 2) {
          total++;
          const d = Math.abs(gray(img, x, y) - gray(prev, x, y));
          sum += d;
          if (d > 12) { changed++; }
        }
      }
      const pct = ((changed / total) * 100).toFixed(2);
      const avg = (sum / total).toFixed(2);
      const bar = '#'.repeat(Math.min(50, Math.round(changed / total * 300)));
      console.log(
        `${String(msOf(prevName)).padStart(5)}→${String(msOf(file)).padStart(5)}ms  ` +
        `变化 ${pct.padStart(6)}%  均差 ${avg.padStart(6)}  ${bar}`);
    }

    prev = img;
    prevName = file;
  }
}

if (mode === 'where') {
  // 变化像素的包围盒与重心。动画发生在哪，这里直接给出坐标——
  // 比在 ASCII 图上目测位置可靠得多。
  let prev = null;
  let prevName = '';

  console.log('相邻帧之间发生变化的区域（定位动画元素）：');
  console.log('');

  for (const file of files) {
    const img = decodePng(readFileSync(join(dir, file)));

    if (prev) {
      let minX = Infinity;
      let maxX = -Infinity;
      let minY = Infinity;
      let maxY = -Infinity;
      let sx = 0;
      let sy = 0;
      let n = 0;

      for (let y = 0; y < img.height; y++) {
        for (let x = 0; x < img.width; x++) {
          if (Math.abs(gray(img, x, y) - gray(prev, x, y)) <= 20) { continue; }
          n++; sx += x; sy += y;
          if (x < minX) { minX = x; }
          if (x > maxX) { maxX = x; }
          if (y < minY) { minY = y; }
          if (y > maxY) { maxY = y; }
        }
      }

      if (n === 0) {
        console.log(`${String(msOf(prevName)).padStart(5)}→${String(msOf(file)).padStart(5)}ms  无变化`);
      } else {
        console.log(
          `${String(msOf(prevName)).padStart(5)}→${String(msOf(file)).padStart(5)}ms  ` +
          `盒 x ${minX}..${maxX} (${maxX - minX + 1}) y ${minY}..${maxY} (${maxY - minY + 1})  ` +
          `重心 ${(sx / n).toFixed(0)},${(sy / n).toFixed(0)}  变化像素 ${n}`);
      }
    }

    prev = img;
    prevName = file;
  }
}

if (mode === 'edge') {
  // 沿一条扫描线找元素的左右边缘，逐帧报出来——位移曲线就是它。
  //
  // 用边缘而不是整块重心：重心会被同框的鼠标指针带偏（指针自己也在动），
  // 而边缘只取一条线上的第一个/最后一个非背景像素，可以把 x 范围掐在
  // 指针之外。亚像素精度靠边缘两侧的灰度做线性插值——抖动幅度可能只有
  // 几个像素，整像素分辨率不够看出曲线形状。
  const y = parseInt(process.argv[4], 10);
  const [xa, xb] = (process.argv[5] ?? '').split(',').map((n) => parseInt(n, 10));

  console.log(`扫描线 y=${y}，x 范围 ${xa}..${xb}`);
  console.log('（背景取该线最左侧 12 像素的中位灰度，偏离超过 18 算元素）');
  console.log('');

  const rows = [];

  for (const file of files) {
    const img = decodePng(readFileSync(join(dir, file)));

    const probe = [];
    for (let x = xa; x < xa + 12; x++) { probe.push(gray(img, x, y)); }
    probe.sort((a, b) => a - b);
    const bg = probe[Math.floor(probe.length / 2)];

    const isInk = (x) => Math.abs(gray(img, x, y) - bg) > 18;

    let left = -1;
    let right = -1;
    for (let x = xa; x <= xb; x++) { if (isInk(x)) { left = x; break; } }
    for (let x = xb; x >= xa; x--) { if (isInk(x)) { right = x; break; } }

    if (left < 0) {
      console.log(`${String(msOf(file)).padStart(5)}ms  线上没有元素（底色 ${bg.toFixed(0)}）`);
      continue;
    }

    /**
     * 亚像素边缘：在跨越阈值的两像素之间线性插值。
     * dir=+1 表示从背景进入元素（左边缘），-1 表示离开（右边缘）。
     */
    const refine = (edge, dir) => {
      const outside = gray(img, edge - dir, y);
      const inside = gray(img, edge, y);
      const target = bg + Math.sign(inside - bg) * 18;
      const span = inside - outside;
      if (Math.abs(span) < 1) { return edge; }
      const frac = (target - outside) / span;
      return edge - dir + dir * Math.min(1, Math.max(0, frac));
    };

    const l = left > xa ? refine(left, 1) : left;
    const r = right < xb ? refine(right, -1) : right;
    rows.push({ ms: msOf(file), l, r, mid: (l + r) / 2, w: r - l });
  }

  const base = rows.length ? rows[0].mid : 0;
  for (const row of rows) {
    const off = row.mid - base;
    const bar = off === 0 ? '' : (off > 0 ? ' '.repeat(20) + '>'.repeat(Math.min(20, Math.round(off * 2)))
      : ' '.repeat(Math.max(0, 20 - Math.round(-off * 2))) + '<'.repeat(Math.min(20, Math.round(-off * 2))));
    console.log(
      `${String(row.ms).padStart(5)}ms  左 ${row.l.toFixed(2).padStart(8)}  右 ${row.r.toFixed(2).padStart(8)}  ` +
      `中 ${row.mid.toFixed(2).padStart(8)}  宽 ${row.w.toFixed(2).padStart(7)}  ` +
      `偏移 ${off >= 0 ? '+' : ''}${off.toFixed(2).padStart(6)}  ${bar}`);
  }
}

if (mode === 'vedge') {
  // 竖向扫描线：查有没有上下位移。抖动是纯水平还是带竖向分量，
  // 决定实现里 translate 写一个轴还是两个轴。
  const x = parseInt(process.argv[4], 10);
  const [ya, yb] = (process.argv[5] ?? '').split(',').map((n) => parseInt(n, 10));

  console.log(`竖向扫描线 x=${x}，y 范围 ${ya}..${yb}`);
  console.log('');

  const rows = [];
  for (const file of files) {
    const img = decodePng(readFileSync(join(dir, file)));

    const probe = [];
    for (let y = ya; y < ya + 12; y++) { probe.push(gray(img, x, y)); }
    probe.sort((a, b) => a - b);
    const bg = probe[Math.floor(probe.length / 2)];

    let top = -1;
    let bottom = -1;
    for (let y = ya; y <= yb; y++) { if (Math.abs(gray(img, x, y) - bg) > 18) { top = y; break; } }
    for (let y = yb; y >= ya; y--) { if (Math.abs(gray(img, x, y) - bg) > 18) { bottom = y; break; } }

    if (top < 0) {
      console.log(`${String(msOf(file)).padStart(5)}ms  线上没有元素`);
      continue;
    }
    rows.push({ ms: msOf(file), top, bottom, mid: (top + bottom) / 2, h: bottom - top });
  }

  const base = rows.length ? rows[0].mid : 0;
  for (const row of rows) {
    const off = row.mid - base;
    console.log(
      `${String(row.ms).padStart(5)}ms  上 ${String(row.top).padStart(5)}  下 ${String(row.bottom).padStart(5)}  ` +
      `中 ${row.mid.toFixed(1).padStart(7)}  高 ${String(row.h).padStart(5)}  ` +
      `竖向偏移 ${off >= 0 ? '+' : ''}${off.toFixed(1)}`);
  }
}

if (mode === 'color') {
  // 区域平均色。抖动的同时有没有变色/变淡（禁用反馈常常一起改颜色）。
  const box = parseBox(process.argv[4]);
  console.log(`区域 ${box.x},${box.y} ${box.w}x${box.h} 的平均色：`);
  console.log('');

  for (const file of files) {
    const img = decodePng(readFileSync(join(dir, file)));
    const x1 = Math.min(box.x + box.w, img.width);
    const y1 = Math.min(box.y + box.h, img.height);
    let r = 0;
    let g = 0;
    let b = 0;
    let n = 0;
    for (let y = box.y; y < y1; y++) {
      for (let x = box.x; x < x1; x++) {
        const i = (y * img.width + x) * 4;
        r += img.rgba[i]; g += img.rgba[i + 1]; b += img.rgba[i + 2]; n++;
      }
    }
    console.log(
      `${String(msOf(file)).padStart(5)}ms  rgb(${(r / n).toFixed(1)}, ${(g / n).toFixed(1)}, ${(b / n).toFixed(1)})`);
  }
}

if (mode === 'ink') {
  const box = parseBox(process.argv[4]);
  console.log(`区域 ${box.x},${box.y} ${box.w}x${box.h} 内墨的重心与包围盒：`);
  console.log('（背景取区域四角的中位灰度，偏离它超过 25 算墨）');
  console.log('');

  for (const file of files) {
    const img = decodePng(readFileSync(join(dir, file)));
    const x1 = Math.min(box.x + box.w, img.width);
    const y1 = Math.min(box.y + box.h, img.height);

    const corners = [
      gray(img, box.x, box.y), gray(img, x1 - 1, box.y),
      gray(img, box.x, y1 - 1), gray(img, x1 - 1, y1 - 1),
    ].sort((a, b) => a - b);
    const bg = (corners[1] + corners[2]) / 2;

    let sx = 0;
    let sy = 0;
    let n = 0;
    let minX = Infinity;
    let maxX = -Infinity;
    let minY = Infinity;
    let maxY = -Infinity;

    for (let y = box.y; y < y1; y++) {
      for (let x = box.x; x < x1; x++) {
        if (Math.abs(gray(img, x, y) - bg) <= 25) { continue; }
        sx += x; sy += y; n++;
        if (x < minX) { minX = x; }
        if (x > maxX) { maxX = x; }
        if (y < minY) { minY = y; }
        if (y > maxY) { maxY = y; }
      }
    }

    if (n === 0) {
      console.log(`${String(msOf(file)).padStart(5)}ms  区域内没有墨（底色 ${bg.toFixed(0)}）`);
      continue;
    }

    console.log(
      `${String(msOf(file)).padStart(5)}ms  ` +
      `重心 x=${(sx / n).toFixed(2)} y=${(sy / n).toFixed(2)}  ` +
      `包围盒 x ${minX}..${maxX} (${maxX - minX + 1}) y ${minY}..${maxY} (${maxY - minY + 1})  ` +
      `墨 ${n}`);
  }
}

if (mode === 'ascii') {
  const file = process.argv[4];
  const box = parseBox(process.argv[5]);
  const outW = parseInt(process.argv[6] ?? '96', 10);

  const img = decodePng(readFileSync(join(dir, file)));
  const x1 = Math.min(box.x + box.w, img.width);
  const y1 = Math.min(box.y + box.h, img.height);
  const bw = x1 - box.x;
  const bh = y1 - box.y;

  // 字符宽高比约 1:2，纵向多压一倍才不变形。
  const outH = Math.max(1, Math.round((bh / bw) * outW / 2));
  const ramp = ' .:-=+*#%@';

  let lo = 255;
  let hi = 0;
  for (let y = box.y; y < y1; y += 2) {
    for (let x = box.x; x < x1; x += 2) {
      const g = gray(img, x, y);
      if (g < lo) { lo = g; }
      if (g > hi) { hi = g; }
    }
  }
  const span = Math.max(1, hi - lo);

  console.log(`${file} 区域 ${box.x},${box.y} ${bw}x${bh} → ${outW}x${outH}，灰度 ${lo.toFixed(0)}..${hi.toFixed(0)}`);
  for (let ry = 0; ry < outH; ry++) {
    let line = '';
    for (let rx = 0; rx < outW; rx++) {
      // 每个输出格取块内平均。
      const fx0 = box.x + Math.floor((rx / outW) * bw);
      const fx1 = box.x + Math.floor(((rx + 1) / outW) * bw);
      const fy0 = box.y + Math.floor((ry / outH) * bh);
      const fy1 = box.y + Math.floor(((ry + 1) / outH) * bh);
      let sum = 0;
      let n = 0;
      for (let y = fy0; y < Math.max(fy0 + 1, fy1); y++) {
        for (let x = fx0; x < Math.max(fx0 + 1, fx1); x++) {
          sum += gray(img, x, y); n++;
        }
      }
      const norm = ((sum / Math.max(1, n)) - lo) / span;
      // 深色为重：屏幕录像多是浅底深字，反过来读更像原图。
      line += ramp[Math.min(ramp.length - 1, Math.max(0, Math.round((1 - norm) * (ramp.length - 1))))];
    }
    console.log(line);
  }
}
