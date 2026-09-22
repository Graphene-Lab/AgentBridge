// Integrate AgentBridge examples into the book markdown.
// - Copies each example PNG into the book assets/examples/<id>.png
// - Appends a "Try it with AgentBridge" section to mapped existing chapters (idempotent)
// - Writes the 4 new promotional chapters
const fs = require('fs');
const path = require('path');

const EX = 'C:/Users/andre/OneDrive/Sorgenti/AgentBridge/examples';
const BOOK = 'C:/Users/andre/OneDrive/Sorgenti/Automate-Your-Business-with-AI';
const CH = path.join(BOOK, 'books', 'EN');
const ASSETS = path.join(BOOK, 'assets', 'examples');
fs.mkdirSync(ASSETS, { recursive: true });

const catalog = JSON.parse(fs.readFileSync(path.join(EX, 'catalog.json'), 'utf8'));
const byId = {};
for (const e of catalog) byId[e.dir + '/' + e.id] = e;

// dir/id -> existing chapter filename
const MAP = {
  '02-documents/client-invoice': 'ch25-administration-and-finance.md',
  '02-documents/business-letter': 'ch25-administration-and-finance.md',
  '02-documents/service-contract': 'ch32-professional-practice.md',
  '02-documents/project-proposal': 'ch26-sales-and-marketing.md',
  '02-documents/meeting-minutes': 'ch30-it-and-leadership.md',
  '02-documents/employee-handbook': 'ch28-hr-and-people.md',
  '03-spreadsheets/monthly-budget': 'ch25-administration-and-finance.md',
  '03-spreadsheets/sales-tracker': 'ch26-sales-and-marketing.md',
  '03-spreadsheets/inventory-list': 'ch29-operations-and-production.md',
  '03-spreadsheets/kpi-dashboard': 'ch22-measuring-results-and-roi.md',
  '03-spreadsheets/timesheet': 'ch28-hr-and-people.md',
  '04-presentations/pitch-deck': 'ch26-sales-and-marketing.md',
  '04-presentations/sales-presentation': 'ch26-sales-and-marketing.md',
  '04-presentations/training-deck': 'ch28-hr-and-people.md',
  '04-presentations/board-review': 'ch30-it-and-leadership.md',
  '05-pdf-reports/market-analysis': 'ch12-where-ai-can-help-your-business.md',
  '05-pdf-reports/financial-report': 'ch22-measuring-results-and-roi.md',
  '05-pdf-reports/research-report': 'ch12-where-ai-can-help-your-business.md',
  '06-email/quick-reply': 'ch27-customer-care-and-support.md',
  '06-email/follow-up': 'ch26-sales-and-marketing.md',
  '06-email/invoice-email': 'ch25-administration-and-finance.md',
  '06-email/newsletter': 'ch26-sales-and-marketing.md',
  '06-email/inbox-summary': 'ch27-customer-care-and-support.md',
  '07-web-research/competitor-research': 'ch12-where-ai-can-help-your-business.md',
  '07-web-research/market-trends': 'ch12-where-ai-can-help-your-business.md',
  '07-web-research/due-diligence': 'ch19-connecting-ai-to-systems-you-already-use.md',
  '07-web-research/news-digest': 'ch12-where-ai-can-help-your-business.md',
  '07-web-research/product-compare': 'ch17-choosing-tools-without-being-fooled.md',
  '08-maps/delivery-route': 'ch29-operations-and-production.md',
  '08-maps/location-analysis': 'ch33-retail-store.md',
  '08-maps/service-area': 'ch34-services-and-consulting.md',
  '09-erp/order-status': 'ch19-connecting-ai-to-systems-you-already-use.md',
  '09-erp/customer-lookup': 'ch27-customer-care-and-support.md',
  '09-erp/stock-check': 'ch29-operations-and-production.md',
  '10-cad/part-design': 'ch31-small-manufacturing-business.md',
  '10-cad/assembly-check': 'ch31-small-manufacturing-business.md',
  '10-cad/technical-drawing': 'ch31-small-manufacturing-business.md',
  '15-docs-memory/ask-your-documents': 'ch14-data-the-raw-material.md',
  '15-docs-memory/find-in-archive': 'ch14-data-the-raw-material.md',
  '15-docs-memory/remember-preferences': 'ch14-data-the-raw-material.md',
  '15-docs-memory/version-history': 'ch14-data-the-raw-material.md',
  '16-web-api/web-chat': 'ch19-connecting-ai-to-systems-you-already-use.md',
  '16-web-api/officemanager-view': 'ch30-it-and-leadership.md',
  '16-web-api/http-api': 'ch19-connecting-ai-to-systems-you-already-use.md',
  '16-web-api/mcp-connector': 'ch19-connecting-ai-to-systems-you-already-use.md',
  '11-podcast/podcast-episode': 'ch26-sales-and-marketing.md',
  '11-podcast/audio-briefing': 'ch30-it-and-leadership.md',
};

