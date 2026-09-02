"use strict";

/* ============================================================
   Office Manager 16-Bit — top-down oblique office game.
   The boss (arrow keys) is the human user's avatar: he hires
   employees (contact, Tab, mouse) and talks to them through the
   bottom chat; what the user types appears above the boss's head.
   Every employee is the visual of an AGENT INSTANCE in
   AIOrchestrator: idle employees wander and say "I have nothing
   to do", session employees (any medium: TUI, API, SIP, this
   chat) work at a desk while their agent runs and go back to the
   door and despawn when the agent instance closes; subagents are
   visual-only. The roster is server-authoritative (AgentBridge →
   /ws/office duplex WebSocket, see AgentBridge/OfficeBridge.cs).
   See docs-dev/ARCHITECTURE.md for the full technical picture.
   ============================================================ */

/* ---------- constants ---------- */
const W = 1024, H = 683;               // office pixel size
const FRAME = 64;                      // LPC sprite frame size
const CHAR_SCALE = 2;                  // sprite render scale (doubled)
const CHAR_H = FRAME * CHAR_SCALE;     // 128 px frame on screen
const CHAT_H = 208;                    // chat panel height in CSS px
const BOSS_SPEED = 96;                 // px/s
const EMP_SPEED = 50;                  // px/s
const ENGAGE_R = 70;                   // contact hire radius (feet distance)
const DISENGAGE_R = 92;                // contact fire radius
const REENGAGE_PAUSE = 1500;           // pause before a fired employee wanders again
const INACTIVE_MS = 60000;             // engagement lost after 1 min of boss inactivity
const LABEL_IDLE_MS = 10000;           // "boss" label reappears after this much idle
const BUBBLE_MS = 3500;                // speech bubble lifetime
const AMBIENT_MIN_MS = 12000;          // min gap between ambient lines
const AMBIENT_MEAN_MS = 60000;         // avg 1 ambient line per minute per employee
const AMBIENT_SPAN_MS = AMBIENT_MEAN_MS - AMBIENT_MIN_MS;
const WALK_FRAME_MS = 100;             // walk animation frame time
const IDLE_FRAME_MS = 600;             // idle animation frame time
const PHONE_ZONE = { x: 150, y: 164, w: 52, h: 42 };   // floor in front of the black wall phone (x 152..190 on the wall)
const COFFEE_ZONE = { x: 762, y: 178, w: 44, h: 36 };  // floor in front of the coffee machine (brown cabinet, left part)
const COFFEE_LINE = "I'm taking a coffee break";
const COFFEE_COOLDOWN_MS = 30000;    // per-employee min gap between coffee lines
const CLOCK = { x: 965, y: 50, r: 16 };                // wall clock spot (right wall, high)
const BEHIND_PX = 48;                  // how far (px) a character may tuck behind an object
const NAV_CELL = 32;                   // navigation grid cell size (employee pathfinding)
/* central front positions of the 8 desks (front base: centre x, bottom edge y + 2) */
const WORK_SPOTS = [
  { x: 229, y: 337 }, { x: 497, y: 336 }, { x: 771, y: 333 },
  { x: 228, y: 491 }, { x: 497, y: 489 }, { x: 771, y: 486 },
  { x: 334, y: 640 }, { x: 652, y: 636 },
];
const WORK_R = 55;                     // attraction radius when passing in front of a desk
const WORK_MS = 20000;                 // IDLE employees: working duration at a desk (agent employees have NO timeout — they stay until the agent instance closes)
const WORK_COOLDOWN_MS = 15000;        // desk attraction disabled after a work session (idle employees)
const IDLE_LINE = "I have nothing to do";
const DOOR = { x0: 72, x1: 156, y0: 152, y1: 212 };   // spawn/despawn strip in front of the door
const PIX_FONT = '8px "Press Start 2P", monospace';
const EMP_SPRITES = ["employee A", "employee B", "employee C", "employee D", "employee E"];

/* boss auto-pilot: after BOSS_AUTO_MS without arrow input the boss wanders on its own
   (never attracted to desks, cannot hire by contact) and keeps an eye on the working agents */
const BOSS_AUTO_MS = 10000;
const BOSS_AUTO_SAY_MIN_MS = 8000;
const BOSS_AUTO_SAY_SPAN_MS = 6000;
const BOSS_AUTO_LINES = [
  "I'm watching you!",
  "Get to work, slackers!",
  "Forget your holidays!",
  "Production bonus to whoever does an excellent job!",
  "Let's have a briefing to assess the situation",
];

/* Sprite poses (verified by pixel census): row 0 = back (mostly hair),
   row 1 = left profile, row 2 = front, row 3 = right profile. Pure up shows
   the back; lateral movement (including the up/down diagonals) uses the
   profiles, matching the direction the character is travelling. */
const DIRS = {
  N:  { r: 0, m: 0 }, NE: { r: 3, m: 0 }, E: { r: 3, m: 0 }, SE: { r: 3, m: 0 },
  S:  { r: 2, m: 0 }, SW: { r: 1, m: 0 }, W: { r: 1, m: 0 }, NW: { r: 1, m: 0 },
};

/* ---------- walkable mask (generated from office-ground.png, see tools/make-mask.py) ---------- */
const MASK_BYTES = atob(OFFICE_MASK_B64);
const MASK = new Uint8Array(MASK_BYTES.length);
for (let i = 0; i < MASK_BYTES.length; i++) MASK[i] = MASK_BYTES.charCodeAt(i);

function walkable(x, y) {
  x |= 0; y |= 0;                        // positions are floats — truncate before the row lookup
  if (x < 0 || y < 0 || x >= OFFICE_MASK_W || y >= OFFICE_MASK_H) return false;
  const i = y * OFFICE_MASK_W + x;
  return (MASK[i >> 3] & (1 << (7 - (i & 7)))) !== 0;
}

/* "Behind" band: the top BEHIND_PX of every opaque run that is entered from
   the north (walkable floor directly above). This lets characters tuck their
   feet behind desks/walls (legs hidden by the object) instead of standing at
   a fixed distance south of them. Runs against the image top (e.g. the back
   wall itself) get no band. BEHIND_TOP stores the object's top row per band
   pixel — used as the render depth so the object is drawn over the character. */
const BEHIND = new Uint8Array(OFFICE_MASK_W * OFFICE_MASK_H);
const BEHIND_TOP = new Int32Array(OFFICE_MASK_W * OFFICE_MASK_H).fill(-1);

