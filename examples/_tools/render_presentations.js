// Render the REAL PresentationTool decks (examples/_assets/pt-html/*.html) into the book
// example PNGs at 3200x1800. The cover slide is sparse (title only), so we capture a
// content-rich slide per deck (cards / KPIs / grids) that shows the tool's design.
// Player chrome (nav buttons, counter, theme toggle) is hidden; the 16:9 frame fills the
// viewport; the target slide is forced visible with !important so the deck's own JS
// (which starts at slide 0) cannot override it during the headless screenshot.
const fs = require('fs');
const path = require('path');
const os = require('os');
const { execFileSync } = require('child_process');

const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const VW = 1600, VH = 900, SCALE = 2;

// deck id -> 1-based slide index to capture (a visually rich content slide)
const TARGET = {
  'pitch-deck': 3,
  'sales-presentation': 3,
  'training-deck': 2,
  'board-review': 2,
};

function shotCss() {
  return `<style id="shot-mode">
html,body{margin:0!important;padding:0!important;min-height:0!important;height:100vh!important;overflow:hidden!important;}
.nav-btn,.counter,.theme-toggle{display:none!important;}
.frame-16-9{width:100vw!important;max-width:100vw!important;height:100vh!important;aspect-ratio:auto!important;border-radius:0!important;box-shadow:none!important;}
</style>`;
}

// Move the deck's "active" class onto the target slide (1-based). The deck JS starts at
// slide 0 and only changes on click/keydown, so with no interaction the HTML active
// class is what renders. This avoids fragile nth-child targeting (decks have background
// layers as siblings of the slides).
function activateSlide(html, n) {
  let i = 0;
  return html.replace(/class="slide(\s[^"]*)?"/g, (m, rest) => {
    const base = (rest || '').replace(/\s*\bactive\b/g, '').trim();
    const cur = i++;
    if (cur === n - 1) return 'class="slide active' + (base ? ' ' + base : '') + '"';
    return 'class="slide' + (base ? ' ' + base : '') + '"';
  });
}

function screenshot(htmlFile, outPng) {
  const udd = path.join(os.tmpdir(), 'edgep_' + Math.random().toString(36).slice(2));
  execFileSync(EDGE, [
    '--headless=new', '--disable-gpu', '--hide-scrollbars',
    `--force-device-scale-factor=${SCALE}`,
    `--window-size=${VW},${VH}`,
    `--virtual-time-budget=4000`,
    `--user-data-dir=${udd}`, '--no-first-run', '--no-default-browser-check',
    `--screenshot=${outPng}`, htmlFile
  ], { stdio: 'ignore' });
}

const SRC = path.join(__dirname, '..', '_assets', 'pt-html');
const TMP = path.join(os.tmpdir(), 'pt_render');
fs.mkdirSync(TMP, { recursive: true });

const MAP = [
  ['pitch-deck',         '04-presentations/pitch-deck/img/pitch-deck.png'],
  ['sales-presentation', '04-presentations/sales-presentation/img/sales-presentation.png'],
  ['training-deck',      '04-presentations/training-deck/img/training-deck.png'],
  ['board-review',       '04-presentations/board-review/img/board-review.png'],
];
const exRoot = path.join(__dirname, '..');
const bookAssets = 'C:/Users/andre/OneDrive/Sorgenti/Automate-Your-Business-with-AI/assets/examples';
fs.mkdirSync(bookAssets, { recursive: true });
for (const [base, target] of MAP) {
  const srcHtml = path.join(SRC, base + '.html');
  if (!fs.existsSync(srcHtml)) { console.log('SKIP missing', base); continue; }
  const html = activateSlide(fs.readFileSync(srcHtml, 'utf8'), TARGET[base]).replace(/<\/head>/i, shotCss() + '</head>');
  const wf = path.join(TMP, base + '.html');
  fs.writeFileSync(wf, html);
  const outAbs = path.join(exRoot, target);
  fs.mkdirSync(path.dirname(outAbs), { recursive: true });
  screenshot(wf, outAbs);
  fs.copyFileSync(outAbs, path.join(bookAssets, base + '.png'));
  const b = fs.readFileSync(outAbs);
  console.log('OK', base, 'slide', TARGET[base], '->', b.readUInt32BE(16) + 'x' + b.readUInt32BE(20), (b.length / 1024).toFixed(0) + 'KB');
}
console.log('done');