// new chapters: file -> { title, intro, ids[] }
const NEWCH = {
  'ch12a-scheduled-tasks.md': {
    title: 'Scheduled Tasks: Work That Runs by Itself',
    intro: 'Most tools wait for you to ask. AgentBridge can also work on a time you agree on. ' +
      'You tell it once what to do and when, and it does it every time, without being reminded. ' +
      'This is one of the easiest ways to save time: the task runs, and the result is waiting for you.',
    ids: ['12-scheduling/daily-report', '12-scheduling/weekly-summary',
      '12-scheduling/recurring-reminder', '12-scheduling/monitor-website'],
  },
  'ch12b-your-assistant-by-voice-and-phone.md': {
    title: 'Your Assistant by Voice and by Phone',
    intro: 'You do not always sit at a desk. AgentBridge listens when you speak, reads answers ' +
      'out loud, and answers a phone call like a person. This means you can use your assistant ' +
      'while your hands are busy or when you are away from the office.',
    ids: ['13-voice-phone/dictate-memo', '13-voice-phone/hear-the-answer',
      '13-voice-phone/call-your-agent', '13-voice-phone/hands-free-while-driving'],
  },
  'ch12c-working-on-the-go-with-telegram.md': {
    title: 'Working on the Go with Telegram',
    intro: 'If you already use Telegram every day, your assistant can join you there. ' +
      'You ask in the same chat you use with your team, and the agent answers, sends files, ' +
      'and keeps everyone informed. No new app to learn.',
    ids: ['14-telegram/chat-on-the-go', '14-telegram/send-a-file', '14-telegram/team-group'],
  },
  'ch17a-meet-agentbridge.md': {
    title: 'Meet AgentBridge: Your Office Assistant',
    intro: 'Everything this book describes as automation can be done with one tool: AgentBridge. ' +
      'It is an AI assistant for small businesses that runs on your own computer. You talk to it ' +
      'in plain words, and it does the work — documents, spreadsheets, email, research, and more. ' +
      'This short chapter shows the first steps, so the examples later in the book make sense.',
    ids: ['01-getting-started/first-chat', '01-getting-started/choose-tools',
      '01-getting-started/switch-model', '01-getting-started/see-commands',
      '01-getting-started/attach-file'],
  },
};

function copyPng(e) {
  const imgDir = path.join(EX, e.dir, e.id, 'img');
  if (!fs.existsSync(imgDir)) return null;
  const png = fs.readdirSync(imgDir).find(f => f.toLowerCase().endsWith('.png'));
  if (!png) return null;
  const dest = path.join(ASSETS, e.id + '.png');
  fs.copyFileSync(path.join(imgDir, png), dest);
  return '../../assets/examples/' + e.id + '.png';
}

function block(e) {
  const img = copyPng(e);
  let s = '### ' + e.title + '\n\n';
  if (img) s += '![' + e.caption + '](' + img + ')\n*' + e.caption + '*\n\n';
  s += '**What you ask:** `' + e.command + '`\n\n' + e.result + '\n';
  if (e.recurring) s += '\n> ' + e.recurring + '\n';
  if (e.tip) s += '\n*Tip: ' + e.tip + '*\n';
  return s;
}

const BEGIN = '<!-- BEGIN agentbridge-examples -->';
const END = '<!-- END agentbridge-examples -->';

function inject(chapterFile, examples) {
  const fp = path.join(CH, chapterFile);
  if (!fs.existsSync(fp)) { console.log('MISSING chapter', chapterFile); return; }
  let txt = fs.readFileSync(fp, 'utf8');
  const section = '\n' + BEGIN + '\n\n## Try it with AgentBridge\n\n' +
    'Here is how the same job looks with AgentBridge. Each box shows the finished result and the ' +
    'one line you type to get it.\n\n' +
    examples.map(block).join('\n---\n\n') + '\n' + END + '\n';
  const b = txt.indexOf(BEGIN), en = txt.indexOf(END);
  if (b >= 0 && en >= 0) {
    txt = txt.slice(0, b) + section.trimStart() + txt.slice(en + END.length);
  } else {
    txt = txt.replace(/\s*$/, '\n') + section;
  }
  fs.writeFileSync(fp, txt);
  console.log('injected', examples.length, '->', chapterFile);
}

// group existing-chapter examples by chapter
const groups = {};
for (const [key, file] of Object.entries(MAP)) {
  const e = byId[key];
  if (!e) { console.log('NO CATALOG for', key); continue; }
  (groups[file] = groups[file] || []).push(e);
}
for (const [file, exs] of Object.entries(groups)) inject(file, exs);

// write new chapters
for (const [file, spec] of Object.entries(NEWCH)) {
  let md = '# ' + spec.title + '\n\n' + spec.intro + '\n\n';
  md += spec.ids.map(id => block(byId[id])).join('\n---\n\n') + '\n';
  md += '\n## Key Takeaways\n\n';
  md += '- You can set a job once and let AgentBridge run it, speak to it, or reach it by phone and Telegram.\n';
  md += '- The result is the same finished work you see in the pictures, delivered where you are.\n';
  md += '- You stay in control: you choose the time, the place, and who may use it.\n';
  fs.writeFileSync(path.join(CH, file), md);
  console.log('wrote new chapter', file, '(' + spec.ids.length + ' examples)');
}
console.log('DONE. PNGs in', ASSETS);