function buildBehind() {
  for (let x = 0; x < OFFICE_MASK_W; x++) {
    let y = 0;
    while (y < OFFICE_MASK_H) {
      if (walkable(x, y)) { y++; continue; }
      const y0 = y;
      while (y < OFFICE_MASK_H && !walkable(x, y)) y++;
      if (y0 > 0 && walkable(x, y0 - 1)) {
        const end = Math.min(y, y0 + BEHIND_PX);
        for (let yy = y0; yy < end; yy++) {
          const i = yy * OFFICE_MASK_W + x;
          BEHIND[i] = 1;
          BEHIND_TOP[i] = y0;
        }
      }
    }
  }
}

/* depth key used for banded sorting: a character tucked in a behind band is
   drawn at the object's top row, so the whole object covers its lower body */
function renderDepth(p) {
  const top = BEHIND_TOP[(p.fy | 0) * OFFICE_MASK_W + (p.fx | 0)];
  return top >= 0 ? top : p.fy;
}

function standable(x, y) {
  x |= 0; y |= 0;
  if (x < 0 || y < 0 || x >= OFFICE_MASK_W || y >= OFFICE_MASK_H) return false;
  const i = y * OFFICE_MASK_W + x;
  return (MASK[i >> 3] & (1 << (7 - (i & 7)))) !== 0 || BEHIND[i] === 1;
}

/* ---------- asset loading ---------- */
const IMG = { top: null, ground: null, chars: {} };   // chars: name -> { walk, idle }

function loadImage(src) {
  return new Promise((res, rej) => {
    const im = new Image();
    im.onload = () => res(im);
    im.onerror = () => rej(new Error("failed to load " + src));
    im.src = src;
  });
}

async function loadAssets() {
  // Absolute asset URLs — the page may be opened at /OfficeManager or /OfficeManager/.
  const base = "/OfficeManager/assets/";
  [IMG.top, IMG.ground] = await Promise.all([
    loadImage(base + "office.png"),
    loadImage(base + "office-ground.png"),
  ]);
  const names = ["boss", ...EMP_SPRITES];
  await Promise.all(names.map(name =>
    Promise.all([
      loadImage(base + name + "/standard/walk.png"),
      loadImage(base + name + "/standard/idle.png"),
    ]).then(([walk, idle]) => { IMG.chars[name] = { walk, idle }; })
  ));
}

/* ---------- 16-bit sound effects (Web Audio synth) ---------- */
const Sfx = {
  ctx: null, muted: false,
  ensure() {
    try {
      if (!this.ctx) {
        const AC = window.AudioContext || window.webkitAudioContext;
        if (AC) this.ctx = new AC();
      }
      if (this.ctx && this.ctx.state === "suspended") this.ctx.resume();
    } catch (e) { /* audio unavailable — keep the game running */ }
  },
  beep(f0, f1, dur, type, vol, delay) {
    if (this.muted || !this.ctx) return;
    const t0 = this.ctx.currentTime + (delay || 0);
    const o = this.ctx.createOscillator(), g = this.ctx.createGain();
    o.type = type || "square";
    o.frequency.setValueAtTime(f0, t0);
    if (f1) o.frequency.exponentialRampToValueAtTime(f1, t0 + dur);
    g.gain.setValueAtTime(vol || 0.05, t0);
    g.gain.exponentialRampToValueAtTime(0.0001, t0 + dur);
    o.connect(g); g.connect(this.ctx.destination);
    o.start(t0); o.stop(t0 + dur + 0.03);
  },
  step()    { this.beep(this._s ? 210 : 280, 0, 0.04, "square", 0.02); this._s = !this._s; },
  engage()  { this.beep(523, 0, .07, "square", .05); this.beep(659, 0, .07, "square", .05, .07); this.beep(784, 0, .10, "square", .05, .14); },
  send()    { this.beep(620, 880, .08, "square", .04); },
  reply()   { this.beep(660, 440, .09, "square", .04); },
  ambient() { this.beep(440, 330, .06, "triangle", .03); },
  coffee()  { this.beep(330, 440, .08, "triangle", .04); this.beep(494, 330, .10, "triangle", .04, .12); },
  phone()   { [0, .16, .32, .48].forEach(d => this.beep(d % .32 ? 940 : 760, 0, .13, "square", .05, d)); },
};

/* ---------- chat (16-bit text-adventure console) ---------- */
function esc(s) { return s.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c])); }

const Chat = {
  el: null, queue: [], typing: false,
  init() { this.el = document.getElementById("log"); },
  say(speaker, text, cls) {
    this.queue.push({ speaker, text, cls: cls || (speaker !== "Boss" ? "emp" : "") });
    if (!this.typing) this._type();
  },
  _type() {
    const m = this.queue.shift();
    if (!m) { this.typing = false; return; }
    this.typing = true;
    const div = document.createElement("div");
    div.className = "msg " + m.cls;
    if (m.speaker) div.innerHTML = "<span class=who>" + esc(m.speaker) + "</span> ";
    const span = document.createElement("span");
    div.appendChild(span);
    this.el.appendChild(div);
    const text = esc(m.text);
    let i = 0;
    const tick = () => {
      if (i >= text.length) { this.timer = setTimeout(() => this._type(), 40); return; }
      span.textContent = text.slice(0, ++i);
      this.el.scrollTop = this.el.scrollHeight;
      this.timer = setTimeout(tick, 30);
    };
    tick();
  },
};

/* ---------- persons ---------- */
class Person {
  constructor(name, img, x, y, isBoss) {
    this.name = name;
    this.img = img;
    this.x = x; this.y = y;            // feet position (depth sort key)
    this.isBoss = isBoss;
    this.dir = "S";
    this.moving = false;
    this.wasMoving = false;
    this.frame = 0; this.animT = 0;
    this.bubble = null;
    this.speed = isBoss ? BOSS_SPEED : EMP_SPEED;
    /* server-driven role: "idle" | "session" | "stateless" | "subagent" */
    this.empId = null;
    this.kind = "idle";
    this.label = "";
    this.running = false;
    this.returningHome = false;
    this.homeX = 0; this.homeY = 0; this.homePath = null;
  }
  get fx() { return this.x | 0; }
  get fy() { return this.y | 0; }
  setDir(vx, vy) {
    const a = Math.atan2(vy, vx) * 180 / Math.PI;      // -180..180, 0 = east
    this.dir = a >= -22.5 && a < 22.5 ? "E"
      : a >= 22.5 && a < 67.5 ? "SE"
      : a >= 67.5 && a < 112.5 ? "S"
      : a >= 112.5 && a < 157.5 ? "SW"
      : a >= -67.5 && a < -22.5 ? "NE"
      : a >= -112.5 && a < -67.5 ? "N"
      : a >= -157.5 && a < -112.5 ? "NW" : "W";
  }
  say(text, kind) {                          // kind: "phone"/"coffee" = persistent zone bubble
    this.bubble = { text, until: kind ? Infinity : performance.now() + BUBBLE_MS, kind: kind || null };
    Chat.say(this.name, text);
  }
  /* the agent's current tool method, shown as its split words (FileSearch → "File Search") */
  sayMethod(method) {
    this.bubble = { text: methodWords(method), until: Infinity, kind: "method" };
  }
  move(dx, dy) {
    if (dx && this.canStand(this.x + dx, this.y)) this.x += dx;
    if (dy && this.canStand(this.x, this.y + dy)) this.y += dy;
  }
  canStand(x, y) {
    return standable(x, y) && standable(x - 16, y) && standable(x + 16, y);
  }
  anim(dt) {
    // never hold a walk frame while drawing the idle sheet (or vice versa)
    if (this.moving !== this.wasMoving) { this.wasMoving = this.moving; this.frame = 0; this.animT = 0; }
    this.animT += dt;
    const n = this.moving ? 9 : 2;     // 9-frame walk cycle, 2-frame idle
    const t = this.moving ? WALK_FRAME_MS : IDLE_FRAME_MS;
    while (this.animT >= t) { this.animT -= t; this.frame = (this.frame + 1) % n; }
  }
}

