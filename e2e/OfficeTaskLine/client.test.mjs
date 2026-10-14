#!/usr/bin/env node
/*  Issue #98 — "Make task tracking easier in OfficeManager": client-side test.
 *
 *  Runs OfficeManager/game.js inside node's `vm` with a minimal fake DOM (no browser,
 *  no jsdom here) and checks the two surfaces the issue asks for:
 *
 *    · the side roster row carries a "Currently doing: <request>" line above the bar;
 *    · the answer window shows the request (line 1) and the progress (line 2), and
 *      keeps updating while it is open without eating what the user is typing.
 *
 *  game.js keeps its state in script-scoped const/let, so the assertions are appended
 *  to the game source and evaluated in the SAME context (that is the only way to reach
 *  `employees`, `askEmp`, … from outside).
 *
 *  Run:  node client.test.mjs
 */
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import vm from "node:vm";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const GAME = path.resolve(HERE, "../../OfficeManager/game.js");

let pass = 0, fail = 0;
function check(name, cond, detail = "") {
  if (cond) { pass++; console.log("  OK   " + name); }
  else { fail++; console.log("  FAIL " + name + (detail ? "   " + detail : "")); }
}

/* ---------- fake DOM ---------- */
class FakeEl {
  constructor(tag) {
    this.tagName = String(tag || "div").toUpperCase();
    this.children = [];
    this.style = {};
    this._text = "";
    this._html = "";
    this.value = "";
    this.title = "";
    this.disabled = false;
    this.placeholder = "";
    this.scrollTop = 0;
    this.scrollHeight = 0;
    this.listeners = {};
    this._cls = new Set();
    const self = this;
    this.classList = {
      add: (c) => self._cls.add(c),
      remove: (c) => self._cls.delete(c),
      contains: (c) => self._cls.has(c),
      toggle: (c, on) => {
        const want = on === undefined ? !self._cls.has(c) : !!on;
        if (want) self._cls.add(c); else self._cls.delete(c);
      },
    };
  }
  set className(v) { this._cls = new Set(String(v).split(/\s+/).filter(Boolean)); }
  get className() { return [...this._cls].join(" "); }
  set textContent(v) { this._text = String(v); this.children = []; }
  get textContent() { return this._text; }
  set innerHTML(v) { this._html = String(v); if (v === "") this.children = []; }
  get innerHTML() { return this._html; }
  appendChild(el) { this.children.push(el); return el; }
  addEventListener(t, fn) { (this.listeners[t] = this.listeners[t] || []).push(fn); }
  focus() { }
  getContext() {
    return new Proxy({}, {
      get: (_t, p) => {
        if (p === "measureText") return (s) => ({ width: String(s).length * 8 });
        if (p === "canvas") return { width: 1024, height: 683 };
        return () => { };
      },
      set: () => true,
    });
  }
  // test helpers
  child(cls) { return this.children.find(c => c._cls.has(cls)); }
  texts() { return [this._text, ...this.children.map(c => c.texts())].filter(Boolean).join(" | "); }
}

