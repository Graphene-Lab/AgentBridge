// Build a per-language cover PNG from the title-less master (assets/book-cover.png)
// with the translated title/subtitle/tag overlaid — same design as the EN cover.
// Renders each via Edge headless at 1024x1536 -> assets/book-cover-title-<LANG>.png
const fs = require('fs');
const path = require('path');
const os = require('os');
const { execFileSync } = require('child_process');

const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const ASSETS = 'C:/Users/andre/OneDrive/Sorgenti/Automate-Your-Business-with-AI/assets';
const MASTER = (ASSETS + '/book-cover.png').replace(/\\/g, '/');
const TMP = path.join(os.tmpdir(), 'covers_' + Math.random().toString(36).slice(2));
fs.mkdirSync(TMP, { recursive: true });

// title uses <br> for the line break; tag is the bottom-right strip.
const COVERS = {
  IT: {
    title: 'Automatizza il tuo<br>business con l\u2019IA',
    subtitle: 'Una guida senza gergo al futuro del lavoro',
    tag: 'Pratico &middot; Linguaggio semplice',
  },
  FR: {
    title: 'Automatisez votre<br>entreprise avec l\u2019IA',
    subtitle: 'Un guide sans jargon sur le futur du travail',
    tag: 'Pratique &middot; Langage simple',
  },
  ES: {
    title: 'Automatiza tu negocio<br>con IA',
    subtitle: 'Una gu\u00eda sin jerga sobre el futuro del trabajo',
    tag: 'Pr\u00e1ctico &middot; Lenguaje sencillo',
  },
  DE: {
    title: 'Automatisieren Sie Ihr<br>Gesch\u00e4ft mit KI',
    subtitle: 'Ein Leitfaden ohne Fachjargon f\u00fcr die Zukunft der Arbeit',
    tag: 'Praktisch &middot; Einfache Sprache',
  },
  RU: {
    title: '\u0410\u0432\u0442\u043e\u043c\u0430\u0442\u0438\u0437\u0438\u0440\u0443\u0439\u0442\u0435 \u0441\u0432\u043e\u0439<br>\u0431\u0438\u0437\u043d\u0435\u0441 \u0441 \u043f\u043e\u043c\u043e\u0449\u044c\u044e \u0418\u0418',
    subtitle: '\u0420\u0443\u043a\u043e\u0432\u043e\u0434\u0441\u0442\u0432\u043e \u0431\u0435\u0437 \u0436\u0430\u0440\u0433\u043e\u043d\u0430 \u043e \u0431\u0443\u0434\u0443\u0449\u0435\u043c \u0440\u0430\u0431\u043e\u0442\u044b',
    tag: '\u041f\u0440\u0430\u043a\u0442\u0438\u0447\u043d\u043e &middot; \u041f\u0440\u043e\u0441\u0442\u044b\u043c \u044f\u0437\u044b\u043a\u043e\u043c',
  },
};

function html(c) {
  return `<!DOCTYPE html>
<html><head><meta charset="utf-8"><style>
  html,body{margin:0;padding:0;width:1024px;height:1536px;overflow:hidden;
    font-family:'Segoe UI',Arial,Helvetica,sans-serif;}
  .bg{position:absolute;inset:0;background:url('${MASTER}') center/cover no-repeat;}
  .scrim{position:absolute;inset:0;background:linear-gradient(180deg,
      rgba(4,9,22,0.78) 0%, rgba(4,9,22,0.30) 34%, rgba(4,9,22,0.0) 52%,
      rgba(4,9,22,0.10) 72%, rgba(4,9,22,0.62) 100%);}
  .title{position:absolute;top:12%;left:8%;right:8%;color:#fff;
    font-size:70px;font-weight:800;line-height:1.08;letter-spacing:-1px;
    text-shadow:0 3px 18px rgba(0,0,0,.65);}
  .subtitle{position:absolute;top:34%;left:8%;right:8%;color:rgba(255,255,255,.94);
    font-size:28px;font-weight:400;line-height:1.3;text-shadow:0 2px 10px rgba(0,0,0,.6);}
  .rule{position:absolute;top:32.5%;left:8%;width:120px;height:5px;background:#38bdf8;border-radius:3px;}
  .brand{position:absolute;bottom:6.5%;left:8%;color:rgba(255,255,255,.9);
    font-size:24px;font-weight:600;letter-spacing:4px;text-shadow:0 2px 8px rgba(0,0,0,.6);}
  .tag{position:absolute;bottom:6.5%;right:8%;color:rgba(255,255,255,.8);
    font-size:18px;font-weight:400;letter-spacing:1px;text-shadow:0 2px 8px rgba(0,0,0,.6);}
</style></head>
<body>
  <div class="bg"></div>
  <div class="scrim"></div>
  <div class="title">${c.title}</div>
  <div class="rule"></div>
  <div class="subtitle">${c.subtitle}</div>
  <div class="brand">GRAPHENE LAB</div>
  <div class="tag">${c.tag}</div>
</body></html>`;
}

for (const [lang, c] of Object.entries(COVERS)) {
  const wf = path.join(TMP, 'cover_' + lang + '.html');
  fs.writeFileSync(wf, html(c));
  const out = path.join(ASSETS, 'book-cover-title-' + lang + '.png');
  const udd = path.join(TMP, 'edge_' + lang);
  execFileSync(EDGE, [
    '--headless=new', '--disable-gpu', '--hide-scrollbars',
    '--force-device-scale-factor=1', '--window-size=1024,1536',
    '--virtual-time-budget=3000',
    '--user-data-dir=' + udd, '--no-first-run', '--no-default-browser-check',
    '--screenshot=' + out,
    'file:///' + wf.replace(/\\/g, '/'),
  ], { stdio: 'ignore' });
  const b = fs.readFileSync(out);
  console.log('OK', lang, b.readUInt32BE(16) + 'x' + b.readUInt32BE(20), (b.length / 1024).toFixed(0) + 'KB', '->', out);
}
console.log('COVERS DONE');