/* splits a PascalCase token into its words: "FileSearch" → "File Search" */
function splitWords(part) {
  return part
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .replace(/[_\-\s]+/g, " ")
    .trim();
}

/* the agent's current tool call as bubble words: the tool CLASS name split into words,
   a comma, then the METHOD name split into words — "FileTool.FileSearch" → "File Tool, File Search".
   Bare reserved methods (done, cli, ...) have no class and stay as their split words. */
function methodWords(name) {
  const parts = name.split(".");
  return parts.length > 1
    ? splitWords(parts[0]) + ", " + splitWords(parts.slice(1).join("."))
    : splitWords(name);
}

/* ---------- world state ---------- */
const boss = new Person("Boss", null, 512, 620, true);
const employees = [];

let engaged = null;        // currently hired employee
let engagedBy = null;      // "contact" | "tab" | "mouse"
let engagedSince = 0;      // engagement timer: fired after 1 min of boss inactivity
let lastBossActivity = 0;  // last boss move / speech timestamp
let bossAuto = false;      // boss auto-pilot (no arrow input for BOSS_AUTO_MS)
let lastBossInput = 0;     // last arrow-key movement timestamp
let phoneActive = false;
let stepT = 0;
let waypoints = [];
let ws = null;
let wsRetry = null;
let connected = false;

function markBossActivity() {            // any action (movement or speech) — drives the "boss" label
  lastBossActivity = performance.now();
}
function markBossSpeech() {              // speech only — keeps the current hire alive
  markBossActivity();
  engagedSince = lastBossActivity;
}

function buildWaypoints() {
  for (let y = 158; y < H - 14; y += 22) {
    for (let x = 42; x < W - 42; x += 22) {
      if (walkable(x, y) && walkable(x - 8, y) && walkable(x + 8, y) && walkable(x, y + 4)) {
        waypoints.push([x + (Math.random() * 14 - 7), y]);
      }
    }
  }
}
const randomWalkable = () => waypoints[(Math.random() * waypoints.length) | 0];

/* ---------- navigation grid (BFS pathfinding for employees) ---------- */
const NAV_COLS = Math.ceil(W / NAV_CELL), NAV_ROWS = Math.ceil(H / NAV_CELL);
const nav = new Uint8Array(NAV_COLS * NAV_ROWS);

function buildNav() {
  const o = NAV_CELL / 2 - 2;              // sample the cell edges so thin walls (chairs) are seen
  for (let r = 0; r < NAV_ROWS; r++) {
    for (let c = 0; c < NAV_COLS; c++) {
      const x = c * NAV_CELL + NAV_CELL / 2, y = r * NAV_CELL + NAV_CELL / 2;
      if (walkable(x, y) && walkable(x - o, y) && walkable(x + o, y) &&
          walkable(x, y - o) && walkable(x, y + o)) {
        nav[r * NAV_COLS + c] = 1;
      }
    }
  }
}

/* BFS over the 4-neighbour grid; returns the pixel waypoints (cell centres)
   from (sx,sy) to (tx,ty), or null when unreachable. */
function pathTo(sx, sy, tx, ty) {
  const sc = Math.min(NAV_COLS - 1, Math.max(0, (sx / NAV_CELL) | 0));
  const sr = Math.min(NAV_ROWS - 1, Math.max(0, (sy / NAV_CELL) | 0));
  const gc = Math.min(NAV_COLS - 1, Math.max(0, (tx / NAV_CELL) | 0));
  const gr = Math.min(NAV_ROWS - 1, Math.max(0, (ty / NAV_CELL) | 0));
  if (sc === gc && sr === gr) return [];
  if (!nav[gr * NAV_COLS + gc]) return null;
  const prev = new Int32Array(NAV_COLS * NAV_ROWS).fill(-1);
  const start = sr * NAV_COLS + sc, goal = gr * NAV_COLS + gc;
  prev[start] = -2;
  const queue = [start];
  while (queue.length) {
    const cur = queue.shift();
    if (cur === goal) break;
    const r = (cur / NAV_COLS) | 0, c = cur % NAV_COLS;
    for (const [dr, dc] of [[-1, 0], [1, 0], [0, -1], [0, 1]]) {
      const nr = r + dr, nc = c + dc;
      if (nr < 0 || nc < 0 || nr >= NAV_ROWS || nc >= NAV_COLS) continue;
      const nk = nr * NAV_COLS + nc;
      if (prev[nk] !== -1 || !nav[nk]) continue;
      prev[nk] = cur;
      queue.push(nk);
    }
  }
  if (prev[goal] === -1) return null;
  const cells = [];
  for (let k = goal; k !== -2; k = prev[k]) cells.push(k);
  cells.reverse();
  return cells.map(k => [(k % NAV_COLS) * NAV_CELL + NAV_CELL / 2, ((k / NAV_COLS) | 0) * NAV_CELL + NAV_CELL / 2]);
}

/* ---------- server protocol (AgentBridge /ws/office) ---------- */
function connect() {
  if (ws && (ws.readyState === WebSocket.OPEN || ws.readyState === WebSocket.CONNECTING)) return;
  const proto = location.protocol === "https:" ? "wss" : "ws";
  ws = new WebSocket(proto + "://" + location.host + "/ws/office");
  ws.onopen = () => {
    connected = true;
    ws.send(JSON.stringify({ type: "hello" }));
    Chat.say("", "OFFICE MANAGER — connected to AgentBridge", "sys");
  };
  ws.onmessage = e => onServerMessage(e.data);
  ws.onclose = () => {
    connected = false;
    if (engaged) disengage();
    Chat.say("", "OFFICE MANAGER — disconnected, retrying…", "sys");
    clearTimeout(wsRetry);
    wsRetry = setTimeout(connect, 3000);
  };
  ws.onerror = () => { try { ws.close(); } catch (e) { } };
}

