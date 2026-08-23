// Unit tests for the pure functions added/modified in GiraffeAI/index.html
// These functions are extracted verbatim from the page and evaluated in isolation.
const fs = require('fs');
const vm = require('vm');

const html = fs.readFileSync('C:/Users/andre/OneDrive/Sorgenti/GiraffeAI/index.html', 'utf8');
const scriptStart = html.indexOf('<script>');
const scriptEnd = html.indexOf('</script>');
const js = html.substring(scriptStart + 8, scriptEnd)
    // Don't execute the PWA bootstrap calls at the end of the script; the function
    // declarations above them are still hoisted so window.installPwa=iP resolves.
    .replace('registerServiceWorker();createManifest();setupPwaInstall();', 'void 0;')
    // Inject an export probe INSIDE the IIFE, right after init().
    .replace('window.installPwa=iP;init()})();', 'window.installPwa=iP;init();window.__internals={isTextualFile,readFileText,uploadFiles,fileToBase64,buildMessageWithAttachments,buildAPIMessagesWithFiles,TEXTUAL_EXTS,TEXTUAL_NAMES,ALLOWED_TYPES,setPending:(a)=>{pendingAttachments=a},setD:(d)=>{D=d},getChat:()=>gACR()};})();');
// Extract only the functions we want to test by slicing the IIFE body.
// The IIFE is (function(){ ... })(); — we grab everything inside and rely on
// the const definitions being hoisted before our test code runs (they aren't,
// so we eval the whole thing in a sandbox with DOM stubs and expose internals).
const makeEl = (id) => ({
    id, textContent: '', innerHTML: '', className: '', value: '', checked: false,
    style: {}, disabled: false, placeholder: '', rows: 0,
    querySelector: () => null, querySelectorAll: () => [],
    classList: { toggle() {} }, setAttribute() {}, appendChild() {}, focus() {},
    scrollHeight: 0, scrollTop: 0, addEventListener() {},
});
const elements = {};
const sandbox = {
    console,
    TextDecoder,
    ReadableStream,
    AbortSignal,
    AbortController,
    fetch: () => { throw new Error('no network in tests'); },
    navigator: { language: 'en-US' },
    location: { hostname: 'localhost', port: '8000', origin: 'http://localhost:8000' },
    document: {
        querySelectorAll: () => [],
        getElementById: (id) => { if (!elements[id]) elements[id] = makeEl(id); return elements[id]; },
        createElement: () => makeEl('dyn'),
        body: { classList: { toggle() {} } },
        head: { appendChild() {} },
        addEventListener() {},
    },
    localStorage: { getItem: () => null, setItem() {} },
    FormData: class { append() {} },
    window: {},
    URL: Object.assign(function () {}, { createObjectURL: () => 'blob:stub', revokeObjectURL() {} }),
    Blob,
    setTimeout,
    requestAnimationFrame: (fn) => setTimeout(fn, 0),
    confirm: () => true,
    addEventListener() {},
    // PWA bootstrap globals (defined after the IIFE; stubs avoid evaluation issues)
    registerServiceWorker() {}, createManifest() {}, setupPwaInstall() {}, iP() {},
};
sandbox.window = sandbox;
sandbox.self = sandbox;
sandbox.globalThis = sandbox;

const ctx = vm.createContext(sandbox);
vm.runInContext(js, ctx);
const I = sandbox.window.__internals;

let pass = 0, fail = 0;
function T(name, cond, detail) {
  if (cond) { pass++; console.log('  OK   ' + name); }
  else { fail++; console.log('  FAIL ' + name + (detail ? '  ' + detail : '')); }
}

console.log('=== isTextualFile ===');
T('note.md is textual', I.isTextualFile('note.md'));
T('Dockerfile (no ext) is textual', I.isTextualFile('Dockerfile'));
T('makefile is textual', I.isTextualFile('Makefile'));
T('config.yaml is textual', I.isTextualFile('config.yaml'));
T('script.ts is textual', I.isTextualFile('script.ts'));
T('readme is textual (name list)', I.isTextualFile('README'));
T('report.pdf is NOT textual', !I.isTextualFile('report.pdf'));
T('photo.JPG is NOT textual', !I.isTextualFile('photo.JPG'));
T('data.xlsx is NOT textual', !I.isTextualFile('data.xlsx'));
T('mixed case .TXT is textual', I.isTextualFile('NOTES.TXT'));
T('group_vars is textual (name list)', I.isTextualFile('group_vars'));
// Regression: a BINARY file whose name starts like a textual name must NOT be treated as text.
T('readme.xlsx is NOT textual (binary despite textual name)', !I.isTextualFile('readme.xlsx'));
T('changelog.pdf is NOT textual (binary despite textual name)', !I.isTextualFile('changelog.pdf'));
T('bugs.dat is NOT textual (binary despite textual name)', !I.isTextualFile('bugs.dat'));

