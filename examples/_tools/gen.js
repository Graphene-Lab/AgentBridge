// gen.js — render an example illustration (HTML -> PNG via Edge headless)
// Usage: node gen.js <spec.json>
// Spec: { "type": "...", "out": "abs/path.png", "w": 1400, "h": 900, "scale": 2, ...content }
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';

const esc = s => String(s == null ? '' : s)
  .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

const BASE = `
*{margin:0;padding:0;box-sizing:border-box}
body{font-family:'Segoe UI',Roboto,Arial,sans-serif;background:#e9edf2;color:#1f2733;-webkit-font-smoothing:antialiased}
.frame{display:inline-block;margin:22px;border-radius:12px;overflow:hidden;box-shadow:0 18px 50px rgba(20,30,50,.28);background:#fff}
.chrome{display:flex;align-items:center;gap:8px;height:40px;padding:0 16px;background:#f1f3f6;border-bottom:1px solid #dde2e9}
.chrome .dot{width:12px;height:12px;border-radius:50%}
.dot.r{background:#ff5f57}.dot.y{background:#febc2e}.dot.g{background:#28c840}
.urlbar{flex:1;height:26px;background:#fff;border:1px solid #d6dbe3;border-radius:14px;display:flex;align-items:center;padding:0 14px;font-size:13px;color:#5a6675;margin-left:10px}
.urlbar .lock{color:#2e9e5b;margin-right:8px}
`;

function docx(s) {
  const rows = (s.table || []).map(r =>
    `<tr>${r.map((c,i)=>`<td style="${i===0?'font-weight:600;color:#33415c':'color:#3a4658'}">${esc(c)}</td>`).join('')}</tr>`).join('');
  const table = rows ? `<table class="tbl">${rows}</table>` : '';
  const paras = (s.paragraphs || []).map(p => `<p>${esc(p)}</p>`).join('');
  return `<style>${BASE}
  .page{width:820px;min-height:1060px;background:#fff;padding:70px 78px}
  .doc-h{font-family:Georgia,'Times New Roman',serif;font-size:30px;color:#16233b;margin-bottom:6px}
  .doc-sub{font-size:14px;color:#8a94a6;letter-spacing:.4px;text-transform:uppercase;margin-bottom:26px}
  .page p{font-family:Georgia,serif;font-size:15.5px;line-height:1.62;color:#2b3648;margin-bottom:14px}
  .tbl{border-collapse:collapse;width:100%;margin:18px 0;font-family:Georgia,serif;font-size:14px}
  .tbl td{border:1px solid #d9dfe8;padding:9px 12px}
  .foot{margin-top:30px;padding-top:14px;border-top:1px solid #e4e8ef;font-size:12px;color:#9aa4b4;font-family:Georgia,serif}
  </style><div class="frame"><div class="page">
  <div class="doc-h">${esc(s.title)}</div>
  <div class="doc-sub">${esc(s.subtitle||'')}</div>
  ${paras}${table}
  <div class="foot">${esc(s.footer||'Generated with AgentBridge')}</div>
  </div></div>`;
}

function sheet(s) {
  const cols = s.cols || [];
  const head = `<tr><th class="cn"></th>${cols.map((c,i)=>`<th>${String.fromCharCode(65+i)}</th>`).join('')}</tr>`;
  const body = (s.rows||[]).map((r,ri)=>`<tr><td class="rn">${ri+1}</td>${r.map((c,ci)=>{
     const num = typeof c === 'number';
     return `<td class="${num?'num':'txt'}${ci===0?' c0':''}">${esc(num?c.toLocaleString('en-US'):c)}</td>`;}).join('')}</tr>`).join('');
  const chart = s.chart ? barChart(s.chart) : '';
  return `<style>${BASE}
  .xl{width:1180px;background:#f6f8fa}
  .xltab{display:flex;align-items:center;gap:10px;height:42px;padding:0 18px;background:#1d6f42;color:#fff;font-size:15px;font-weight:600}
  .xltab .fn{font-weight:400;opacity:.85;font-size:13px}
  .grid{border-collapse:collapse;font-size:13px;background:#fff;margin:14px 16px;box-shadow:0 1px 4px rgba(0,0,0,.08)}
  .grid th{background:#eef2f6;color:#5a6675;font-weight:600;border:1px solid #dfe5ec;padding:6px 10px;text-align:center;min-width:120px}
  .grid .cn{min-width:34px;background:#eef2f6;border:1px solid #dfe5ec}
  .grid .rn{background:#eef2f6;color:#8a94a6;text-align:center;border:1px solid #dfe5ec;min-width:34px}
  .grid td{border:1px solid #e6ebf1;padding:6px 10px}
  .grid .num{text-align:right;font-variant-numeric:tabular-nums}
  .grid .c0{font-weight:600;color:#243044}
  .charts{display:flex;gap:18px;padding:0 16px 18px}
  </style><div class="frame"><div class="xl">
  <div class="xltab">Excel <span class="fn">— ${esc(s.title)}</span></div>
  <table class="grid">${head}${body}</table>
  ${chart}
  </div></div>`;
}