function onServerMessage(json) {
  let m;
  try { m = JSON.parse(json); } catch (e) { return; }
  switch (m.type) {
    case "snapshot":
      syncRoster(m.employees || []);
      break;
    case "spawn":
      spawnEmployee(m);
      break;
    case "assign":
      assignEmployee(m);
      break;
    case "running":
      setRunning(m.empId, m.value);
      break;
    case "method":
      setMethod(m.empId, m.method);
      break;
    case "closed":
      closeEmployee(m.empId);
      break;
    case "chat":
      chatFromServer(m);
      break;
    case "error":
      Chat.say("", m.text || "server error", "sys");
      break;
  }
}

/* the snapshot is the authoritative roster (sent on connect/hello): despawn employees the
   server no longer knows (e.g. they closed while this tab was reconnecting), keep the rest */
function syncRoster(list) {
  const ids = new Set(list.map(e => e.empId));
  for (const e of [...employees]) {
    if (!ids.has(e.empId) && !e.returningHome) despawnEmployee(e);
  }
  for (const m of list) spawnEmployee(m);
}

function doorSpot() {
  for (let tries = 0; tries < 24; tries++) {
    const x = DOOR.x0 + Math.random() * (DOOR.x1 - DOOR.x0);
    const y = DOOR.y0 + Math.random() * (DOOR.y1 - DOOR.y0);
    if (walkable(x, y)) return [x, y];
  }
  return [(DOOR.x0 + DOOR.x1) / 2, (DOOR.y0 + DOOR.y1) / 2];
}

function spawnEmployee(m) {
  if (employees.some(e => e.empId === m.empId)) return;   // re-sync/snapshot
  const [x, y] = doorSpot();
  const e = new Person(m.label || "employee", IMG.chars[EMP_SPRITES[m.sprite % EMP_SPRITES.length]], x, y, false);
  e.empId = m.empId;
  e.kind = m.kind || "idle";
  e.label = m.label || "";
  e.running = !!m.running;
  e.idleUntil = Math.random() * 2000;
  e.blockedUntil = 0;
  e.stuckT = 0; e.lastX = x; e.lastY = y;
  e.nextAmbient = performance.now() + AMBIENT_MIN_MS + (-Math.log(Math.random()) * AMBIENT_SPAN_MS);
  e.workSpot = null; e.workAt = null; e.workRoute = null;
  e.workDeadline = 0; e.noWorkUntil = 0;
  e.nextCoffee = 0;
  employees.push(e);
}

function assignEmployee(m) {
  const e = employees.find(e => e.empId === m.empId);
  if (!e) return;
  e.kind = "session";
  e.label = m.label || e.label;
  e.name = e.label || e.name;
  Chat.say("", e.name + " is now an agent — a new employee appeared at the door", "sys");
  Sfx.engage();
}

function setRunning(empId, value) {
  const e = employees.find(e => e.empId === empId);
  if (!e) return;
  e.running = value;
  if (!value && e.bubble && e.bubble.kind === "method") e.bubble = null;
  if (value) e.noWorkUntil = 0;                       // an agent employee may be attracted right away
}

function setMethod(empId, method) {
  const e = employees.find(e => e.empId === empId);
  if (!e) return;
  e.running = true;
  e.sayMethod(method);
}

function closeEmployee(empId) {
  const e = employees.find(e => e.empId === empId);
  if (!e) return;
  if (engaged === e) disengage();
  // release the desk (if any) and walk home: while returning, the employee is
  // NEVER re-attracted to a desk, so it cannot get stuck and always reaches the door.
  e.workSpot = null; e.workAt = null; e.workRoute = null; e.workDeadline = 0;
  e.path = null;
  e.returningHome = true;
  [e.homeX, e.homeY] = doorSpot();
  e.homePath = null;
  e.bubble = null;
  e.running = false;
  e.blockedUntil = 0;
  e.stuckT = 0; e.lastX = e.x; e.lastY = e.y;
}

function chatFromServer(m) {
  if (m.role === "assistant") {
    const e = employees.find(e => e.empId === m.empId);
    Chat.say(e && e.label ? e.label : "agent", m.text);
    Sfx.reply();
  } else if (m.role === "user") {
    Chat.say("Boss", m.text);
  } else {
    Chat.say("", m.text, "sys");
  }
}

/* ---------- boss movement ---------- */
const keys = { up: false, down: false, left: false, right: false };

function updateBoss(dt) {
  const now = performance.now();
  const vx = (keys.right ? 1 : 0) - (keys.left ? 1 : 0);
  const vy = (keys.down ? 1 : 0) - (keys.up ? 1 : 0);
  if (vx || vy) {
    // any arrow input ends the auto-pilot immediately
    bossAuto = false;
    boss.path = null;
    lastBossInput = now;
    const n = Math.hypot(vx, vy);
    const sp = BOSS_SPEED * dt / 1000;
    boss.setDir(vx, vy);
    boss.move(vx / n * sp, vy / n * sp);
    boss.moving = true;
    markBossActivity();
    stepT += dt;
    if (stepT > 150) { stepT = 0; Sfx.step(); }
  } else {
    boss.moving = false;
    if (!bossAuto && now - lastBossInput > BOSS_AUTO_MS) {
      bossAuto = true;                       // unattended boss starts wandering on its own
      boss.nextAutoSay = now + 4000;
    }
    if (bossAuto) updateBossAuto(dt, now);
  }
  boss.anim(dt);
}

/* auto-pilot: wander like an employee (BFS paths — never attracted to desks) and, while real
   agents are working, keep an eye on them with supervision lines */