function run() {
  const byId = new Map();
  const getEl = (id) => {
    if (!byId.has(id)) { const el = new FakeEl("div"); el.id = id; byId.set(id, el); }
    return byId.get(id);
  };
  ["log", "input", "game", "gamebox", "stage", "roster", "rosterlist", "askbox",
   "askname", "askdoing", "asktask", "askbar", "askfill", "askstatus", "askmsg",
   "askinput", "asksend", "askclose", "snd"].forEach(getEl);

  const document = {
    getElementById: getEl,
    createElement: (t) => new FakeEl(t),
    addEventListener: () => { },
  };
  const windowObj = { addEventListener: () => { }, innerWidth: 1600, innerHeight: 900 };

  const sent = [];
  class FakeWebSocket {
    constructor(url) { this.url = url; this.readyState = 1; setTimeout(() => this.onopen && this.onopen(), 0); }
    send(m) { sent.push(m); }
    close() { this.readyState = 3; }
  }
  FakeWebSocket.OPEN = 1; FakeWebSocket.CONNECTING = 0;

  class FakeImage {
    constructor() { this._src = ""; }
    set src(v) { this._src = v; setTimeout(() => this.onload && this.onload(), 0); }
    get src() { return this._src; }
  }

  const sandbox = {
    document, window: windowObj, innerWidth: 1600, innerHeight: 900,
    WebSocket: FakeWebSocket, Image: FakeImage,
    requestAnimationFrame: () => { },
    setTimeout, clearTimeout, performance, console,
    atob: (s) => Buffer.from(s, "base64").toString("binary"),
    OFFICE_MASK_W: 4, OFFICE_MASK_H: 4, OFFICE_MASK_B64: "AAAA",
    location: { protocol: "http:", host: "localhost:5290" },
  };
  sandbox.globalThis = sandbox;
  vm.createContext(sandbox);

  const game = fs.readFileSync(GAME, "utf8");
  const snippet = `
;(function () {
  const R = [];
  const T = (name, cond, detail) => R.push([name, !!cond, detail === undefined ? "" : String(detail)]);
  const el = (id) => document.getElementById(id);
  const rosterlist = el("rosterlist");
  const askdoing = el("askdoing"), asktask = el("asktask"), askfill = el("askfill");
  const askstatus = el("askstatus"), askinput = el("askinput");

  // a session employee with a known request
  spawnEmployee({ empId: "emp-1", agentId: "sess-1", kind: "session", sprite: 0,
                 label: "agent 1", task: "Generate a presentation" });
  // a subagent: no request reported, only the tool it has in hand
  spawnEmployee({ empId: "emp-2", agentId: "sess-1::sub-2", kind: "subagent", sprite: 1,
                 label: "sub 2", task: "" });
  setMethod("emp-2", "PresentationTool.CreatePresentation");

  // ── doingText precedence ──
  const e1 = employees.find(e => e.empId === "emp-1");
  const e2 = employees.find(e => e.empId === "emp-2");
  T("doingText uses the request when the host knows it",
    doingText(e1) === "Generate a presentation", doingText(e1));
  T("doingText falls back to the current tool method",
    doingText(e2) === "Presentation Tool, Create Presentation", doingText(e2));
  e1.task = ""; e1.method = "";
  T("doingText is empty with neither", doingText(e1) === "", doingText(e1));
  e1.task = "Generate a presentation";

  // ── the roster row carries the "Currently doing" line ──
  renderRoster();
  const rows = rosterlist.children;
  T("roster has one row per active employee", rows.length === 2, "rows=" + rows.length);
  const r1 = rows[0].child("rtask");
  T("the row of a known request shows 'Currently doing: <request>'",
    !!r1 && r1.textContent === "Currently doing: Generate a presentation",
    r1 ? r1.textContent : "no .rtask");
  T("the row tooltip carries the full request", !!r1 && r1.title === "Generate a presentation");
  const r2 = rows[1].child("rtask");
  T("a subagent row shows the tool it is busy with",
    !!r2 && r2.textContent === "Currently doing: Presentation Tool, Create Presentation",
    r2 ? r2.textContent : "no .rtask");
  T("the name still comes first in the row",
    rows[0].children[0].className === "rname" && rows[0].children[1].className === "rtask",
    rows[0].children.map(c => c.className).join(","));

  // ── an employee with nothing to report gets no task line ──
  spawnEmployee({ empId: "emp-3", agentId: "sess-3", kind: "stateless", sprite: 2, label: "a3", task: "" });
  renderRoster();
  const r3 = rosterlist.children.find(r => r.child("rname") && r.child("rname").textContent === "a3");
  T("no task line when nothing is known", !!r3 && !r3.child("rtask"));

  // ── the answer window: line 1 the request, line 2 the progress ──
  onServerMessage(JSON.stringify({ type: "running", empId: "emp-1", value: true }));
  setIteration("emp-1", 4);
  openAskbox(e1);
  T("the answer window shows the request", asktask.textContent === "Generate a presentation",
    asktask.textContent);
  T("the answer window shows the progress bar filled", askfill.style.width === "40%",
    askfill.style.width);
  T("the answer window shows the status word", askstatus.textContent === "WORKING",
    askstatus.textContent);
  T("the status block carries the working class", askdoing.className === "askdoing working",
    askdoing.className);
  T("the status block is visible when there is a task", askdoing.style.display === "",
    JSON.stringify(askdoing.style.display));

  // ── it keeps updating while open, without eating the answer being typed ──
  askinput.value = "the chart slide goes last";
  onServerMessage(JSON.stringify({ type: "progress", empId: "emp-1", iteration: 7 }));
  T("the open window's bar follows the server", askfill.style.width === "70%", askfill.style.width);
  T("the answer being typed is untouched", askinput.value === "the chart slide goes last",
    askinput.value);

  // ── waiting / done colours (the server always closes a run with running:false) ──
  onServerMessage(JSON.stringify({ type: "needs", empId: "emp-1", value: true, done: false }));
  onServerMessage(JSON.stringify({ type: "running", empId: "emp-1", value: false }));
  T("waiting state recolours the block", askdoing.className === "askdoing waiting" &&
    askstatus.textContent === "NEEDS YOU", askdoing.className + "/" + askstatus.textContent);
  onServerMessage(JSON.stringify({ type: "needs", empId: "emp-1", value: false, done: true }));
  onServerMessage(JSON.stringify({ type: "running", empId: "emp-1", value: false }));
  T("done state fills the bar and recolours", askfill.style.width === "100%" &&
    askdoing.className === "askdoing done", askfill.style.width + "/" + askdoing.className);
  T("the finished task is still readable", asktask.textContent === "Generate a presentation",
    asktask.textContent);

  // ── a task pushed later reaches both surfaces ──
  onServerMessage(JSON.stringify({ type: "task", empId: "emp-2", task: "Check the sales spreadsheet" }));
  T("a later task event updates the employee", doingText(e2) === "Check the sales spreadsheet",
    doingText(e2));
  renderRoster();
  const r2b = rosterlist.children[1].child("rtask");
  T("the roster row shows the new task, not the old method",
    r2b.textContent === "Currently doing: Check the sales spreadsheet", r2b.textContent);

  // ── an employee whose request is unknown still shows its progress, not an empty line ──
  spawnEmployee({ empId: "emp-4", agentId: "sess-4", kind: "subagent", sprite: 3, label: "a4", task: "" });
  const e4 = employees.find(e => e.empId === "emp-4");
  onServerMessage(JSON.stringify({ type: "running", empId: "emp-4", value: true }));
  openAskbox(e4);
  T("a running employee with nothing known still shows its bar",
    askdoing.style.display === "" && askstatus.textContent === "WORKING",
    askdoing.style.display + "/" + askstatus.textContent);
  T("…and drops the empty 'currently doing' lines",
    askdoing.className.indexOf("notask") >= 0, askdoing.className);
  onServerMessage(JSON.stringify({ type: "running", empId: "emp-4", value: false }));
  openAskbox(e4);
  T("an employee with nothing at all to report hides the block",
    askdoing.style.display === "none", askdoing.style.display);

  // ── robustness ──
  T("a task for an unknown employee is ignored",
    (onServerMessage(JSON.stringify({ type: "task", empId: "nope", task: "x" })), true));
  T("an empty task clears the line",
    (setTask("emp-1", ""), doingText(e1) === ""));

  // ── issue #103: the mouse selects any employee at any time, not only the first ──
  if (engaged) disengage();
  spawnEmployee({ empId: "m-A", agentId: "s-A", kind: "session", sprite: 0, label: "mouse A", task: "" });
  spawnEmployee({ empId: "m-B", agentId: "s-B", kind: "session", sprite: 1, label: "mouse B", task: "" });
  const eA = employees.find(e => e.empId === "m-A");
  const eB = employees.find(e => e.empId === "m-B");
  eA.x = 200; eA.y = 400;   // fx/fy are getters off x/y — place them away from the door
  eB.x = 600; eB.y = 400;
  const hitA = [200, 340], hitB = [600, 340], floor = [100, 650];

  engageAt(hitA[0], hitA[1]);
  T("first mouse click hires the clicked employee", engaged === eA,
    engaged ? engaged.empId : "none");

  engageAt(hitB[0], hitB[1]);
  T("a second mouse click on a DIFFERENT employee switches the hire",
    engaged === eB, engaged ? engaged.empId : "none");
  T("switching releases the previous hire (re-engage pause set on it)",
    eA.blockedUntil > performance.now(), "blockedUntil=" + eA.blockedUntil);

  engageAt(hitB[0], hitB[1]);
  T("clicking the already-hired employee keeps it hired", engaged === eB,
    engaged ? engaged.empId : "none");

  engageAt(floor[0], floor[1]);
  T("clicking the empty floor keeps the current hire (no accidental release)",
    engaged === eB, engaged ? engaged.empId : "none");

  disengage();
  engageAt(hitA[0], hitA[1]);
  T("after releasing, the mouse hires again", engaged === eA,
    engaged ? engaged.empId : "none");
  if (engaged) disengage();

  __results = R;
})();
`;
  let results = [];
  sandbox.__results = results;
  vm.runInContext(game + snippet, sandbox, { filename: "game.js+test" });
  results = sandbox.__results;
  for (const [name, ok, detail] of results) check(name, ok, detail);
}

run();
console.log(fail === 0 ? "\nAll client checks passed." : "\n" + fail + " check(s) FAILED.");
process.exit(fail === 0 ? 0 : 1);