function barChart(c) {
  const labels = c.labels || []; const vals = c.values || [];
  const max = Math.max(...vals, 1); const H = 220; const bw = 54; const gap = 26;
  const bars = vals.map((v,i)=>{
    const h = Math.round((v/max)*(H-30));
    return `<div style="display:flex;flex-direction:column;align-items:center;justify-content:flex-end;height:${H}px">
      <div style="font-size:12px;color:#5a6675;margin-bottom:4px">${esc(c.prefix||'')}${v.toLocaleString('en-US')}${c.suffix||''}</div>
      <div style="width:${bw}px;height:${h}px;background:linear-gradient(180deg,#3b82f6,#2563eb);border-radius:5px 5px 0 0"></div>
      <div style="font-size:12px;color:#8a94a6;margin-top:6px">${esc(labels[i]||'')}</div></div>`;
  }).join('');
  return `<div class="charts"><div style="background:#fff;border:1px solid #e6ebf1;border-radius:8px;padding:16px 20px;box-shadow:0 1px 4px rgba(0,0,0,.06)">
    <div style="font-size:14px;font-weight:600;color:#33415c;margin-bottom:12px">${esc(c.title||'Chart')}</div>
    <div style="display:flex;align-items:flex-end;gap:${gap}px">${bars}</div></div></div>`;
}

function slide(s) {
  const bullets = (s.bullets||[]).map(b=>`<li>${esc(b)}</li>`).join('');
  return `<style>${BASE}
  body{background:#20242c}
  .sl{width:1120px;height:630px;background:linear-gradient(135deg,#0f2a5c,#123a7a);color:#fff;padding:64px 72px;display:flex;flex-direction:column}
  .sl .kick{font-size:15px;letter-spacing:2px;text-transform:uppercase;color:#7fb0ff;margin-bottom:14px}
  .sl h1{font-size:46px;font-weight:700;line-height:1.1;margin-bottom:26px}
  .sl ul{list-style:none;font-size:22px;line-height:1.7}
  .sl ul li{padding-left:34px;position:relative;margin-bottom:10px;color:#dbe7ff}
  .sl ul li:before{content:'▸';position:absolute;left:0;color:#5b9bff}
  .sl .pg{margin-top:auto;font-size:14px;color:#8fb3e8;text-align:right}
  </style><div class="frame"><div class="sl">
  <div class="kick">${esc(s.kicker||'PRESENTATION')}</div>
  <h1>${esc(s.title)}</h1>
  <ul>${bullets}</ul>
  <div class="pg">${esc(s.pageno||'')}</div>
  </div></div>`;
}

function pdf(s) {
  const secs = (s.sections||[]).map(x=>`<h3>${esc(x.h)}</h3><p>${esc(x.p)}</p>`).join('');
  const chart = s.chart ? barChart(s.chart) : '';
  return `<style>${BASE}
  .pg{width:820px;min-height:1080px;background:#fff;padding:64px 72px}
  .rpt-h{font-size:30px;font-weight:700;color:#16233b;border-bottom:3px solid #2563eb;padding-bottom:12px;margin-bottom:8px}
  .rpt-meta{font-size:13px;color:#8a94a6;margin-bottom:26px}
  .pg h3{font-size:18px;color:#1e3a6e;margin:22px 0 8px}
  .pg p{font-size:14.5px;line-height:1.6;color:#33405a;margin-bottom:10px}
  .pg .charts{padding:14px 0}
  .rpt-foot{margin-top:28px;padding-top:12px;border-top:1px solid #e4e8ef;font-size:11px;color:#9aa4b4;display:flex;justify-content:space-between}
  </style><div class="frame"><div class="pg">
  <div class="rpt-h">${esc(s.title)}</div>
  <div class="rpt-meta">${esc(s.meta||'')}</div>
  ${secs}${chart}
  <div class="rpt-foot"><span>${esc(s.footL||'AgentBridge — AI report')}</span><span>Page 1 of 1</span></div>
  </div></div>`;
}