function updateBossAuto(dt, now) {
  if (!boss.path || boss.path.length === 0) {
    if (now < (boss.idleUntil || 0)) { boss.moving = false; return; }
    for (let tries = 0; tries < 6; tries++) {
      const t = randomWalkable();
      const p = pathTo(boss.x, boss.y, t[0], t[1]);
      if (p) { boss.path = p; boss.path.push(t); boss.stuckT = 0; boss.lastX = boss.x; boss.lastY = boss.y; break; }
    }
    if (!boss.path || boss.path.length === 0) { boss.idleUntil = now + 1200; return; }
  }
  const dx = boss.path[0][0] - boss.x, dy = boss.path[0][1] - boss.y;
  const dist = Math.hypot(dx, dy);
  if (dist < 12) {
    boss.path.shift();
    if (!boss.path.length) { boss.idleUntil = now + 800 + Math.random() * 2200; boss.moving = false; return; }
  }
  boss.stuckT += dt;
  if (boss.stuckT > 2000) {
    if (Math.hypot(boss.x - boss.lastX, boss.y - boss.lastY) < 2) boss.path = null;   // replan
    boss.lastX = boss.x; boss.lastY = boss.y; boss.stuckT = 0;
  }
  const sp = BOSS_SPEED * dt / 1000;
  const ox = boss.x, oy = boss.y;
  boss.move(dx / dist * sp, dy / dist * sp);
  boss.moving = Math.hypot(boss.x - ox, boss.y - oy) > 0.1;
  if (boss.moving) boss.setDir(boss.x - ox, boss.y - oy);

  // supervision lines only while agents (not the idle employee) are actually working
  if (now >= (boss.nextAutoSay || 0)) {
    const working = employees.some(o => o.kind !== "idle" && (o.running || o.workAt));
    if (working) {
      boss.nextAutoSay = now + BOSS_AUTO_SAY_MIN_MS + Math.random() * BOSS_AUTO_SAY_SPAN_MS;
      boss.say(BOSS_AUTO_LINES[(Math.random() * BOSS_AUTO_LINES.length) | 0]);
      Sfx.ambient();
    } else {
      boss.nextAutoSay = now + 4000;
    }
  }
  markBossActivity();
}

/* ---------- employee AI (BFS path following) ---------- */
function updateEmployee(e, dt) {
  e.anim(dt);
  const now = performance.now();

  // coffee machine: anyone passing in front says the line (cooldown per employee)
  if (now >= e.nextCoffee && inZone(e.x, e.y, COFFEE_ZONE)) {
    e.nextCoffee = now + COFFEE_COOLDOWN_MS;
    e.say(COFFEE_LINE);
    Sfx.coffee();
  }

  if (e === engaged) {
    e.moving = false;
    e.setDir(boss.x - e.x, boss.y - e.y);    // face the boss while hired
    return;
  }
  if (e.returningHome) { updateReturnHome(e, dt, now); return; }
  if (now < e.blockedUntil) { e.moving = false; return; }

  const isAgent = e.kind !== "idle";

  // idle employees say "I have nothing to do" on the ambient cadence (~1/min) —
  // they never claim to be working.
  if (!isAgent && now >= e.nextAmbient) {
    e.nextAmbient = now + AMBIENT_MIN_MS + (-Math.log(Math.random()) * AMBIENT_SPAN_MS);
    e.say(IDLE_LINE);
    Sfx.ambient();
  }

  // work deadline (starts at the attraction moment): IDLE employees free the spot and
  // enter a cooldown; AGENT employees have workDeadline = 0 (no timeout — they stay at
  // the desk until the agent instance closes).
  if (e.workDeadline && now >= e.workDeadline) {
    e.workSpot = null;
    e.workAt = null;
    e.workDeadline = 0;
    e.noWorkUntil = now + WORK_COOLDOWN_MS;
  }

  // standing at the desk (working pose) — direction is kept as-is
  if (e.workAt && !e.workSpot) {
    e.moving = false;
    return;
  }

  // heading to the desk spot: BFS route follow with anti-jam (no progress for 2 s → replan)
  if (e.workSpot) {
    const [tx, ty] = e.workRoute[0];
    const dx = tx - e.x, dy = ty - e.y;
    const dist = Math.hypot(dx, dy);
    if (dist < 10) {
      e.workRoute.shift();
      if (!e.workRoute.length) { e.workSpot = null; e.moving = false; return; }
    } else {
      const sp = e.speed * dt / 1000;
      const ox = e.x, oy = e.y;
      e.move(dx / dist * sp, dy / dist * sp);
      e.moving = Math.hypot(e.x - ox, e.y - oy) > 0.1;
      if (e.moving) e.setDir(e.x - ox, e.y - oy);
    }
    e.stuckT += dt;
    if (e.stuckT > 2000) {
      const moved = Math.hypot(e.x - e.lastX, e.y - e.lastY);
      e.lastX = e.x; e.lastY = e.y; e.stuckT = 0;
      if (moved < 3) {
        // jammed against furniture: free the spot and retry (agents) or cool down (idle)
        e.workSpot = null; e.workAt = null; e.workRoute = null;
        if (isAgent && e.running) { attractAgentToDesk(e); return; }
        e.noWorkUntil = now + WORK_COOLDOWN_MS;
        return;
      }
    }
    return;
  }

  // an agent employee that just started running walks to a free desk and stays there
  if (isAgent && e.running && now >= e.noWorkUntil) {
    attractAgentToDesk(e);
    return;
  }

  // desk attraction (idle employees only): passing in front of a desk pulls the
  // employee to its spot for the 20 s WORK_MS session (same as before)
  if (!isAgent && now >= e.noWorkUntil) {
    for (const s of WORK_SPOTS) {
      if (Math.hypot(e.x - s.x, e.y - s.y) <= WORK_R) {
        const taken = employees.some(o => o !== e && (o.workSpot === s || o.workAt === s));
        const tx = taken ? s.x + 30 : s.x;         // stand to the right when the spot is taken
        const ay = s.y + 24;                       // approach point in the aisle (clear of the chair)
        const route = pathTo(e.x, e.y, tx, ay);
        if (!route) continue;                      // desk unreachable from here — try the next one
        e.workSpot = s;
        e.workAt = s;
        e.workDeadline = now + WORK_MS;            // the 20 s run from the attraction moment
        e.workRoute = [...route, [tx, ay], [tx, s.y]];   // BFS to the aisle, then straight up
        e.stuckT = 0; e.lastX = e.x; e.lastY = e.y;
        break;
      }
    }
  }

  if (!e.path || e.path.length === 0) {
    if (now < e.idleUntil) { e.moving = false; return; }
    for (let tries = 0; tries < 6; tries++) {            // pick a reachable random target
      const t = randomWalkable();
      const p = pathTo(e.x, e.y, t[0], t[1]);
      if (p) { e.path = p; e.path.push(t); e.stuckT = 0; e.lastX = e.x; e.lastY = e.y; break; }
    }
    if (!e.path || e.path.length === 0) { e.idleUntil = now + 1200; return; }
  }
  const dx = e.path[0][0] - e.x, dy = e.path[0][1] - e.y;
  const dist = Math.hypot(dx, dy);
  if (dist < 12) {
    e.path.shift();
    if (!e.path.length) { e.idleUntil = now + 800 + Math.random() * 2200; e.moving = false; return; }
  }
  // stuck detection: little progress in a while -> replan (wandering only)
  e.stuckT += dt;
  if (e.stuckT > 2000) {
    if (Math.hypot(e.x - e.lastX, e.y - e.lastY) < 2) e.path = null;
    e.lastX = e.x; e.lastY = e.y; e.stuckT = 0;
  }
  const sp = e.speed * dt / 1000;
  const ox = e.x, oy = e.y;
  e.move(dx / dist * sp, dy / dist * sp);
  e.moving = Math.hypot(e.x - ox, e.y - oy) > 0.1;
  if (e.moving) e.setDir(e.x - ox, e.y - oy);
}

