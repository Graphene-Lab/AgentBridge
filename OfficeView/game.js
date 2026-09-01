"use strict";

/* ============================================================
   Office Manager 16-Bit — top-down oblique office game.
   The boss (arrow keys) hires employees by contact, Tab or
   mouse; the bottom chat is a 16-bit text-adventure console.
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
const WORK_MS = 20000;                 // working duration at a desk
const WORK_COOLDOWN_MS = 15000;        // desk attraction disabled after a work session
const AMBIENT_LINES = ["I am working", "I'm in a hurry"];
const PIX_FONT = '8px "Press Start 2P", monospace';

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
  [IMG.top, IMG.ground] = await Promise.all([
    loadImage("assets/office.png"),
    loadImage("assets/office-ground.png"),
  ]);
  const names = ["boss", "employee A", "employee B", "employee C", "employee D", "employee E"];
  await Promise.all(names.map(name =>
    Promise.all([
      loadImage("assets/" + name + "/standard/walk.png"),
      loadImage("assets/" + name + "/standard/idle.png"),
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

/* ---------- world state ---------- */
const boss = new Person("Boss", null, 512, 620, true);
const employees = [];
const EMP_NAMES = ["employee A", "employee B", "employee C", "employee D", "employee E"];

let engaged = null;        // currently hired employee
let engagedBy = null;      // "contact" | "tab" | "mouse"
let engagedSince = 0;      // engagement timer: fired after 1 min of boss inactivity
let lastBossActivity = 0;  // last boss move / speech timestamp
let phoneActive = false;
let stepT = 0;
let waypoints = [];

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

function initEmployees() {
  employees.length = 0;
  const used = [];
  for (const name of EMP_NAMES) {
    let p, tries = 0;
    do {                                   // all employees start in front of the door
      p = [72 + Math.random() * 84, 152 + Math.random() * 60];
      tries++;
    } while (tries < 50 && used.some(u => (u[0] - p[0]) ** 2 + (u[1] - p[1]) ** 2 < 40 ** 2));
    used.push(p);
    const e = new Person(name, IMG.chars[name], p[0], p[1], false);
    e.path = null;
    e.idleUntil = Math.random() * 2000;
    e.blockedUntil = 0;
    e.stuckT = 0; e.lastX = p[0]; e.lastY = p[1];
    e.nextAmbient = performance.now() + AMBIENT_MIN_MS + (-Math.log(Math.random()) * AMBIENT_SPAN_MS);
    e.workSpot = null;                 // desk the employee is heading to (attraction)
    e.workAt = null;                   // desk the employee is working at (occupies the spot)
    e.workRoute = null;                // [approach point, spot] route while heading there
    e.workDeadline = 0;                // work session deadline, started at the attraction moment
    e.noWorkUntil = 0;                 // desk attraction disabled until this time
    e.nextCoffee = 0;                  // coffee line cooldown
    employees.push(e);
  }
}

/* ---------- boss movement ---------- */
const keys = { up: false, down: false, left: false, right: false };

function updateBoss(dt) {
  const vx = (keys.right ? 1 : 0) - (keys.left ? 1 : 0);
  const vy = (keys.down ? 1 : 0) - (keys.up ? 1 : 0);
  if (vx || vy) {
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
  }
  boss.anim(dt);
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
  if (now < e.blockedUntil) { e.moving = false; return; }

  if (now >= e.nextAmbient) {                            // ~1 ambient line per minute
    e.nextAmbient = now + AMBIENT_MIN_MS + (-Math.log(Math.random()) * AMBIENT_SPAN_MS);
    e.say(AMBIENT_LINES[(Math.random() * AMBIENT_LINES.length) | 0]);
    Sfx.ambient();
  }

  // work deadline (starts at the attraction moment): reached -> free + cooldown,
  // so employees jammed on the chair without reaching the spot still unlock
  if (e.workDeadline && now >= e.workDeadline) {
    e.workSpot = null;
    e.workAt = null;
    e.workDeadline = 0;
    e.noWorkUntil = now + WORK_COOLDOWN_MS;
  }

  // standing at the desk (working pose) until the deadline — direction is kept as-is
  if (e.workAt && !e.workSpot) {
    e.moving = false;
    return;
  }

  // heading to the desk spot: direct steering along the route
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
    return;
  }

  // desk attraction: passing in front of a desk pulls the employee to its spot
  if (now >= e.noWorkUntil) {
    for (const s of WORK_SPOTS) {
      if (Math.hypot(e.x - s.x, e.y - s.y) <= WORK_R) {
        const taken = employees.some(o => o !== e && (o.workSpot === s || o.workAt === s));
        const tx = taken ? s.x + 30 : s.x;         // stand to the right when the spot is taken
        const ay = s.y + 24;                       // approach point in the aisle (clear of the chair)
        e.workSpot = s;
        e.workAt = s;
        e.workDeadline = now + WORK_MS;            // the 20 s run from the attraction moment
        e.workRoute = [[tx, ay], [tx, s.y]];       // via the aisle, then straight up through the gap
        e.stuckT = 0; e.lastX = e.x; e.lastY = e.y;
        break;
      }
    }
  }

  if (!e.path || e.path.length === 0) {
    if (now < e.idleUntil) { e.moving = false; return; }
    for (let tries = 0; tries < 12; tries++) {           // pick a reachable random target
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
  } else {
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
  if (boss.bubble && boss.bubble.kind) Chat.say("Boss", text);   // persistent zone bubble stays (phone/coffee)
  else boss.say(text);
  Sfx.send();
  if (engaged) { engaged.say("Ok boss!"); Sfx.reply(); }
}

/* Tab cycles through the employees: hires the nearest one first, then every
   further press releases the current hire and hires the next in the roster. */
function engageNextTab() {
  const now = performance.now();
  if (engaged) {
    const idx = employees.indexOf(engaged);
    engaged.blockedUntil = now + REENGAGE_PAUSE;   // prevent instant re-hire by contact
    engaged = null; engagedBy = null;
    engage(employees[(idx + 1) % employees.length], "tab");
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
      case "Escape":     disengage(); break;
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
  for (const e of employees) updateEmployee(e, dt);
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
  buildWaypoints();
  buildNav();
  buildBehind();
  initEmployees();
  initInput();
  fitCanvas();
  Chat.say("", "OFFICE 16-BIT - arrows: move boss - tab = hire - esc = release - click: hire - enter: talk", "sys");
  requestAnimationFrame(frame);
}
boot();
