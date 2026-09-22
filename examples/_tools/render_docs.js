// Render OfficeSupportTool-filled HTML into high-res "document" PNGs.
// Wraps the tool's body in a white A4-like sheet on a magenta sentinel
// background, screenshots tall with Edge headless, then crops the PNG to
// the sheet bottom (pure Node zlib, no image libs).
const fs = require('fs');
const path = require('path');
const os = require('os');
const { execFileSync } = require('child_process');
const zlib = require('zlib');

const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const SHEET_W = 820;          // logical sheet width (A4-ish)
const RENDER_H = 4200;        // tall window; we crop down to content
const SCALE = 2;              // 2x => crisp

// CRC32 (PNG)
const crcTable = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();
function crc32(buf) {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = crcTable[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const len = Buffer.alloc(4); len.writeUInt32BE(data.length, 0);
  const td = Buffer.concat([Buffer.from(type, 'ascii'), data]);
  const crc = Buffer.alloc(4); crc.writeUInt32BE(crc32(td), 0);
  return Buffer.concat([len, td, crc]);
}

function parsePng(buf) {
  let off = 8; const chunks = {}; const order = [];
  while (off < buf.length) {
    const len = buf.readUInt32BE(off);
    const type = buf.slice(off + 4, off + 8).toString('ascii');
    const data = buf.slice(off + 8, off + 8 + len);
    if (type === 'IDAT') { chunks.IDAT = Buffer.concat([chunks.IDAT || Buffer.alloc(0), data]); }
    else { chunks[type] = data; }
    order.push(type);
    off += 12 + len;
  }
  const ihdr = chunks.IHDR;
  return {
    w: ihdr.readUInt32BE(0), h: ihdr.readUInt32BE(4),
    bd: ihdr[8], ct: ihdr[9], chunks, order
  };
}

function unfilter(raw, w, h, bpp) {
  const stride = w * bpp;
  const out = Buffer.alloc(h * stride);
  let pos = 0;
  for (let y = 0; y < h; y++) {
    const ft = raw[pos++];
    const row = raw.slice(pos, pos + stride); pos += stride;
    const cur = y * stride;
    const prev = y > 0 ? (y - 1) * stride : -1;
    for (let x = 0; x < stride; x++) {
      const a = x >= bpp ? out[cur + x - bpp] : 0;
      const b = prev >= 0 ? out[prev + x] : 0;
      const c = (x >= bpp && prev >= 0) ? out[prev + x - bpp] : 0;
      let v = row[x];
      switch (ft) {
        case 0: break;
        case 1: v = v + a; break;
        case 2: v = v + b; break;
        case 3: v = v + ((a + b) >> 1); break;
        case 4: { const p = a + b - c; const pa = Math.abs(p - a), pb = Math.abs(p - b), pc = Math.abs(p - c);
          v = v + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c); break; }
      }
      out[cur + x] = v & 0xff;
    }
  }
  return out;
}

function encodePng(img, w, h, bpp, pHYs) {
  const stride = w * bpp;
  const raw = Buffer.alloc(h * (stride + 1));
  let pos = 0;
  for (let y = 0; y < h; y++) {
    raw[pos++] = 0; // filter None
    img.copy(raw, pos, y * stride, (y + 1) * stride); pos += stride;
  }
  const idat = zlib.deflateSync(raw, { level: 9 });
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(w, 0); ihdr.writeUInt32BE(h, 4);
  ihdr[8] = 8; ihdr[9] = bpp === 3 ? 2 : 6; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
  const parts = [Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]), chunk('IHDR', ihdr)];
  if (pHYs) parts.push(chunk('pHYs', pHYs));
  parts.push(chunk('IDAT', idat), chunk('IEND', Buffer.alloc(0)));
  return Buffer.concat(parts);
}