/* agent employee: walk to the nearest FREE desk whose approach is REACHABLE (BFS around the
   furniture — a straight line from the door would jam against desks) and stay there forever */
function attractAgentToDesk(e) {
  let best = null, bd = Infinity, bestRoute = null;
  for (const s of WORK_SPOTS) {
    const taken = employees.some(o => o !== e && (o.workSpot === s || o.workAt === s));
    if (taken) continue;
    const route = pathTo(e.x, e.y, s.x, s.y + 24);     // BFS to the aisle approach point
    if (!route) continue;                               // unreachable — try the next desk
    if (route.length < bd) { bd = route.length; best = s; bestRoute = route; }
  }
  if (!best) {
    // every desk taken or unreachable — queue right of the first desk (best-effort)
    best = WORK_SPOTS[0];
    bestRoute = pathTo(e.x, e.y, best.x + 30, best.y + 24) || [];
  }
  e.workSpot = best;
  e.workAt = best;
  e.workDeadline = 0;                                // no 20 s timeout for agent employees
  e.workRoute = [...bestRoute, [best.x, best.y + 24], [best.x, best.y]];
  e.stuckT = 0; e.lastX = e.x; e.lastY = e.y;
}

/* return to the door after the agent instance closed: attraction is permanently
   disabled in this state, so the employee can never get stuck on a desk again */
function updateReturnHome(e, dt, now) {
  if (Math.hypot(e.x - e.homeX, e.y - e.homeY) < 16) {
    despawnEmployee(e);
    return;
  }
  if (!e.homePath || !e.homePath.length) {
    e.homePath = pathTo(e.x, e.y, e.homeX, e.homeY) || [];
    if (e.homePath.length) e.homePath.push([e.homeX, e.homeY]);
    e.stuckT = 0; e.lastX = e.x; e.lastY = e.y;
  }
  let tx, ty;
  if (e.homePath.length) {
    [tx, ty] = e.homePath[0];
  } else {
    tx = e.homeX; ty = e.homeY;                     // unreachable via BFS — go straight
  }
  const dx = tx - e.x, dy = ty - e.y;
  const dist = Math.hypot(dx, dy);
  if (dist < 12) {
    e.homePath.shift();
    if (!e.homePath.length) { despawnEmployee(e); return; }
    [tx, ty] = e.homePath[0];
  }
  e.stuckT += dt;
  if (e.stuckT > 2500) {
    if (Math.hypot(e.x - e.lastX, e.y - e.lastY) < 2) e.homePath = null;   // replan
    e.lastX = e.x; e.lastY = e.y; e.stuckT = 0;
  }
  const d2 = Math.hypot(tx - e.x, ty - e.y);
  const sp = e.speed * dt / 1000;
  const ox = e.x, oy = e.y;
  e.move(d2 ? (tx - e.x) / d2 * sp : 0, d2 ? (ty - e.y) / d2 * sp : 0);
  e.moving = Math.hypot(e.x - ox, e.y - oy) > 0.1;
  if (e.moving) e.setDir(e.x - ox, e.y - oy);
}

function despawnEmployee(e) {
  const i = employees.indexOf(e);
  if (i >= 0) employees.splice(i, 1);
  if (engaged === e) { engaged = null; engagedBy = null; }
}

/* ---------- engagement ---------- */
function engage(emp, by) {
  if (engaged) return;
  engaged = emp;
  engagedBy = by;
  engagedSince = performance.now();    // the 1-min inactivity timer starts now
  emp.path = null;                     // forget the old destination while hired
  emp.say("Boss, give orders!");
  Sfx.engage();
}

function disengage() {
  if (!engaged) return;
  engaged.blockedUntil = performance.now() + REENGAGE_PAUSE;   // pause before wandering again
  engaged = null;
  engagedBy = null;
}

function updateEngagement() {
  const now = performance.now();
  if (engaged) {
    if (now - engagedSince > INACTIVE_MS ||
        (engagedBy === "contact" && Math.hypot(boss.x - engaged.x, boss.y - engaged.y) > DISENGAGE_R)) {
      disengage();
    }
  } else if (!bossAuto) {
    // the auto-piloting boss is "off duty": it cannot hire by contact (tab/mouse still work)
    for (const e of employees) {
      if (e.blockedUntil < now && Math.hypot(boss.x - e.x, boss.y - e.y) <= ENGAGE_R) {
        engage(e, "contact");
        break;
      }
    }
  }
}

/* ---------- wall phone + coffee machine ---------- */
function inZone(x, y, z) {
  return x >= z.x && x <= z.x + z.w && y >= z.y && y <= z.y + z.h;
}

function updatePhone() {
  const inZone_ = inZone(boss.x, boss.y, PHONE_ZONE);
  if (inZone_ && !phoneActive) {
    boss.say("I have to call", "phone");                 // speech: bubble + chat log
    markBossSpeech();
    Sfx.phone();
  } else if (inZone_ && boss.bubble) {
    boss.bubble.until = Infinity;                        // keep the phone bubble up
  }
  if (!inZone_ && phoneActive && boss.bubble && boss.bubble.kind === "phone") boss.bubble = null;
  phoneActive = inZone_;
}

let inCoffee = false;
function updateCoffee() {
  const inZone_ = inZone(boss.x, boss.y, COFFEE_ZONE);
  if (inZone_ && !inCoffee) {
    boss.say(COFFEE_LINE, "coffee");
    markBossSpeech();
    Sfx.coffee();
  } else if (inZone_ && boss.bubble) {
    boss.bubble.until = Infinity;                        // keep the coffee bubble up
  }
  if (!inZone_ && inCoffee && boss.bubble && boss.bubble.kind === "coffee") boss.bubble = null;
  inCoffee = inZone_;
}

