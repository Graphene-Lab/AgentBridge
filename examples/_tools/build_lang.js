// Build the book for ONE language into EPUB + PDF.
// Usage: node build_lang.js <LANG>   where LANG in {EN,IT,FR,ES,DE,RU}
// Reads books/<LANG>/*.md ; images are shared at <bookroot>/assets (kept in English).
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const LANG = (process.argv[2] || 'EN').toUpperCase();
const BOOK = 'C:/Users/andre/OneDrive/Sorgenti/Automate-Your-Business-with-AI';
const CH = path.join(BOOK, 'books', LANG);
const PANDOC = 'C:/Users/andre/AppData/Local/Pandoc/pandoc.exe';
const PUBLISH = path.join(BOOK, 'publish');
fs.mkdirSync(PUBLISH, { recursive: true });

const META = {
  EN: ['Automate Your Business with AI', 'A No-Jargon Guide to the Future of Work', 'en'],
  IT: ['Automatizza il tuo business con l\u2019IA', 'Una guida senza gergo al futuro del lavoro', 'it'],
  FR: ['Automatisez votre entreprise avec l\u2019IA', 'Un guide sans jargon sur le futur du travail', 'fr'],
  ES: ['Automatiza tu negocio con IA', 'Una gu\u00eda sin jerga sobre el futuro del trabajo', 'es'],
  DE: ['Automatisieren Sie Ihr Gesch\u00e4ft mit KI', 'Ein Leitfaden ohne Fachjargon f\u00fcr die Zukunft der Arbeit', 'de'],
  RU: ['\u0410\u0432\u0442\u043e\u043c\u0430\u0442\u0438\u0437\u0438\u0440\u0443\u0439\u0442\u0435 \u0441\u0432\u043e\u0439 \u0431\u0438\u0437\u043d\u0435\u0441 \u0441 \u043f\u043e\u043c\u043e\u0449\u044c\u044e \u0418\u0418', '\u0420\u0443\u043a\u043e\u0432\u043e\u0434\u0441\u0442\u0432\u043e \u0431\u0435\u0437 \u0436\u0430\u0440\u0433\u043e\u043d\u0430 \u043e \u0431\u0443\u0434\u0443\u0449\u0435\u043c \u0440\u0430\u0431\u043e\u0442\u044b', 'ru'],
};
if (!META[LANG]) { console.error('Unknown LANG', LANG); process.exit(2); }
const [TITLE, SUBTITLE, LANGCODE] = META[LANG];

// Per-language cover: EN keeps the original file name; others use book-cover-title-<LANG>.png
const coverImg = LANG === 'EN' ? 'book-cover-title.png' : 'book-cover-title-' + LANG + '.png';
const coverBody = path.join(PUBLISH, '_coverbody_' + LANG + '.html');
fs.writeFileSync(coverBody, '<div class="bookcover"><img src="assets/' + coverImg + '" alt="' + TITLE + '"></div>');

const files = fs.readdirSync(CH);
const chapters = files.filter(f => /^ch\d/.test(f) && f.endsWith('.md')).sort();
const appendices = files.filter(f => /^appendix-/.test(f) && f.endsWith('.md')).sort();
const preface = 'preface-agentbridge.md';
const ordered = [preface, ...chapters, ...appendices].map(f => path.join('books', LANG, f));

const meta = [
  '--metadata', 'title=' + TITLE,
  '--metadata', 'subtitle=' + SUBTITLE,
  '--metadata', 'author=Graphene Lab',
  '--metadata', 'date=2026',
  '--metadata', 'lang=' + LANGCODE,
  '--toc', '--toc-depth=2',
  '--resource-path', 'books/' + LANG + ';.',
  '-f', 'markdown+east_asian_line_breaks-smart',
];

function run(args) {
  console.log('pandoc', args.slice(-1)[0]);
  execFileSync(PANDOC, args, { cwd: BOOK, stdio: 'inherit' });
}

// EPUB
run([...meta,
  '--epub-cover-image=' + path.join('assets', coverImg),
  '--epub-chapter-level=2',
  ...ordered,
  '-o', path.join('publish', 'Automate-Your-Business-with-AI-' + LANG + '.epub'),
]);

// PDF: pandoc -> self-contained HTML -> Edge print-to-pdf
const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const TOOLS = 'C:\\Users\\andre\\OneDrive\\Sorgenti\\AgentBridge\\examples\\_tools';
const htmlOut = path.join(PUBLISH, '_book_' + LANG + '.html');
run([...meta,
  '--embed-resources', '--standalone',
  '--css=' + path.join(TOOLS, 'print.css'),
  '--include-before-body=' + coverBody,
  ...ordered,
  '-o', htmlOut,
]);
const udd = path.join(process.env.TEMP, 'edgepdf_' + LANG + '_' + Math.random().toString(36).slice(2));
execFileSync(EDGE, [
  '--headless=new', '--disable-gpu', '--no-pdf-header-footer',
  '--user-data-dir=' + udd, '--no-first-run', '--no-default-browser-check',
  '--virtual-time-budget=8000',
  '--print-to-pdf=' + path.join(PUBLISH, 'Automate-Your-Business-with-AI-' + LANG + '.pdf'),
  'file:///' + htmlOut.replace(/\\/g, '/'),
], { stdio: 'inherit' });
fs.unlinkSync(htmlOut);

console.log('BUILD ' + LANG + ' DONE ->', PUBLISH);