console.log('=== buildMessageWithAttachments ===');
T('no files -> returns content unchanged', I.buildMessageWithAttachments('ciao', []) === 'ciao');
const txtOut = I.buildMessageWithAttachments('domanda', [{ name: 'note.md', textContent: '# Titolo', textual: true, inline: true }]);
T('textual file content embedded with fence', txtOut.includes('```md') && txtOut.includes('# Titolo') && txtOut.includes('domanda'));
const imgOut = I.buildMessageWithAttachments('domanda', [{ name: 'f.png', type: 'image/png', size: 100, inline: true, image: true, base64: 'abc' }]);
T('image -> [Image: name]', imgOut.includes('[Image: f.png]'));
const upOut = I.buildMessageWithAttachments('domanda', [{ name: 'r.pdf', type: 'application/pdf', size: 100, inline: false, document: true, file_id: 'file-1' }]);
T('uploaded doc -> uploaded as file_id', upOut.includes('uploaded as file-1'));

console.log('=== buildAPIMessagesWithFiles (per format) ===');
function msgsWith(lastContent) { return [{ role: 'system', content: 'sys' }, { role: 'user', content: lastContent }]; }

// openai format
let m = I.buildAPIMessagesWithFiles({ format: 'openai' }, msgsWith('u1'), [
  { name: 'note.md', textContent: '# T', textual: true, inline: true },
  { name: 'f.png', type: 'image/png', base64: 'B64', inline: true, image: true }
]);
let last = m[m.length - 1];
T('openai: content is array', Array.isArray(last.content));
T('openai: has text part with file content', JSON.stringify(last.content).includes('# T'));
T('openai: has image_url part', JSON.stringify(last.content).includes('image_url'));
T('openai: base64 data uri correct', JSON.stringify(last.content).includes('data:image/png;base64,B64'));

// ollama format
m = I.buildAPIMessagesWithFiles({ format: 'ollama' }, msgsWith('u2'), [
  { name: 'f.png', type: 'image/png', base64: 'B64', inline: true, image: true },
  { name: 'doc.pdf', type: 'application/pdf', base64: 'PDOC', inline: true, document: true }
]);
last = m[m.length - 1];
T('ollama: content is string', typeof last.content === 'string');
T('ollama: images array holds base64', Array.isArray(last.images) && last.images[0] === 'B64');
// Regression: a base64 DOCUMENT must NOT be sent in ollama's images array (only images).
T('ollama: documents excluded from images array', Array.isArray(last.images) && last.images.length === 1);

// anthropic format
m = I.buildAPIMessagesWithFiles({ format: 'anthropic' }, msgsWith('u3'), [
  { name: 'f.png', type: 'image/png', base64: 'B64', inline: true, image: true },
  { name: 'doc.pdf', type: 'application/pdf', base64: 'PDOC', inline: true, document: true }
]);
last = m[m.length - 1];
const cj = JSON.stringify(last.content);
T('anthropic: has image source base64', cj.includes('"type":"image"') && cj.includes('B64'));
T('anthropic: has document source base64', cj.includes('"type":"document"') && cj.includes('PDOC'));
T('anthropic: media_type preserved', cj.includes('"media_type":"image/png"') && cj.includes('"media_type":"application/pdf"'));

// gemini format
m = I.buildAPIMessagesWithFiles({ format: 'gemini' }, msgsWith('u4'), [
  { name: 'f.png', type: 'image/png', base64: 'B64', inline: true, image: true }
]);
last = m[m.length - 1];
const gj = JSON.stringify(last.content);
T('gemini: parts has inlineData', gj.includes('"inlineData"') && gj.includes('"mimeType":"image/png"'));

// uploaded document via /files (openai) -> file_id reference only
m = I.buildAPIMessagesWithFiles({ format: 'openai' }, msgsWith('u5'), [
  { name: 'doc.pdf', type: 'application/pdf', inline: false, document: true, file_id: 'file-x' }
]);
last = m[m.length - 1];
T('openai: uploaded doc referenced by file_id', JSON.stringify(last.content).includes('file-x'));
// Regression: the file_ids must be exposed on the messages array so the request body can send them.
T('openai: file_ids exposed for the request body', Array.isArray(m.fileIds) && m.fileIds[0] === 'file-x');
// No file_id entries -> no fileIds property (nothing to send).
m = I.buildAPIMessagesWithFiles({ format: 'openai' }, msgsWith('u5b'), [
  { name: 'f.png', type: 'image/png', base64: 'B64', inline: true, image: true }
]);
T('openai: no fileIds property when nothing was uploaded via /files', !m.fileIds);