/* ---------- chat interaction ---------- */
function sendMessage() {
  const input = document.getElementById("input");
  const text = input.value.trim();
  input.value = "";
  if (!text) return;
  markBossSpeech();
  Sfx.send();

  // Send is inhibited unless an employee is engaged (and the target can be talked to).
  if (!connected) { Chat.say("", "OFFICE MANAGER — not connected to AgentBridge", "sys"); return; }
  if (!engaged) { Chat.say("", "hire an employee first (tab or click)", "sys"); return; }
  if (engaged.kind === "subagent" || engaged.kind === "stateless") {
    Chat.say("", (engaged.label || "this employee") + " cannot be chatted with", "sys");
    return;
  }
  if (engaged.running) { Chat.say("", (engaged.label || engaged.name) + " is still working…", "sys"); return; }

  // What the user typed appears in the bubble above the boss's head; the server echoes the
  // message into the chat log (role "user"), so the log is consistent across tabs.
  if (!(boss.bubble && boss.bubble.kind))           // keep a persistent zone bubble (phone/coffee)
    boss.bubble = { text, until: performance.now() + BUBBLE_MS, kind: null };
  ws.send(JSON.stringify({ type: "chat_send", empId: engaged.empId, prompt: text }));
}

/* Tab cycles through the employees: hires the nearest one first, then every
   further press releases the current hire and hires the next in the roster. */
function engageNextTab() {
  const now = performance.now();
  if (engaged) {
    const idx = employees.indexOf(engaged);
    engaged.blockedUntil = now + REENGAGE_PAUSE;   // prevent instant re-hire by contact
    engaged = null; engagedBy = null;
    if (employees.length) engage(employees[(idx + 1) % employees.length], "tab");
    return;
  }
  let best = null, bd = Infinity;
  for (const e of employees) {
    const d = Math.hypot(boss.x - e.x, boss.y - e.y);
    if (d < bd) { bd = d; best = e; }
  }
  if (best) engage(best, "tab");
}

function engageAt(px, py) {
  if (engaged) return;
  for (const e of employees) {
    if (px >= e.fx - 24 && px <= e.fx + 24 && py >= e.fy - CHAR_H + 4 && py <= e.fy - 8) {
      engage(e, "mouse");
      return;
    }
  }
}

/* ---------- input ---------- */
function initInput() {
  const input = document.getElementById("input");
  window.addEventListener("keydown", (e) => {
    Sfx.ensure();
    switch (e.key) {
      case "ArrowUp":    keys.up = true; break;
      case "ArrowDown":  keys.down = true; break;
      case "ArrowLeft":  keys.left = true; break;
      case "ArrowRight": keys.right = true; break;
      case "Tab":        engageNextTab(); break;
      case "Escape":
        // Esc on a session employee CLOSES the conversation (the agent instance ends → the
        // employee walks back to the door and despawns); on any other employee it just releases.
        if (engaged && engaged.kind === "session" && connected)
          ws.send(JSON.stringify({ type: "close", empId: engaged.empId }));
        disengage();
        break;
      case "Enter":      sendMessage(); break;
      default: return;
    }
    e.preventDefault();
  });
  window.addEventListener("keyup", (e) => {
    switch (e.key) {
      case "ArrowUp":    keys.up = false; break;
      case "ArrowDown":  keys.down = false; break;
      case "ArrowLeft":  keys.left = false; break;
      case "ArrowRight": keys.right = false; break;
    }
  });
  // keep the cursor in the input at all times
  const focus = () => input.focus();
  window.addEventListener("blur", () => { keys.up = keys.down = keys.left = keys.right = false; setTimeout(focus, 60); });
  document.addEventListener("click", focus);

  // mouse hire (canvas coordinates from screen coords)
  const cv = document.getElementById("game");
  cv.addEventListener("mousedown", (e) => {
    const r = cv.getBoundingClientRect();
    const s = r.width / W;
    engageAt((e.clientX - r.left) / s, (e.clientY - r.top) / s);
  });

  // sound toggle
  const snd = document.getElementById("snd");
  snd.addEventListener("click", () => {
    Sfx.muted = !Sfx.muted;
    snd.textContent = Sfx.muted ? "SOUND:OFF" : "SOUND:ON";
    snd.classList.toggle("off", Sfx.muted);
    Sfx.ensure();
  });

  focus();
}

/* ---------- rendering ---------- */
const cv = document.getElementById("game");
const ctx = cv.getContext("2d");

function fitCanvas() {
  // the office + chat form one centred column: the chat is never wider than the office
  const s = Math.min(innerWidth / W, (innerHeight - CHAT_H) / H);
  const w = W * s;
  document.getElementById("gamebox").style.width = w + "px";
  cv.style.width = w + "px";
  cv.style.height = (H * s) + "px";
}
window.addEventListener("resize", fitCanvas);

function drawPerson(p) {
  const d = DIRS[p.dir];
  const sheet = p.moving ? p.img.walk : p.img.idle;
  ctx.save();
  if (d.m) { ctx.translate(p.x * 2, 0); ctx.scale(-1, 1); }
  ctx.drawImage(sheet, p.frame * FRAME, d.r * FRAME, FRAME, FRAME, p.fx - CHAR_H / 2, p.fy - CHAR_H, CHAR_H, CHAR_H);
  ctx.restore();
}

function labelVisible() {
  return !phoneActive && performance.now() - lastBossActivity > LABEL_IDLE_MS;
}

function drawLabel() {
  if (!labelVisible()) return;
  ctx.font = PIX_FONT;
  const w = ctx.measureText("boss").width + 8, h = 11;
  const x = boss.fx - w / 2, y = boss.fy - CHAR_H - h - 2;
  ctx.fillStyle = "#111";
  ctx.fillRect(x, y, w, h);
  ctx.strokeStyle = "#baa272";
  ctx.lineWidth = 1;
  ctx.strokeRect(x, y, w, h);
  ctx.fillStyle = "#baa272";
  ctx.fillText("boss", x + 4, y + h - 2);
}

/* small nameplate over agent employees (they may share a sprite with other
   employees, so the label tells them apart) */
function drawEmployeeLabel(p) {
  if (!p.label || p.returningHome) return;
  ctx.font = PIX_FONT;
  const w = ctx.measureText(p.label).width + 6, h = 10;
  const x = p.fx - w / 2, y = p.fy - CHAR_H - h - 2;
  ctx.fillStyle = "rgba(0,0,0,0.6)";
  ctx.fillRect(x, y, w, h);
  ctx.fillStyle = "#9fb8c4";
  ctx.fillText(p.label, x + 3, y + h - 3);
}

/* wrap a speech text and return the bubble size (w, h) at the pixel font size */
const BUBBLE_PAD = 5, BUBBLE_LH = 10;
function wrapBubble(text) {
  ctx.font = PIX_FONT;
  const maxW = 172;
  const lines = [];
  let cur = "";
  for (const w of text.split(" ")) {
    const test = cur ? cur + " " + w : w;
    if (ctx.measureText(test).width > maxW && cur) { lines.push(cur); cur = w; }
    else cur = test;
  }
  if (cur) lines.push(cur);
  return {
    lines,
    w: Math.max(...lines.map(l => ctx.measureText(l).width)) + BUBBLE_PAD * 2,
    h: lines.length * BUBBLE_LH + BUBBLE_PAD * 2,
  };
}