function email(s) {
  return `<style>${BASE}
  .mail{width:900px;background:#fff}
  .mbar{display:flex;align-items:center;gap:10px;height:44px;padding:0 18px;background:#0f6cbd;color:#fff;font-weight:600;font-size:15px}
  .mbody{padding:26px 34px}
  .fld{display:flex;font-size:14px;margin-bottom:10px;border-bottom:1px solid #eef1f5;padding-bottom:8px}
  .fld .k{width:78px;color:#8a94a6;font-weight:600}
  .fld .v{color:#2b3648}
  .subj{font-size:20px;font-weight:600;color:#16233b;margin:14px 0}
  .mtext{font-size:15px;line-height:1.6;color:#33405a}
  .mtext p{margin-bottom:12px}
  .btns{padding:18px 34px;border-top:1px solid #eef1f5;display:flex;gap:12px}
  .btn{padding:9px 22px;border-radius:6px;font-size:14px;font-weight:600}
  .btn.send{background:#0f6cbd;color:#fff}.btn.draft{background:#eef1f5;color:#5a6675}
  .attach{display:inline-flex;align-items:center;gap:8px;background:#f3f6fa;border:1px solid #dbe2ea;border-radius:6px;padding:8px 12px;font-size:13px;color:#3a4658;margin-top:14px}
  </style><div class="frame"><div class="mail">
  <div class="mbar">New message</div>
  <div class="mbody">
    <div class="fld"><span class="k">To</span><span class="v">${esc(s.to)}</span></div>
    <div class="fld"><span class="k">From</span><span class="v">${esc(s.from||'me@company.com')}</span></div>
    <div class="subj">${esc(s.subject)}</div>
    <div class="mtext">${(s.body||[]).map(p=>`<p>${esc(p)}</p>`).join('')}</div>
    ${s.attachment?`<div class="attach">📎 ${esc(s.attachment)}</div>`:''}
  </div>
  <div class="btns"><span class="btn send">Send</span><span class="btn draft">Save draft</span></div>
  </div></div>`;
}

function telegram(s) {
  const msgs = (s.messages||[]).map(m=>{
    const mine = m.from==='me';
    return `<div style="display:flex;justify-content:${mine?'flex-end':'flex-start'};margin:8px 0">
      <div style="max-width:70%;background:${mine?'#e7f7ff':'#fff'};border:1px solid ${mine?'#cfeefb':'#e6e9ee'};border-radius:14px;padding:10px 14px;font-size:15px;line-height:1.45;color:#222;box-shadow:0 1px 1px rgba(0,0,0,.05)">
      ${esc(m.text)}<div style="font-size:11px;color:#9aa4b4;text-align:right;margin-top:4px">${esc(m.time||'')}</div></div></div>`;
  }).join('');
  return `<style>${BASE}
  body{background:#dfe5ea}
  .tg{width:520px;background:#e7ebef;border-radius:12px;overflow:hidden}
  .tgh{display:flex;align-items:center;gap:12px;height:60px;padding:0 16px;background:#fff;border-bottom:1px solid #e0e4e9}
  .av{width:42px;height:42px;border-radius:50%;background:linear-gradient(135deg,#2aabee,#229ed9);color:#fff;display:flex;align-items:center;justify-content:center;font-weight:700;font-size:18px}
  .tgh .nm{font-weight:600;color:#222;font-size:16px}.tgh .st{font-size:12px;color:#3fa9f5}
  .tgc{padding:16px;min-height:420px}
  .tgi{display:flex;align-items:center;gap:10px;background:#fff;border-radius:22px;padding:10px 16px;margin:10px 14px;color:#9aa4b4;font-size:14px}
  </style><div class="frame"><div class="tg">
  <div class="tgh"><div class="av">A</div><div><div class="nm">${esc(s.name||'AgentBridge')}</div><div class="st">online</div></div></div>
  <div class="tgc">${msgs}</div>
  <div class="tgi">Message</div>
  </div></div>`;
}

