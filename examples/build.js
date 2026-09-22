// build.js — generate all example folders (README.md + PNG) from catalog.json + IMAGES map
const fs = require('fs');
const path = require('path');
const { execFileSync } = require('child_process');
const ROOT = 'C:\\Users\\andre\\OneDrive\\Sorgenti\\AgentBridge\\examples';
const GEN = path.join(ROOT, '_tools', 'gen.js');
const TUI = path.join(ROOT, '_tools', 'tui_to_png.ps1');
const cat = JSON.parse(fs.readFileSync(path.join(ROOT, 'catalog.json'), 'utf8'));

// Visual payload per example id. kind: 'tui' (real capture) or a gen.js template type.
const IMAGES = {
  // 01 getting-started — real TUI captures
  'first-chat': {kind:'tui', src:'_assets/tui/00-welcome.txt', title:'AGENT - AI Chat Console'},
  'choose-tools': {kind:'tui', src:'_assets/tui/01-tools.txt', title:'AGENT - Agent tools'},
  'switch-model': {kind:'tui', src:'_assets/tui/05-model-setup.txt', title:'AGENT - LLM & Providers'},
  'see-commands': {kind:'tui', src:'_assets/tui/06-help.txt', title:'AGENT - Help'},
  'attach-file': {kind:'tui', src:'_assets/tui/07-files-palette.txt', title:'AGENT - Files'},

  // 02 documents — docx
  'client-invoice': {kind:'docx', title:'INVOICE', subtitle:'Acme Bookkeeping · Invoice #2026-114',
    paragraphs:['Bill to: Bright Cafe, Via Roma 12, Milan. Due within 14 days of issue.','Services rendered in July 2026. Thank you for your business.'],
    table:[['Description','Hours','Rate','Amount'],['Monthly bookkeeping','12','€45','€540'],['VAT 22%','','','€118.80'],['Total due','','','€658.80']],
    footer:'Acme Bookkeeping · P.IVA 01234567890 · Generated with AgentBridge'},
  'service-contract': {kind:'docx', title:'SERVICE AGREEMENT', subtitle:'Website Project — 3 Months',
    paragraphs:['This agreement is made between Acme Studio ("Provider") and the Client for the design and build of a business website.','The Provider will deliver the site within three months. The Client pays a 50% deposit before work begins and the balance on delivery.','Either party may end the agreement with 14 days written notice.'],
    table:[['Milestone','Deliverable','Payment'],['Start','Deposit','€3,000'],['Delivery','Live website','€3,000'],['Total','','€6,000']],
    footer:'Draft prepared with AgentBridge — for review, not legal advice'},
  'business-letter': {kind:'docx', title:'Lease Renewal Request', subtitle:'Acme Studio — Formal Letter',
    paragraphs:['Dear Mr. Bianchi,','We have greatly enjoyed occupying the premises at Via Roma 12 over the past two years. We would like to formally request a renewal of our lease for a further two years on the same terms.','We believe the location continues to serve our business well and we are keen to continue our positive relationship with you.','Thank you for considering our request. We look forward to hearing from you.'],
    footer:'Yours sincerely, Acme Studio'},
  'project-proposal': {kind:'docx', title:'Project Proposal', subtitle:'Online Shop for Bright Cafe',
    paragraphs:['Goal: give Bright Cafe a simple online store so customers can order ahead.','What we deliver: a branded online shop, product catalogue, secure checkout and order notifications.','Timeline: 8 weeks from kickoff. Price: €9,500, payable in two instalments.'],
    table:[['Phase','Weeks','Output'],['Design','2','Shop look & feel'],['Build','4','Working store'],['Launch','2','Live & trained']],
    footer:'Prepared with AgentBridge'},
  'meeting-minutes': {kind:'docx', title:'Meeting Minutes', subtitle:'Weekly Team Sync — 18 Sep 2026',
    paragraphs:['Attendees: Anna, Luca, Sara. The team reviewed the week and agreed next steps.'],
    table:[['Decision','Owner','Due'],['Launch the loyalty card','Anna','30 Sep'],['Fix the checkout bug','Luca','22 Sep'],['Order new packaging','Sara','25 Sep']],
    footer:'Minutes prepared with AgentBridge'},
  'employee-handbook': {kind:'docx', title:'Employee Handbook', subtitle:'Acme Studio — Small Team Guide',
    paragraphs:['Working hours: 9am to 6pm, Monday to Friday, with a one-hour lunch.','Holidays: 26 days per year, booked two weeks in advance.','Remote work: two days a week from home, with a stable connection and a quiet space.','Who to ask: for anything unclear, ask your team lead or Anna.'],
    footer:'Keep this in your documents area so the agent can answer from it'},

  // 03 spreadsheets — sheet
  'monthly-budget': {kind:'sheet', title:'Monthly Budget — September', cols:['Category','Planned','Actual'],
    rows:[['Rent',1200,1200],['Marketing',800,950],['Salaries',5000,5000],['Utilities',300,280],['Total',7300,7430]],
    chart:{title:'Planned vs Actual by category',labels:['Rent','Marketing','Salaries','Utilities'],values:[1200,950,5000,280],prefix:'€'}},
  'sales-tracker': {kind:'sheet', title:'Sales Tracker — Q3', cols:['Month','Channel','Revenue'],
    rows:[['July','Online',8200],['July','In store',5400],['August','Online',9100],['August','In store',5000],['September','Online',10300],['September','In store',5600]],
    chart:{title:'Revenue by channel',labels:['Online','In store'],values:[27600,16000],prefix:'€'}},
  'inventory-list': {kind:'sheet', title:'Inventory — Warehouse A', cols:['Item','Quantity','Reorder level','Supplier'],
    rows:[['Paper cups (100pk)',40,50,'PackCo'],['Lids',120,80,'PackCo'],['Napkins',30,60,'CleanSupply'],['Beans 1kg',18,25,'Roasters']],
    chart:{title:'Items below reorder level',labels:['Cups','Napkins','Beans'],values:[40,30,18]}},
  'kpi-dashboard': {kind:'sheet', title:'KPI Dashboard — September', cols:['Metric','This month','Last month'],
    rows:[['Revenue','€15,900','€13,600'],['New customers',42,35],['Costs','€7,430','€7,100'],['Profit','€8,470','€6,500']],
    chart:{title:'Revenue trend (€)',labels:['Jul','Aug','Sep'],values:[13600,14800,15900],prefix:'€'}},
  'timesheet': {kind:'sheet', title:'Weekly Timesheet — Week 38', cols:['Person','Mon','Tue','Wed','Thu','Fri','Total'],
    rows:[['Anna',8,8,7,8,6,37],['Luca',8,7,8,8,8,39],['Sara',6,8,8,7,8,37]],
    chart:{title:'Hours by person',labels:['Anna','Luca','Sara'],values:[37,39,37]}},

  // 04 presentations — slide
  'pitch-deck': {kind:'slide', kicker:'PITCH DECK', title:'Faster local delivery for small shops',
    bullets:['Small shops lose online sales to slow delivery','We connect shops to a same-day courier network','A €2.4B market growing 18% a year','Pay-per-delivery model, no upfront cost','40 shops live, 12,000 deliveries done','Raising €500K to reach 10 cities'], pageno:'1 / 6'},
  'sales-presentation': {kind:'slide', kicker:'ACME — QUARTERLY REVIEW', title:'What we built together',
    bullets:['Online shop launched in 8 weeks','Online revenue up 34% since launch','Customer wait time cut in half','Next: loyalty programme & mobile app'], pageno:'2 / 5'},
  'training-deck': {kind:'slide', kicker:'TEAM TRAINING', title:'Handling a customer refund',
    bullets:['Confirm the order and the reason','Check the refund policy window','Issue the refund in the system','Send a friendly confirmation','Log it and tell your team lead if unsure'], pageno:'1 / 4'},
  'board-review': {kind:'slide', kicker:'Q3 BOARD REVIEW', title:'Results and the road ahead',
    bullets:['Revenue €15.9K, up 17% quarter over quarter','New customers: 42, churn down to 2%','Challenge: delivery costs rising','Next quarter: automate stock and reporting'], pageno:'3 / 8'},

  // 05 pdf reports — pdf
  'market-analysis': {kind:'pdf', title:'Market Analysis: Specialty Coffee Retail', meta:'Prepared by AgentBridge · September 2026',
    sections:[{h:'1. Market size',p:'The specialty coffee retail segment reached an estimated €2.1B, growing steadily as consumers trade up for quality.'},{h:'2. Key trends',p:'Subscription models, single-origin beans and online ordering are the strongest growth drivers.'},{h:'3. Competition',p:'A few large chains dominate, but independent shops win on quality and local loyalty.'},{h:'4. Outlook',p:'Favourable for small players who pair quality products with a strong online presence.'}],
    chart:{title:'Market size (€B)',labels:['2024','2025','2026'],values:[1.6,1.85,2.1]}},
  'financial-report': {kind:'pdf', title:'Financial Summary 2025', meta:'Acme Studio · Prepared with AgentBridge',
    sections:[{h:'Overview',p:'Revenue grew steadily through the year while costs stayed controlled, lifting profit noticeably.'},{h:'Revenue and costs',p:'Total revenue €152K against costs of €88K, giving a healthy margin.'},{h:'Result',p:'Net profit of €64K, up from €41K the previous year.'}],
    chart:{title:'Net profit by year (€K)',labels:['2023','2024','2025'],values:[30,41,64],prefix:'€'}},
  'research-report': {kind:'pdf', title:'Taking a Small Bakery Online', meta:'Options and recommendation · AgentBridge',
    sections:[{h:'The question',p:'What is the simplest way for a small bakery to take orders online without heavy cost?'},{h:'Option A — Marketplace',p:'Fast to start, but fees are high and you do not own the customer relationship.'},{h:'Option B — Own simple shop',p:'A small branded store: more control, moderate setup, best long-term value.'},{h:'Recommendation',p:'Start with a simple own shop and add click-and-collect. Lower fees, direct customer relationship.'}],
    chart:{title:'First-year cost by option (€)',labels:['Marketplace','Own shop','Full platform'],values:[4200,3100,9800],prefix:'€'}},

  // 06 email — email
  'quick-reply': {kind:'email', to:'customer@example.com', subject:'Re: My order',
    body:['Hi Giulia,','Thank you for your order! We have it and will ship it tomorrow morning. You will get a tracking number by email.','Thanks for choosing us.'], from:'me@acme.com'},
  'follow-up': {kind:'email', to:'client@brightcafe.com', subject:'Checking in on your quote',
    body:['Hi Marco,','Just checking in on the quote we sent last week. Happy to answer any questions or adjust anything that does not fit your plans.','No rush — just wanted to keep the line open.'], from:'me@acme.com'},
  'invoice-email': {kind:'email', to:'accounts@brightcafe.com', subject:'Your July invoice (€658.80)',
    body:['Hi there,','Please find your July invoice attached. The total is €658.80, due within 14 days.','Thanks as always!'], attachment:'invoice-2026-114.pdf', from:'me@acme.com'},
  'newsletter': {kind:'email', to:'customers@acme.com', subject:'This month at Acme: new items + a treat',
    body:['Hi friends,','New this month: our autumn blend is here, and the loyalty card now gives every 10th coffee free.','As a thank-you, use code AUTUMN10 for 10% off your next order.','See you soon!'], from:'Acme Studio <hello@acme.com>'},
  'inbox-summary': {kind:'email', to:'(your inbox)', subject:'Your inbox summary — 3 need a reply today',
    body:['Urgent: supplier asking about the delayed shipment — reply today.','Urgent: client confirming tomorrow delivery — reply today.','Can wait: newsletter feedback, a meeting invite for next week.','Ignore: two newsletters and one promotion.'], from:'AgentBridge'},

  // 07 web research — browser
  'competitor-research': {kind:'browser', url:'http://localhost:5291/chat', title:'Competitor research', subtitle:'Three main competitors, compared',
    messages:[{from:'me',text:'Look up our three main competitors and summarise their prices and what they highlight.'},
      {from:'ai',text:'Competitor A: €4.50 avg, highlights speed. Competitor B: €3.90, highlights price. Competitor C: €5.20, highlights quality. Sources linked below.'}],
    generated:'Sources: competitor-a.com, competitor-b.com, competitor-c.com'},
  'market-trends': {kind:'browser', url:'http://localhost:5291/chat', title:'Trends in small-business e-commerce', subtitle:'Top three this year, with sources',
    messages:[{from:'me',text:'What are the top three trends in small-business e-commerce this year?'},
      {from:'ai',text:'1) Mobile-first checkout. 2) Subscriptions and repeat-delivery. 3) AI-assisted customer service. Each with a source you can open.'}],
    generated:'Sources: retailweekly.io, commerce.report, tbnews.com'},
  'due-diligence': {kind:'browser', url:'http://localhost:5291/chat', title:'Supplier check: PackCo Ltd', subtitle:'What is publicly available',
    messages:[{from:'me',text:'Research this supplier and tell me their reputation and any red flags.'},
      {from:'ai',text:'In business since 2014, generally positive reviews, on-time delivery praised. One note about slow email replies. No major red flags found.'}],
    generated:'Sources: companyregistry, review sites'},
  'news-digest': {kind:'browser', url:'http://localhost:5291/chat', title:'Today for a small logistics business', subtitle:'Short digest, with links',
    messages:[{from:'me',text:'Give me a short digest of today\u2019s news that matters to a small logistics business.'},
      {from:'ai',text:'Fuel prices eased 3%. New delivery-hour rules in the city from October. A courier strike was averted. Links to each.'}],
    generated:'Sources: logisticsdaily, transportnews'},
  'product-compare': {kind:'browser', url:'http://localhost:5291/chat', title:'Printer comparison for a small office', subtitle:'Price, running cost, reliability',
    messages:[{from:'me',text:'Compare these two printers for a small office.'},
      {from:'ai',text:'Model X: €180, cheap toner, reliable. Model Y: €140, pricier toner, more jams. Over 3 years, Model X costs less overall.'}],
    generated:'Total cost of ownership over 3 years included'},

  // 08 maps — map
  'delivery-route': {kind:'map', title:'Delivery route — today', desc:'5 stops · 24 km · est. 1h 10m',
    points:[{x:120,y:440,label:'Depot'},{x:320,y:320,label:'Bright Cafe'},{x:520,y:380,label:'Office 9'},{x:720,y:240,label:'Hotel Luna'},{x:860,y:360,label:'Shop North'}]},
  'location-analysis': {kind:'map', title:'Candidate shop locations', desc:'Two options, with surroundings noted',
    points:[{x:260,y:300,label:'Option A (high street)'},{x:720,y:360,label:'Option B (mall)'}]},
  'service-area': {kind:'map', title:'Our service area', desc:'Main towns we deliver to',
    points:[{x:200,y:260,label:'City Center'},{x:420,y:200,label:'Northgate'},{x:560,y:420,label:'Riverside'},{x:800,y:300,label:'Eastfield'}]},

  // 09 erp — browser/sheet
  'order-status': {kind:'browser', url:'http://localhost:5291/chat', title:'Order 4821', subtitle:'Status from your business system',
    messages:[{from:'me',text:'What is the status of order 4821?'},
      {from:'ai',text:'Order 4821 is packed and scheduled for delivery tomorrow. Customer: Bright Cafe. Value: €240.'}]},
  'customer-lookup': {kind:'browser', url:'http://localhost:5291/chat', title:'Customer: Maria Rossi', subtitle:'Contact and last order',
    messages:[{from:'me',text:'Find the customer Maria Rossi and show her contact and last order.'},
      {from:'ai',text:'Maria Rossi — maria@example.com, +39 333 1234567. Last order: #4810, €180, delivered 12 Sep.'}]},
  'stock-check': {kind:'sheet', title:'Stock check — SKU 3391', cols:['SKU','Item','In stock','Reserved','Available'],
    rows:[['3391','Ceramic mug',140,20,120]],
    chart:{title:'Available stock',labels:['Available'],values:[120]}},

  // 10 cad — browser/docx
  'part-design': {kind:'browser', url:'http://localhost:5291/chat', title:'Part modelled: L-bracket', subtitle:'80 × 40 mm, four holes',
    messages:[{from:'me',text:'Design a small metal bracket 80 by 40 millimetres with four mounting holes.'},
      {from:'ai',text:'Modelled an L-bracket 80×40×4 mm with four 6 mm holes at the corners. File ready to export.'}]},
  'assembly-check': {kind:'browser', url:'http://localhost:5291/chat', title:'Assembly check', subtitle:'Fit between two parts',
    messages:[{from:'me',text:'Place these two parts together and check for overlap or clearance issues.'},
      {from:'ai',text:'Parts fit with 0.5 mm clearance on the sides. One corner overlaps by 0.2 mm — recommend enlarging the hole.'}]},
  'technical-drawing': {kind:'docx', title:'Technical Drawing — L-Bracket', subtitle:'For production · all dimensions in mm',
    paragraphs:['Part: L-bracket, aluminium 6082. Thickness 4 mm.','Holes: 4 × Ø6 mm, 10 mm from each edge.','Finish: deburred, anodised.'],
    table:[['Feature','Dimension'],['Length','80 mm'],['Width','40 mm'],['Thickness','4 mm'],['Hole Ø','6 mm']],
    footer:'Drawing generated with AgentBridge (FreeCAD)'},

  // 11 podcast — browser
  'podcast-episode': {kind:'browser', url:'http://localhost:5291/chat', title:'Podcast episode ready', subtitle:'"Why small shops should go online" · 5:12',
    messages:[{from:'me',text:'Create a 5-minute podcast episode about why small shops should go online.'},
      {from:'ai',text:'Episode produced with two voices. ▶ 0:00 intro · 1:10 the problem · 2:40 the solution · 4:20 wrap-up. Audio file ready.'}]},
  'audio-briefing': {kind:'browser', url:'http://localhost:5291/chat', title:'Audio briefing ready', subtitle:'Weekly summary · 3:40',
    messages:[{from:'me',text:'Turn this week\u2019s summary into a short audio briefing.'},
      {from:'ai',text:'Audio briefing generated. ▶ 3:40 — sales, what is due, and one thing to watch. Ready to play.'}]},

  // 12 scheduling — browser
  'daily-report': {kind:'browser', url:'http://localhost:5291/chat', title:'Scheduled: daily report', subtitle:'Every weekday at 08:00',
    messages:[{from:'me',text:'Every weekday at 8am, give me a summary of yesterday\u2019s sales and any urgent emails.'},
      {from:'ai',text:'Done — scheduled for Mon–Fri at 08:00. First report will arrive tomorrow morning.'}]},
  'weekly-summary': {kind:'browser', url:'http://localhost:5291/chat', title:'Scheduled: weekly wrap-up', subtitle:'Every Friday at 17:00',
    messages:[{from:'me',text:'Every Friday at 5pm, summarise this week\u2019s sales and list what is due next week.'},
      {from:'ai',text:'Scheduled. You will get the wrap-up every Friday before you leave.'}]},
  'recurring-reminder': {kind:'browser', url:'http://localhost:5291/chat', title:'Scheduled: monthly reminder', subtitle:'On the 25th of every month',
    messages:[{from:'me',text:'Remind me on the 25th of every month that the supplier invoice is due.'},
      {from:'ai',text:'Reminder set for the 25th each month. You will get a message on the day.'}]},
  'monitor-website': {kind:'browser', url:'http://localhost:5291/chat', title:'Scheduled: daily watch', subtitle:'Checks a page, alerts on change',
    messages:[{from:'me',text:'Check this competitor\u2019s pricing page every day and tell me if anything changes.'},
      {from:'ai',text:'Watching daily. I will only message you when something actually changes.'}]},

  // 13 voice-phone — phone
  'dictate-memo': {kind:'phone', top:'VOICE NOTE', name:'AgentBridge', status:'Captured: "call the supplier about the delayed shipment tomorrow morning"'},
  'hear-the-answer': {kind:'phone', top:'SPEAKING', name:'AgentBridge', status:'Reading: "Today you have 38 orders, €4,120 total…"'},
  'call-your-agent': {kind:'phone', top:'IN CALL', name:'AgentBridge', status:'01:24 · "You agreed to deliver 20 cups to the cafe on Friday"'},
  'hands-free-while-driving': {kind:'phone', top:'HANDS-FREE', name:'AgentBridge', status:'00:51 · "Your first appointment is 10am with Bright Cafe"'},

  // 14 telegram — telegram
  'chat-on-the-go': {kind:'telegram', name:'AgentBridge',
    messages:[{from:'me',text:'How much did we sell yesterday?',time:'08:14'},{from:'ai',text:'Yesterday: €1,480 across 31 orders. Up 9% vs the day before.',time:'08:14'}]},
  'send-a-file': {kind:'telegram', name:'AgentBridge',
    messages:[{from:'me',text:'Send me the invoice you made for Bright Cafe.',time:'09:02'},{from:'ai',text:'Here it is 👇',time:'09:02'},{from:'ai',text:'📄 invoice-2026-114.pdf',time:'09:02'}]},
  'team-group': {kind:'telegram', name:'Acme Team',
    messages:[{from:'me',text:'What are today\u2019s delivery stops?',time:'07:50'},{from:'ai',text:'Today: Bright Cafe (9am), Office 9 (11am), Hotel Luna (2pm). 24 km total.',time:'07:50'}]},

  // 15 docs-memory — browser
  'ask-your-documents': {kind:'browser', url:'http://localhost:5291/chat', title:'Answered from your documents', subtitle:'Cancellation policy — printing supplier',
    messages:[{from:'me',text:'What is our cancellation policy with the printing supplier?'},
      {from:'ai',text:'From your supplier agreement: orders can be cancelled free within 24 hours; after that, 50% of the order value. Source: supplier-agreement.pdf'}]},
  'find-in-archive': {kind:'browser', url:'http://localhost:5291/chat', title:'Found in your archive', subtitle:'Hotel lobby proposal — spring 2026',
    messages:[{from:'me',text:'Find the proposal I sent to the hotel last spring about the lobby redesign.'},
      {from:'ai',text:'Found it: "Hotel Luna — Lobby Redesign Proposal", sent 14 April 2026. Opening it now.'}]},
  'remember-preferences': {kind:'browser', url:'http://localhost:5291/chat', title:'Using your saved style', subtitle:'Invoice — the way you like it',
    messages:[{from:'me',text:'Make an invoice — you know how I like them.'},
      {from:'ai',text:'Done — your usual layout, your logo, net-14 terms, no early-payment discount. Same as last time.'}]},
  'version-history': {kind:'browser', url:'http://localhost:5291/chat', title:'Version history', subtitle:'Previous draft restored',
    messages:[{from:'me',text:'Show me the previous version of the contract and restore it.'},
      {from:'ai',text:'Here is the version from 16 Sep (before the payment-term change). Restored — the newer draft is still saved too.'}]},

  // 16 web-api — browser
  'web-chat': {kind:'browser', url:'http://localhost:5291/chat', title:'AgentBridge — Web Chat', subtitle:'Your assistant in the browser',
    messages:[{from:'me',text:'Draft a thank-you note to our regular customers.'},
      {from:'ai',text:'Here is a warm thank-you note you can send. Want it shorter or more formal?'}]},
  'officemanager-view': {kind:'browser', url:'http://localhost:5291/OfficeManager', raw:
'OFFICE MANAGER — your agents at a glance\n\n  [ Desk 1 ]  default-agent      idle\n  [ Desk 2 ]  web-agent          researching…\n  [ Desk 3 ]  spreadsheet-files  idle\n  [ Desk 4 ]  email-agent        sending 1 message\n\n  Tasks scheduled: 4    Documents indexed: 312\n  Active session: 1     Last update: just now'},
  'http-api': {kind:'browser', url:'http://localhost:5291/v1/chat/completions', raw:
'POST /v1/chat/completions\nContent-Type: application/json\n\n{\n  "model": "default-agent",\n  "messages": [\n    { "role": "user", "content": "Summarise today\'s sales" }\n  ]\n}\n\n--- response ---\n{\n  "choices": [ { "message": { "role": "assistant",\n    "content": "Today: 38 orders, €4,120 total." } } ]\n}'},
  'mcp-connector': {kind:'browser', url:'http://localhost:5291/mcp', raw:
'AgentBridge as an MCP tool server\n\n{\n  "mcpServers": {\n    "agentbridge": {\n      "url": "http://localhost:5291/mcp",\n      "tools": ["chat", "documents", "email", "schedule"]\n    }\n  }\n}\n\nOther AI tools that speak MCP can now use\nAgentBridge\'s tools through this connector.'}
};

