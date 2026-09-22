// Build the EN book into EPUB + PDF with pandoc (+ typst for PDF).
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const BOOK = 'C:/Users/andre/OneDrive/Sorgenti/Automate-Your-Business-with-AI';
const CH = path.join(BOOK, 'books', 'EN');
const PANDOC = 'C:/Users/andre/AppData/Local/Pandoc/pandoc.exe';
const TYPST = 'C:/Users/andre/AppData/Local/Microsoft/WinGet/Links/typst.exe';
const PUBLISH = path.join(BOOK, 'publish');
fs.mkdirSync(PUBLISH, { recursive: true });

const files = fs.readdirSync(CH);
const chapters = files.filter(f => /^ch\d/.test(f) && f.endsWith('.md')).sort(); // ch12 < ch12a < ch13
const appendices = files.filter(f => /^appendix-/.test(f) && f.endsWith('.md')).sort();
const preface = 'preface-agentbridge.md';
const ordered = [preface, ...chapters, ...appendices].map(f => path.join('books', 'EN', f));

const meta = [
  '--metadata', 'title=Automate Your Business with AI',
  '--metadata', 'subtitle=A No-Jargon Guide to the Future of Work',
  '--metadata', 'author=Graphene Lab',
  '--metadata', 'date=2026',
  '--metadata', 'lang=en',
  '--toc', '--toc-depth=2',
  '--resource-path', 'books/EN;.',
  '-f', 'markdown+east_asian_line_breaks-smart',
];

function run(args) {
  console.log('pandoc', args.slice(-1)[0]);
  execFileSync(PANDOC, args, { cwd: BOOK, stdio: 'inherit' });
}

// EPUB
run([...meta,
  '--epub-cover-image=' + path.join('assets', 'book-cover-title.png'),
  '--epub-chapter-level=2',
  ...ordered,
  '-o', path.join('publish', 'Automate-Your-Business-with-AI-EN.epub'),
]);

// PDF: pandoc -> self-contained HTML -> Edge print-to-pdf (avoids typst Windows path issues)
const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const TOOLS = 'C:\\Users\\andre\\OneDrive\\Sorgenti\\AgentBridge\\examples\\_tools';
const htmlOut = path.join(PUBLISH, '_book.html');
run([...meta,
  '--embed-resources', '--standalone',
  '--css=' + path.join(TOOLS, 'print.css'),
  '--include-before-body=' + path.join(TOOLS, 'coverbody.html'),
  ...ordered,
  '-o', htmlOut,
]);
const udd = path.join(process.env.TEMP, 'edgepdf_' + Math.random().toString(36).slice(2));
execFileSync(EDGE, [
  '--headless=new', '--disable-gpu', '--no-pdf-header-footer',
  '--user-data-dir=' + udd, '--no-first-run', '--no-default-browser-check',
  '--virtual-time-budget=8000',
  '--print-to-pdf=' + path.join(PUBLISH, 'Automate-Your-Business-with-AI-EN.pdf'),
  'file:///' + htmlOut.replace(/\\/g, '/'),
], { stdio: 'inherit' });

console.log('BUILD DONE ->', PUBLISH);
