/**
 * JM Download 图标生成器（单一来源）
 *
 * 一次生成 Web/PWA 与 Android 两套图标，几何完全一致：
 * 圆角方底 + 白色 “J” 字形。
 *
 *   node mobile/frontend/icons/build-icons.mjs
 *
 * 输出：
 *   mobile/frontend/icons     PWA / 网页图标
 *   mobile/android/Resources  Android 各密度图标
 */
import { deflateSync } from 'node:zlib';
import { writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const androidRes = resolve(here, '..', '..', 'android', 'Resources');

/* ── PNG 编码 ── */

const CRC_TABLE = (() => {
  const table = new Int32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c;
  }
  return table;
})();

function crc32(buffer) {
  let c = 0xffffffff;
  for (const byte of buffer) c = CRC_TABLE[(c ^ byte) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length, 0);
  const body = Buffer.concat([Buffer.from(type, 'latin1'), data]);
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crc32(body), 0);
  return Buffer.concat([length, body, crc]);
}

function encodePng(size, rgba) {
  const stride = size * 4 + 1;
  const raw = Buffer.alloc(stride * size);
  for (let y = 0; y < size; y++) {
    raw[y * stride] = 0;
    rgba.copy(raw, y * stride + 1, y * size * 4, (y + 1) * size * 4);
  }
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    chunk('IHDR', ihdr),
    chunk('IDAT', deflateSync(raw, { level: 9 })),
    chunk('IEND', Buffer.alloc(0))
  ]);
}

/* ── 几何（归一化坐标，y 轴向下） ── */

const lerp = (a, b, t) => a + (b - a) * t;

function distToSegment(px, py, ax, ay, bx, by) {
  const vx = bx - ax, vy = by - ay;
  const wx = px - ax, wy = py - ay;
  const len = vx * vx + vy * vy;
  const t = len === 0 ? 0 : Math.max(0, Math.min(1, (wx * vx + wy * vy) / len));
  return Math.hypot(px - (ax + t * vx), py - (ay + t * vy));
}

const distToPoint = (px, py, ax, ay) => Math.hypot(px - ax, py - ay);

/** 下半圆弧的带符号距离，圆心 (cx,cy)，半径 radius，角度区间 [0, π]。 */
function distToArc(px, py, cx, cy, radius) {
  const dx = px - cx, dy = py - cy;
  const angle = Math.atan2(dy, dx);
  if (angle >= 0 && angle <= Math.PI) return Math.abs(Math.hypot(dx, dy) - radius);
  return Math.min(distToPoint(px, py, cx + radius, cy), distToPoint(px, py, cx - radius, cy));
}

/** “J” 字形：顶部横笔 + 竖笔 + 底部半圆钩。 */
function distToGlyph(px, py) {
  const stem = distToSegment(px, py, 0.615, 0.255, 0.615, 0.585);
  const top = distToSegment(px, py, 0.500, 0.255, 0.615, 0.255);
  const hook = distToArc(px, py, 0.475, 0.585, 0.140);
  return Math.min(stem, top, hook) - 0.0425;
}

const insideRoundedRect = (px, py, radius) => {
  const ax = Math.abs(px - 0.5), ay = Math.abs(py - 0.5);
  const dx = Math.max(ax - (0.5 - radius), 0);
  const dy = Math.max(ay - (0.5 - radius), 0);
  return Math.hypot(dx, dy) <= radius;
};

const insideCircle = (px, py) => Math.hypot(px - 0.5, py - 0.5) <= 0.5;

/* ── 渲染 ── */