function readme(e) {
  const img = e.id + '.png';
  let s = `# ${e.title}\n\n`;
  s += `**Tool:** ${e.tool} · **How you reach it:** ${e.channel}\n\n`;
  s += `## The situation\n${e.scenario}\n\n`;
  s += `## What you ask the agent\n\`\`\`text\n${e.command}\n\`\`\`\n\n`;
  s += `![${e.caption}](img/${img})\n\n`;
  s += `## What AgentBridge does\n${e.result}\n\n`;
  if (e.recurring) s += `## On a schedule\n${e.recurring}\n\n`;
  if (e.tip) s += `## Good to know\n${e.tip}\n\n`;
  s += `---\n*Part of the **Automate Your Business with AI** example library. The full book is free — see the [examples README](../../README.md).*\n`;
  return s;
}

let count = 0;
for (const e of cat) {
  const dir = path.join(ROOT, e.dir, e.id);
  fs.mkdirSync(path.join(dir, 'img'), {recursive:true});
  fs.writeFileSync(path.join(dir, 'README.md'), readme(e), 'utf8');
  const png = path.join(dir, 'img', e.id + '.png');
  const im = IMAGES[e.id];
  if (!im) { console.log('NOIMG ' + e.id); continue; }
  try {
    const spec = Object.assign({}, im); spec.type = im.kind; delete spec.kind; spec.out = png;
    if (im.kind === 'tui') spec.src = path.join(ROOT, im.src);
    const sf = path.join(dir, '_spec.json');
    fs.writeFileSync(sf, JSON.stringify(spec), 'utf8');
    execFileSync('node', [GEN, sf], {stdio:'pipe'});
    fs.unlinkSync(sf);
    if (fs.existsSync(png)) { count++; console.log('OK ' + e.dir + '/' + e.id); }
    else console.log('NOPNG ' + e.dir + '/' + e.id);
  } catch (err) { console.log('ERR ' + e.dir + '/' + e.id + ' :: ' + (err.message||'').split('\n')[0]); }
}
console.log('TOTAL ' + count + '/' + cat.length);