function phone(s) {
  return `<style>${BASE}
  body{background:#10151d}
  .ph{width:380px;height:760px;background:linear-gradient(160deg,#1b2740,#0e1626);border-radius:38px;padding:40px 28px;color:#fff;display:flex;flex-direction:column;align-items:center;border:2px solid #2a3a55}
  .ph .top{font-size:13px;color:#8fa6c8;letter-spacing:1px;margin-bottom:40px}
  .pav{width:120px;height:120px;border-radius:50%;background:linear-gradient(135deg,#2aabee,#1c7fb8);display:flex;align-items:center;justify-content:center;font-size:48px;font-weight:700;margin-bottom:22px}
  .pn{font-size:26px;font-weight:600;margin-bottom:6px}
  .ps{font-size:16px;color:#7fd0ff;margin-bottom:60px}
  .acts{display:flex;gap:40px;margin-top:auto;margin-bottom:30px}
  .act{width:66px;height:66px;border-radius:50%;display:flex;flex-direction:column;align-items:center;justify-content:center;font-size:11px;color:#cfe0f5;gap:4px}
  .act.end{background:#e5484d}.act.on{background:#2b3a55}
  </style><div class="frame"><div class="ph">
  <div class="top">${esc(s.top||'INCOMING CALL')}</div>
  <div class="pav">A</div>
  <div class="pn">${esc(s.name||'AgentBridge')}</div>
  <div class="ps">${esc(s.status||'00:42')}</div>
  <div class="acts">
    <div class="act on">🎙<span>Mute</span></div>
    <div class="act on">🔢<span>Keypad</span></div>
    <div class="act end">📞<span>End</span></div>
  </div>
  </div></div>`;
}

function browser(s) {
  return `<style>${BASE}
  .page{padding:26px 30px;min-height:560px;background:#fff}
  .h1{font-size:22px;font-weight:700;color:#16233b;margin-bottom:4px}
  .sub{font-size:14px;color:#8a94a6;margin-bottom:20px}
  .chat{display:flex;flex-direction:column;gap:12px}
  .msg{max-width:78%;padding:12px 16px;border-radius:14px;font-size:15px;line-height:1.5}
  .msg.u{align-self:flex-end;background:#2563eb;color:#fff}
  .msg.a{align-self:flex-start;background:#f1f4f8;color:#2b3648;border:1px solid #e3e8ef}
  .gen{margin-top:18px;font-size:12px;color:#9aa4b4}
  .raw{font-family:'Cascadia Mono',Consolas,monospace;font-size:13px;background:#0f1622;color:#cdd6f4;padding:18px 20px;border-radius:8px;white-space:pre-wrap;line-height:1.5}
  </style><div class="frame" style="width:${s.w||1000}px">
  <div class="chrome"><span class="dot r"></span><span class="dot y"></span><span class="dot g"></span>
    <div class="urlbar"><span class="lock">🔒</span>${esc(s.url||'http://localhost:5291/')}</div></div>
  <div class="page">
    ${s.raw!==undefined?`<div class="raw">${esc(s.raw)}</div>`:`
    <div class="h1">${esc(s.title||'')}</div>
    <div class="sub">${esc(s.subtitle||'')}</div>
    <div class="chat">${(s.messages||[]).map(m=>`<div class="msg ${m.from==='me'?'u':'a'}">${esc(m.text)}</div>`).join('')}</div>
    ${s.generated?`<div class="gen">${esc(s.generated)}</div>`:''}`}
  </div></div>`;
}

function map(s) {
  const pts = s.points||[];
  const W=1000,H=560;
  const path = pts.map((p,i)=>`${i===0?'M':'L'} ${p.x} ${p.y}`).join(' ');
  const markers = pts.map((p,i)=>`<g><circle cx="${p.x}" cy="${p.y}" r="11" fill="${i===0?'#2563eb':'#e5484d'}" stroke="#fff" stroke-width="3"/><text x="${p.x}" y="${p.y+5}" font-size="12" fill="#fff" text-anchor="middle" font-weight="700">${i+1}</text><text x="${p.x+16}" y="${p.y+5}" font-size="14" fill="#33415c">${esc(p.label||'')}</text></g>`).join('');
  return `<style>${BASE}
  .mapwrap{position:relative;width:${W}px;height:${H}px;background:#e8eee6}
  svg{display:block}
  .mapcard{position:absolute;top:16px;left:16px;background:#fff;border-radius:10px;padding:14px 18px;box-shadow:0 4px 14px rgba(0,0,0,.15)}
  .mapcard .t{font-size:16px;font-weight:700;color:#16233b}.mapcard .d{font-size:13px;color:#5a6675;margin-top:4px}
  </style><div class="frame"><div class="mapwrap">
  <svg width="${W}" height="${H}">
    <rect width="${W}" height="${H}" fill="#eef2ea"/>
    ${Array.from({length:12},(_,i)=>`<line x1="${i*90}" y1="0" x2="${i*90}" y2="${H}" stroke="#dde5d8" stroke-width="1"/>`).join('')}
    ${Array.from({length:8},(_,i)=>`<line x1="0" y1="${i*80}" x2="${W}" y2="${i*80}" stroke="#dde5d8" stroke-width="1"/>`).join('')}
    <path d="${path}" fill="none" stroke="#2563eb" stroke-width="5" stroke-linecap="round" stroke-dasharray="2 0"/>
    ${markers}
  </svg>
  <div class="mapcard"><div class="t">${esc(s.title||'Route')}</div><div class="d">${esc(s.desc||'')}</div></div>
  </div></div>`;
}