console.log('');
(async () => {
  // uploadFiles error-clarity checks need the sandbox's pendingAttachments + fetch stubs.
  const prevFetch = sandbox.fetch;
  sandbox.fetch = async () => ({ ok: false, status: 404 });
  try {
    vm.runInContext(`
      window.__internals.setPending([ { name: 'doc.pdf', type: 'application/pdf', size: 100, fileObject: { name: 'doc.pdf', type: 'application/pdf' }, textual: false } ]);
      window.__upRes = window.__internals.uploadFiles({ format: 'openai', endpoint: 'https://x.example/v1/chat/completions', apiKey: 'k', textLimit: 30000 })
        .then(r => ({ ok: true, result: r }))
        .catch(e => ({ ok: false, error: e.message }));
    `, ctx);
    const res = await vm.runInContext('window.__upRes', ctx);
    T('binary without upload endpoint -> clear error thrown', res.ok === false && /not supported by this provider/i.test(res.error));
    T('error mentions the file name', res.ok === false && res.error.includes('doc.pdf'));
  } finally {
    sandbox.fetch = prevFetch;
  }
  sandbox.fetch = async () => ({ ok: false, status: 404 });
  try {
    vm.runInContext(`
      window.__internals.setPending([ { name: 'doc.pdf', type: 'application/pdf', size: 100, fileObject: { name: 'doc.pdf', type: 'application/pdf' }, textual: false } ]);
      globalThis.FileReader = class { constructor(){ this.onload=null; this.onerror=null; this.result=''; } readAsDataURL(){ this.result='data:application/pdf;base64,RE9D'; this.onload({ target: { result: this.result } }); } readAsText(){ this.result='PDFTEXT'; this.onload({ target: { result: this.result } }); } };
      window.__upRes2 = window.__internals.uploadFiles({ format: 'anthropic', endpoint: 'https://api.anthropic.com/v1/messages', apiKey: 'k', textLimit: 30000 })
        .then(r => ({ ok: true, result: r }))
        .catch(e => ({ ok: false, error: e.message }));
    `, ctx);
    const res2 = await vm.runInContext('window.__upRes2', ctx);
    console.log('  [debug] anthropic uploadFiles result:', JSON.stringify(res2));
    T('anthropic binary -> base64 document inline (no throw)', res2.ok === true && res2.result && res2.result.length === 1 && res2.result[0].document === true && res2.result[0].base64 === 'RE9D');
  } finally {
    sandbox.fetch = prevFetch;
    delete sandbox.FileReader;
  }

  // Regression B2: regenerating a message must re-send the ORIGINAL attachments —
  // file_ids for uploaded docs, base64 for images — not just placeholder text.
  let captured = null;
  const prevFetch3 = sandbox.fetch;
  sandbox.fetch = async (url, opts) => { captured = JSON.parse(opts.body); return { ok: false, status: 500, text: async () => 'boom' }; };
  try {
    vm.runInContext(`
      window.__internals.setD({ providers: [{ id: 'p1', name: 'Test', format: 'openai', model: 'm', endpoint: 'http://localhost:8000/v1/chat/completions', apiKey: 'k', temperature: 0.7, maxTokens: 4096, topP: 0.9 }], activeProviderId: 'p1', chats: { p1: { c1: { id: 'c1', title: 't', createdAt: 1, messages: [
        { role: 'user', text: 'domanda', content: '[File: doc.pdf - uploaded as file-1]\\n\\ndomanda', timestamp: 1, attachments: [
          { name: 'doc.pdf', type: 'application/pdf', size: 100, file_id: 'file-1', inline: false, document: true },
          { name: 'f.png', type: 'image/png', size: 100, base64: 'B64', inline: true, image: true } ] },
        { role: 'assistant', content: 'ok', timestamp: 2 } ] } } }, activeChatId: 'c1', settings: {}, theme: 'dark', language: 'en' });
      window.regenerateMessage(1);
    `, ctx);
    await new Promise(r => setTimeout(r, 50));
    const body = captured || {};
    T('regenerate: file_ids re-sent in the request body', Array.isArray(body.file_ids) && body.file_ids[0] === 'file-1', JSON.stringify(body.file_ids));
    const lastMsg = body.messages && body.messages[body.messages.length - 1];
    const lastJson = JSON.stringify(lastMsg && lastMsg.content);
    T('regenerate: image re-sent as image_url base64', !!lastJson && lastJson.includes('data:image/png;base64,B64'));
    T('regenerate: prompt text preserved', !!lastJson && lastJson.includes('domanda'));
    const chatAfter = vm.runInContext('window.__internals.getChat()', ctx);
    T('regenerate: stored message keeps text + attachments', !!chatAfter && chatAfter.messages.some(m => m.role === 'user' && m.text === 'domanda' && Array.isArray(m.attachments) && m.attachments.length === 2));
    // Regression: regenerate must replace the original user message, not append a duplicate —
    // the old rM left [user, user, assistant] in the history and sent the enriched dump twice.
    T('regenerate: no duplicate user message', !!chatAfter && chatAfter.messages.filter(m => m.role === 'user' && m.text === 'domanda').length === 1);
  } finally {
    sandbox.fetch = prevFetch3;
  }

  console.log('');
  console.log(`${pass} passed, ${fail} failed ${fail === 0 ? 'ALL OK!' : ''}`);
  process.exit(fail === 0 ? 0 : 1);
})();
