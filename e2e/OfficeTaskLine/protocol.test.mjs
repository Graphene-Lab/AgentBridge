#!/usr/bin/env node
/*  Issue #98 — "Make task tracking easier in OfficeManager": protocol-side test.
 *
 *  Drives a RUNNING AgentBridge (default http://localhost:5290) and checks that the
 *  /ws/office stream carries the new `task` frame — the request an employee is
 *  working on — and that the snapshot carries it too, so a tab that connects later
 *  still sees what each employee is doing.
 *
 *  Run:   node protocol.test.mjs [bridgeUrl]
 *  Needs the agent up, e.g.
 *    DOTNET_ROOT=~/.dotnet ./bin/Release/net10.0/agent --headless --no-update \
 *        --SkipIndexingOnStartup true --Urls http://localhost:5290
 *
 *  No LLM key is needed: the task is reported BEFORE the run starts (that is the
 *  whole point of the hook in AgentTools.ExecuteSplit), so the frame arrives even
 *  when the agent itself cannot answer.
 */
const BRIDGE = process.argv[2] || "http://localhost:5290";
const WS_URL = BRIDGE.replace(/^http/, "ws") + "/ws/office";
const PROMPT = "Generate a presentation about quarterly results\n(with the chart slide)";
const EXPECTED = "Generate a presentation about quarterly results";

let pass = 0, fail = 0;
function check(name, cond, detail = "") {
  if (cond) { pass++; console.log("  OK   " + name); }
  else { fail++; console.log("  FAIL " + name + (detail ? "   " + detail : "")); }
}

function connect() {
  return new Promise((resolve, reject) => {
    const ws = new WebSocket(WS_URL);
    const frames = [];
    ws.onopen = () => resolve({ ws, frames });
    ws.onmessage = (e) => { try { frames.push(JSON.parse(e.data)); } catch (err) { } };
    ws.onerror = (e) => reject(new Error("ws error: " + (e.message || e.type)));
    setTimeout(() => reject(new Error("timeout connecting to " + WS_URL)), 8000);
  });
}

const sleep = (ms) => new Promise(r => setTimeout(r, ms));

async function main() {
  console.log("OfficeManager task-line protocol test against " + BRIDGE);

  // ── 1. the snapshot of a fresh tab carries the (empty) task field ──
  const a = await connect();
  a.ws.send(JSON.stringify({ type: "hello" }));
  await sleep(500);
  const snap = a.frames.find(f => f.type === "snapshot");
  check("snapshot received", !!snap);
  check("every employee carries a task field",
    !!snap && (snap.employees || []).every(e => typeof e.task === "string"),
    snap ? JSON.stringify((snap.employees || []).map(e => e.task)) : "no snapshot");

  // ── 2. chatting with the idle employee reports its request as a task ──
  const idle = (snap.employees || []).find(e => e.kind === "idle");
  check("an idle employee is available", !!idle);
  a.ws.send(JSON.stringify({ type: "chat_send", empId: idle.empId, prompt: PROMPT }));
  await sleep(2500);

  const assigned = a.frames.find(f => f.type === "assign" && f.empId === idle.empId);
  check("the idle employee became a session agent", !!assigned);

  const taskFrame = a.frames.find(f => f.type === "task" && f.empId === idle.empId);
  check("a task frame was broadcast", !!taskFrame,
    "frames: " + a.frames.map(f => f.type).join(","));
  check("the task is the first line of the request, whitespace-collapsed",
    taskFrame && taskFrame.task === EXPECTED,
    taskFrame ? JSON.stringify(taskFrame.task) : "no task frame");

  // ── 3. a tab that connects later sees the task in its snapshot ──
  const b = await connect();
  await sleep(500);
  const snap2 = b.frames.find(f => f.type === "snapshot");
  const emp2 = (snap2.employees || []).find(e => e.empId === idle.empId);
  check("a late-connecting tab sees the running task",
    !!emp2 && emp2.task === EXPECTED,
    emp2 ? JSON.stringify(emp2.task) : "employee gone from the snapshot");

  // ── 4. the task survives the end of the run (a finished task stays readable) ──
  await sleep(4000);
  const c = await connect();
  await sleep(500);
  const snap3 = c.frames.find(f => f.type === "snapshot");
  const emp3 = (snap3.employees || []).find(e => e.empId === idle.empId);
  check("the task is still there after the run",
    !!emp3 && emp3.task === EXPECTED,
    emp3 ? JSON.stringify({ task: emp3.task, running: emp3.running }) : "employee gone");

  // ── 5. a long request is capped, on a word boundary ──
  const LONG = "Please build a very long quarterly business presentation covering revenue, costs, hiring plans, market analysis, competitive landscape and the roadmap for next year";
  const idle2 = (snap3.employees || []).find(e => e.kind === "idle");
  check("a replacement idle employee is available", !!idle2);
  c.ws.send(JSON.stringify({ type: "chat_send", empId: idle2.empId, prompt: LONG }));
  await sleep(2000);
  const longTask = c.frames.filter(f => f.type === "task" && f.empId === idle2.empId).pop();
  check("the long request is reported", !!longTask,
    "frames: " + c.frames.map(f => f.type).join(","));
  const capped = longTask ? longTask.task : "";
  check("the capped line stays short", capped.length <= 91, "len=" + capped.length);
  check("the capped line ends with an ellipsis", capped.endsWith("…"), JSON.stringify(capped));
  const body = capped.slice(0, -1);
  check("the cap falls on a word boundary, not mid-word",
    LONG.startsWith(body) && LONG[body.length] === " ",
    JSON.stringify({ body, next: LONG[body.length] }));

  a.ws.close(); b.ws.close(); c.ws.close();
  console.log(fail === 0 ? "\nAll protocol checks passed." : "\n" + fail + " check(s) FAILED.");
  process.exit(fail === 0 ? 0 : 1);
}

main().catch(e => { console.log("  FAIL could not run: " + e.message); process.exit(1); });