// magenta sentinel = (255,0,255)
function isMagentaRow(img, y, w, bpp) {
  const base = y * w * bpp;
  for (let x = 0; x < w; x++) {
    const r = img[base + x * bpp], g = img[base + x * bpp + 1], b = img[base + x * bpp + 2];
    if (!(r > 230 && g < 40 && b > 230)) return false;
  }
  return true;
}

function cropToSheet(inPng, outPng) {
  const buf = fs.readFileSync(inPng);
  const p = parsePng(buf);
  const bpp = p.ct === 6 ? 4 : 3;
  const raw = zlib.inflateSync(p.chunks.IDAT);
  const img = unfilter(raw, p.w, p.h, bpp);
  // find last non-magenta row from bottom
  let bottom = p.h - 1;
  while (bottom > 0 && isMagentaRow(img, bottom, p.w, bpp)) bottom--;
  const cropH = bottom + 1;
  const cropped = img.slice(0, cropH * p.w * bpp);
  fs.writeFileSync(outPng, encodePng(cropped, p.w, cropH, bpp, p.chunks.pHYs));
  return { w: p.w, h: cropH };
}

function wrapSheet(innerHtml) {
  return `<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"/>
<style>html,body{margin:0;padding:0;background:#ff00ff;}
#sheet{box-sizing:border-box;width:${SHEET_W}px;margin:0 auto;background:#fff;
padding:30px 34px 34px 34px;}</style></head>
<body><div id="sheet">${innerHtml}</div></body></html>`;
}

function extractBody(html) {
  const m = html.match(/<body[^>]*>([\s\S]*?)<\/body>/i);
  return m ? m[1] : html;
}

function screenshot(htmlFile, outPng) {
  const udd = path.join(os.tmpdir(), 'edge_' + Math.random().toString(36).slice(2));
  execFileSync(EDGE, [
    '--headless=new', '--disable-gpu', '--hide-scrollbars',
    `--force-device-scale-factor=${SCALE}`,
    `--window-size=${SHEET_W},${RENDER_H}`,
    `--user-data-dir=${udd}`, '--no-first-run', '--no-default-browser-check',
    `--screenshot=${outPng}`, htmlFile
  ], { stdio: 'ignore' });
}

const SRC = path.join(__dirname, '..', '_assets', 'ost-html');
const TMP = path.join(os.tmpdir(), 'ost_render');
fs.mkdirSync(TMP, { recursive: true });

// map: html base name -> target png (relative to examples/)
const MAP = [
  ['client-invoice',     '02-documents/client-invoice/img/client-invoice.png'],
  ['service-contract',   '02-documents/service-contract/img/service-contract.png'],
  ['business-letter',    '02-documents/business-letter/img/business-letter.png'],
  ['project-proposal',   '02-documents/project-proposal/img/project-proposal.png'],
  ['meeting-minutes',    '02-documents/meeting-minutes/img/meeting-minutes.png'],
  ['employee-handbook',  '02-documents/employee-handbook/img/employee-handbook.png'],
  ['financial-report',   '05-pdf-reports/financial-report/img/financial-report.png'],
  ['market-analysis',    '05-pdf-reports/market-analysis/img/market-analysis.png'],
  ['research-report',    '05-pdf-reports/research-report/img/research-report.png'],
];

const exRoot = path.join(__dirname, '..');
for (const [base, target] of MAP) {
  const srcHtml = path.join(SRC, base + '.html');
  if (!fs.existsSync(srcHtml)) { console.log('SKIP missing', base); continue; }
  const wrapped = wrapSheet(extractBody(fs.readFileSync(srcHtml, 'utf8')));
  const wf = path.join(TMP, base + '.html');
  fs.writeFileSync(wf, wrapped);
  const shot = path.join(TMP, base + '_shot.png');
  screenshot(wf, shot);
  const outAbs = path.join(exRoot, target);
  fs.mkdirSync(path.dirname(outAbs), { recursive: true });
  const dim = cropToSheet(shot, outAbs);
  console.log('OK', base, '->', target, dim.w + 'x' + dim.h);
}
console.log('done');
