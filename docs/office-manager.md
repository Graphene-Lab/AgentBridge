# OfficeManager — the 16-bit office view of your agents

OfficeManager is a web app served by AgentBridge at **`http://localhost:5290/OfficeManager`**.
It renders every live agent instance of the AIOrchestrator engine as an **employee** in a
16-bit top-down office, and lets you drive conversations from a text-adventure style chat.

## The big picture

An employee **is** an agent instance. There is always a pool of **idle employees** that
wander around the office saying *"I have nothing to do"* — they are not backed by any agent.
Every other employee is the visual representation of one **agent or subagent instance**,
no matter how that instance was created:

| Created by | Employee behaviour |
|---|---|
| TUI chat, `/v1/chat/completions` (session), SIP, Telegram | Appears at the door when the conversation starts, works at a desk while the agent runs, **returns to the door and disappears** when the conversation closes (idle timeout or explicit close) |
| Stateless one-shot API call (`/v1/chat/completions` without `session_id`) | Appears when the run starts, works at a desk, returns to the door when the run ends |
| A subagent launched by any agent | Same as above, tied to the subagent session lifecycle |
| Other applications that host AIOrchestrator directly (e.g. the AIOffice desktop app) | They **forward** their agent lifecycle events to AgentBridge (`AgentHarness.ForwardGlobalProgressTo` → `POST /v1/office/events`), so those agents appear as employees too |

> **Stateless employees are never hidden.** A stateless request is a legitimate agent
> instance — often a **small task created by an agent or a tool** (a quick lookup, a file
> check, a sub-step). OfficeManager is also a **granular monitoring surface**: you see every
> one of those instances appear, work at a desk and leave, in real time. Hiding them would
> defeat the purpose of the office.

While an agent is executing a tool, its speech bubble shows the **tool class and the method,
each split into words**: `FileTool.FileSearch` → **"File Tool, File Search"**.

## Playing the game

- **Arrows** move the boss — the human user's avatar.
- **Tab** / **click** an employee to hire (engage) it; **Esc** releases it — and on a
  session employee it **closes the conversation** (the employee walks back to the door and
  disappears).
- The bottom chat sends prompts to the **engaged employee** and shows the responses.
  Typed text appears in the bubble above the boss's head.
  - Chatting with an **idle** employee **creates a new agent** for it: it becomes a real
    session employee and a replacement idle employee appears at the door, so you can
    always spawn a new parallel agent.
  - **Send is inhibited** when no employee is engaged, and while the engaged agent is
    still working.
  - Subagent / one-shot employees are **visual only** — their conversation belongs to
    their parent agent.

## How it works

AgentBridge and OfficeManager talk over a **duplex WebSocket** (`/ws/office`): the server
pushes employee lifecycle events (spawn/assign/running/method/closed + chat messages), the
browser sends `chat_send` and `close`. The full wire protocol is documented in
`OfficeBridge.cs` (AgentBridge source).

Agents created by other processes are reflected by calling
`AgentHarness.ForwardGlobalProgressTo("http://localhost:5290")` once at their startup
(the AIOffice desktop app does this automatically; the URL is `AgentBridge:Url` in its
appsettings). The forwarding is best-effort — an unreachable AgentBridge is never an
error for the chat pipeline.

The OfficeManager app ships next to the executable (same rule as `docs/`) and never
calls external services: the pixel font is bundled locally.

## Third-party clients without `session_id` (dynamic-hash correlation)

`session_id` is a **proprietary additive extension** of `/v1/chat/completions` — it is not
part of the OpenAI spec, so a third-party implementation may never send it. Without it, a
multi-message chat would be a sequence of stateless one-shot agent instances (one employee
per message). To keep such a chat as **ONE conversation (one persistent employee)**,
AgentBridge correlates stateless requests by content:

- after every exchange the **full transcript** (roles + texts, including the assistant
  reply) is hashed with SHA-256 into a `hash → session` dictionary — the hash is
  *dynamic*, it changes at every output message;
- the next request's transcript minus its last message (the "previous part" the client
  resends) is hashed the same way; a hit routes the request to that conversation, creating
  and seeding the session when the first message was processed one-shot.

**Inherent limit:** correlation needs the client to **resend the accumulated transcript**
(which most OpenAI-style SDKs do). A client that sends only the newest message has no
"previous part" to hash: those requests stay one-shot — short-lived employees that remain
fully visible as granular monitoring (see above). Clients that want the canonical mapping
should send `session_id` (one employee per chat, guaranteed).