const SAMPLES = 3;
// shape: rounded（圆角方）| circle（圆形）| maskable（满幅，图形缩到安全区）| foreground（透明底白字，供自适应图标）
function render(size, shape) {
  const rgba = Buffer.alloc(size * size * 4);
  const scale = shape === 'maskable' || shape === 'foreground' ? 0.72 : 1;
  const paintBackground = shape !== 'foreground';
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let bgHits = 0, fgHits = 0;
      for (let sy = 0; sy < SAMPLES; sy++) {
        for (let sx = 0; sx < SAMPLES; sx++) {
          const u = (x + (sx + 0.5) / SAMPLES) / size;
          const v = (y + (sy + 0.5) / SAMPLES) / size;
          if (!paintBackground) bgHits++;
          else if (shape === 'maskable') bgHits++;
          else if (shape === 'circle' ? insideCircle(u, v) : insideRoundedRect(u, v, 0.22)) bgHits++;
          const gx = (u - 0.5) / scale + 0.5;
          const gy = (v - 0.5) / scale + 0.5;
          if (distToGlyph(gx, gy) <= 0) fgHits++;
        }
      }
      const total = SAMPLES * SAMPLES;
      const coverage = bgHits / total;
      const glyph = fgHits / total;
      const t = (x / size + y / size) / 2;
      let r = lerp(0x3d, 0x22, t), g = lerp(0x8a, 0x5f, t), b = lerp(0x66, 0x44, t);
      if (!paintBackground) { r = g = b = 255; }
      else { r = lerp(r, 255, glyph); g = lerp(g, 255, glyph); b = lerp(b, 255, glyph); }
      const offset = (y * size + x) * 4;
      rgba[offset] = Math.round(r);
      rgba[offset + 1] = Math.round(g);
      rgba[offset + 2] = Math.round(b);
      rgba[offset + 3] = paintBackground ? Math.round(coverage * 255) : Math.round(glyph * 255);
    }
  }
  return encodePng(size, rgba);
}

/** 把 PNG 打包成 ICO（Vista 起支持 PNG 压缩条目）。 */
function encodeIco(pngs) {
  const directory = Buffer.alloc(6);
  directory.writeUInt16LE(0, 0);
  directory.writeUInt16LE(1, 2);
  directory.writeUInt16LE(pngs.length, 4);
  let offset = 6 + pngs.length * 16;
  const entries = [];
  for (const { size, data } of pngs) {
    const entry = Buffer.alloc(16);
    entry[0] = size >= 256 ? 0 : size;
    entry[1] = size >= 256 ? 0 : size;
    entry.writeUInt16LE(1, 4);
    entry.writeUInt16LE(32, 6);
    entry.writeUInt32LE(data.length, 8);
    entry.writeUInt32LE(offset, 12);
    entries.push(entry);
    offset += data.length;
  }
  return Buffer.concat([directory, ...entries, ...pngs.map(p => p.data)]);
}

/* ── 输出 ── */

mkdirSync(here, { recursive: true });
const web = [
  ['icon-192.png', 192, 'rounded'],
  ['icon-512.png', 512, 'rounded'],
  ['icon-maskable-512.png', 512, 'maskable'],
  ['apple-touch-icon.png', 180, 'maskable']
];
for (const [name, size, shape] of web) {
  writeFileSync(join(here, name), render(size, shape));
  console.log(`web      ${name} (${size}x${size}, ${shape})`);
}
writeFileSync(join(here, 'icon.ico'), encodeIco(
  [64, 128, 256].map(size => ({ size, data: render(size, 'rounded') }))
));
console.log('web      icon.ico (64/128/256)');

if (existsSync(androidRes)) {
  const densities = [['mdpi', 1], ['hdpi', 1.5], ['xhdpi', 2], ['xxhdpi', 3], ['xxxhdpi', 4]];
  for (const [density, factor] of densities) {
    const dir = join(androidRes, `mipmap-${density}`);
    mkdirSync(dir, { recursive: true });
    writeFileSync(join(dir, 'ic_launcher.png'), render(Math.round(48 * factor), 'rounded'));
    writeFileSync(join(dir, 'ic_launcher_round.png'), render(Math.round(48 * factor), 'circle'));
    writeFileSync(join(dir, 'ic_launcher_foreground.png'), render(Math.round(108 * factor), 'foreground'));
  }
  console.log(`android  mipmap-*/ic_launcher[_round|_foreground] (${densities.length} 个密度)`);
}