function drawBubble(p) {
  const b = p.bubble;
  if (!b || b.until < performance.now()) return;
  const { lines, w, h } = wrapBubble(b.text);

  let baseY = p.fy - CHAR_H - 4;
  if (p.isBoss && labelVisible()) baseY -= 13;      // stack above the "boss" nameplate
  else if (!p.isBoss && p.label && !p.returningHome) baseY -= 12;   // above the employee nameplate
  const x = p.fx - w / 2;
  let y = baseY - h;
  if (y < 2) y = p.fy + CHAR_H / 2;                 // below the head when there is no room above

  ctx.fillStyle = "#fff";
  ctx.strokeStyle = "#000";
  ctx.lineWidth = 2;
  ctx.fillRect(x, y, w, h);
  ctx.strokeRect(x, y, w, h);
  const cx = p.fx, above = y > p.fy;                // tail points towards the speaker
  ctx.beginPath();
  if (above) { ctx.moveTo(cx - 4, y); ctx.lineTo(cx + 4, y); ctx.lineTo(cx, y - 4); }
  else { ctx.moveTo(cx - 4, y + h); ctx.lineTo(cx + 4, y + h); ctx.lineTo(cx, y + h + 4); }
  ctx.closePath();
  ctx.fillStyle = "#fff";
  ctx.fill();
  ctx.stroke();
  ctx.fillStyle = "#000";
  lines.forEach((l, i) => ctx.fillText(l, x + BUBBLE_PAD, y + BUBBLE_PAD + BUBBLE_LH - 2 + i * BUBBLE_LH));
}

function drawClock() {
  const t = new Date();
  const { x, y, r } = CLOCK;
  // drop shadow
  ctx.fillStyle = "rgba(0,0,0,0.35)";
  ctx.beginPath(); ctx.arc(x + 2, y + 3, r, 0, 7); ctx.fill();
  // case
  ctx.fillStyle = "#241200";
  ctx.beginPath(); ctx.arc(x, y, r, 0, 7); ctx.fill();
  // face
  ctx.fillStyle = "#f4ecd8";
  ctx.beginPath(); ctx.arc(x, y, r - 3, 0, 7); ctx.fill();
  ctx.strokeStyle = "#241200"; ctx.lineWidth = 1;
  ctx.beginPath(); ctx.arc(x, y, r - 3, 0, 7); ctx.stroke();
  // hour ticks
  ctx.strokeStyle = "#241200"; ctx.lineWidth = 2;
  for (let i = 0; i < 12; i++) {
    const a = i * Math.PI / 6;
    ctx.beginPath();
    ctx.moveTo(x + Math.cos(a) * (r - 6), y + Math.sin(a) * (r - 6));
    ctx.lineTo(x + Math.cos(a) * (r - 9), y + Math.sin(a) * (r - 9));
    ctx.stroke();
  }
  // hands
  const hand = (angle, len, wid, col) => {
    const a0 = (angle - 90) * Math.PI / 180;
    ctx.strokeStyle = col; ctx.lineWidth = wid;
    ctx.beginPath(); ctx.moveTo(x, y);
    ctx.lineTo(x + Math.cos(a0) * len, y + Math.sin(a0) * len);
    ctx.stroke();
  };
  hand((t.getHours() % 12 + t.getMinutes() / 60) * 30, r * 0.5, 2.5, "#241200");
  hand((t.getMinutes() + t.getSeconds() / 60) * 6, r * 0.68, 1.5, "#241200");
  hand(t.getSeconds() * 6, r * 0.78, 1, "#a83030");
  // hub
  ctx.fillStyle = "#241200";
  ctx.beginPath(); ctx.arc(x, y, 2, 0, 7); ctx.fill();
}

function render() {
  ctx.drawImage(IMG.ground, 0, 0);                                  // floor layer
  const all = [...employees, boss].sort((a, b) => renderDepth(a) - renderDepth(b));
  let cursor = 0;
  for (const p of all) {                                            // top layer in bands between chars
    const d = renderDepth(p);
    if (d > cursor) {
      ctx.drawImage(IMG.top, 0, cursor, W, d - cursor, 0, cursor, W, d - cursor);
      cursor = d;
    }
    drawPerson(p);
  }
  if (cursor < H) ctx.drawImage(IMG.top, 0, cursor, W, H - cursor, 0, cursor, W, H - cursor);
  drawClock();
  drawLabel();
  for (const p of employees) drawEmployeeLabel(p);
  for (const p of all) drawBubble(p);
  if (engaged) drawEngagedMark();                    // "!" above the hired employee (clears any bubble)
}

function drawEngagedMark() {
  const b = engaged.bubble;
  let y = engaged.fy - CHAR_H - 10;
  if (b && b.until >= performance.now()) y -= wrapBubble(b.text).h + 6;   // above the speech bubble
  const x = engaged.fx;
  ctx.fillStyle = "#f4d03f";
  ctx.strokeStyle = "#241200";
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.moveTo(x - 6, y);
  ctx.lineTo(x + 6, y);
  ctx.lineTo(x + 6, y + 8);
  ctx.lineTo(x, y + 14);
  ctx.lineTo(x - 6, y + 8);
  ctx.closePath();
  ctx.fill();
  ctx.stroke();
  ctx.fillStyle = "#241200";
  ctx.font = PIX_FONT;
  ctx.fillText("!", x - 1, y + 9);
}

/* ---------- main loop ---------- */
let lastT = performance.now();
function frame(now) {
  const dt = Math.min(now - lastT, 50);
  lastT = now;
  updateBoss(dt);
  for (const e of [...employees]) updateEmployee(e, dt);
  updateEngagement();
  updatePhone();
  updateCoffee();
  render();
  requestAnimationFrame(frame);
}

/* ---------- bootstrap ---------- */
async function boot() {
  Chat.init();
  try { await loadAssets(); } catch (err) {
    Chat.say("", "assets failed to load: " + err.message, "sys");
    return;
  }
  boss.img = IMG.chars["boss"];
  boss.path = null;
  boss.idleUntil = 0;
  boss.stuckT = 0;
  boss.lastX = boss.x; boss.lastY = boss.y;
  boss.nextAutoSay = 0;
  lastBossInput = performance.now();            // the auto-pilot starts after BOSS_AUTO_MS idle
  buildWaypoints();
  buildNav();
  buildBehind();
  initInput();
  fitCanvas();
  Chat.say("", "OFFICE MANAGER - arrows: move boss - tab/click: hire - enter: talk - esc: release/close", "sys");
  connect();
  requestAnimationFrame(frame);
}
boot();