function tui(s) {
  const text = fs.readFileSync(s.src, 'utf8').replace(/\r/g,'').replace(/\s+$/,'');
  const lines = text.split('\n');
  const hl = ln => (ln.indexOf('tools:')>=0 || /^\u2502?\u2502?\u25cf/.test(ln) || ln.indexOf('F1 help')>=0);
  const body = lines.map(ln => hl(ln) ? '<span class="hl">'+esc(ln)+'</span>' : esc(ln)).join('\n');
  const maxlen = lines.reduce((m,l)=>Math.max(m,l.length),0);
  const w = Math.round(maxlen*9.7)+60, h = Math.round(lines.length*20.5)+90;
  s.w = w; s.h = h;
  return `<style>${BASE}
  body{background:#0b0e14;font-family:'Cascadia Mono','Consolas','Courier New',monospace}
  .win{display:inline-block;background:#14181f;border:1px solid #2a3140;border-radius:10px;overflow:hidden;box-shadow:0 12px 40px rgba(0,0,0,.6);margin:18px}
  .bar{display:flex;align-items:center;gap:8px;height:34px;padding:0 14px;background:#1b212c;border-bottom:1px solid #2a3140}
  .dot{width:12px;height:12px;border-radius:50%}
  .r{background:#ff5f57}.y{background:#febc2e}.g{background:#28c840}
  .tt{margin-left:10px;color:#8b97ab;font-size:14px;font-family:'Segoe UI',Arial,sans-serif}
  .scr{padding:10px 12px 14px 12px}
  pre{color:#d7deeb;font-size:16px;line-height:1.28;white-space:pre}
  .hl{color:#7ee787}
  </style><div class="win"><div class="bar"><span class="dot r"></span><span class="dot y"></span><span class="dot g"></span><span class="tt">${esc(s.title||'AGENT - AI Chat Console')}</span></div><div class="scr"><pre>${body}</pre></div></div>`;
}

const T = { docx, sheet, slide, pdf, email, telegram, phone, browser, map, tui };

const spec = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const fn = T[spec.type];
if (!fn) { console.error('unknown type ' + spec.type); process.exit(1); }
const body = fn(spec);
const w = spec.w || 1200, h = spec.h || 820, scale = spec.scale || 2;
const html = '<!doctype html><html><head><meta charset="utf-8"></head><body>' + body + '</body></html>';
const tmp = spec.out.replace(/\.png$/i, '.gen.html');
fs.writeFileSync(tmp, html, 'utf8');
const udd = path.join(require('os').tmpdir(), 'abgen-' + process.pid + '-' + Math.random().toString(36).slice(2));
try {
  execFileSync(EDGE, ['--headless=new','--disable-gpu','--hide-scrollbars',
    '--no-first-run','--no-default-browser-check','--user-data-dir=' + udd,
    '--force-device-scale-factor='+scale, '--window-size='+w+','+h,
    '--screenshot='+spec.out, 'file:///'+tmp.replace(/\\/g,'/')], {stdio:'ignore'});
} catch (e) { /* Edge may exit non-zero on cleanup; png check below decides */ }
setTimeout(()=>{ try{fs.unlinkSync(tmp);}catch(e){}
  try{ fs.rmSync(udd, {recursive:true, force:true}); }catch(e){}
  if (fs.existsSync(spec.out)) console.log('OK '+spec.out+' '+fs.statSync(spec.out).size);
  else { console.log('FAIL '+spec.out); process.exit(1); }
}, 600);
