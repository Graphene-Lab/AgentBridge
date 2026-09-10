# How AgentBridge works — the basics

Three things are worth understanding before you use AgentBridge, because everything else follows from them: **what an agent is**, **what its tools are**, and **where its knowledge of your office lives**. This guide is short on purpose; the rest of the series goes into each area in detail.

## What an AI agent is

An ordinary AI chatbot *answers questions*. An agent **does the job**.

You give an agent an objective, in plain language — "prepare the supply contract for Rossi S.r.l.", "find the last invoice of a client and tell me if it was paid" — and it works it out by itself: it plans the steps, uses its instruments to carry them out, looks at the result, and corrects itself if something went wrong, until the objective is reached. That loop — plan, act, check, adjust — is what makes it an *agent* instead of a chat.

The best way to picture it is a new colleague. A new employee is intelligent, fast, and tireless, but on their first day they know nothing about your company: they need the archive, the list of clients, the paperwork, and the history of what has already been produced. Give them that and they start being useful immediately. An agent works exactly the same way: the assistant's intelligence comes from the AI model, while **its knowledge of your office** and **its ability to act** are things you provide — a working folder you configure, and a set of tools you can switch on and off. Both are covered below.

## Tools — the agent's hands

A **tool** is one specialised ability the agent can use when a task requires it: reading and writing files, browsing the web, building a Word document, an Excel workbook, a PowerPoint deck or a PDF report, reading and sending email, looking up places on a map, producing a podcast, and so on. Without tools an agent can only talk. With tools it produces real files and performs real actions — which is the whole point.

A small set of tools is **always on**, because the agent cannot work without them: the file tool (read and write inside its workspace), versioning (the history of what it produced, so nothing is ever lost) and scheduling (tasks it runs on a deadline). They are not meant to be switched off.

The remaining tools are chosen per conversation. From the chat window:

1. Type **`/tools`** (the older name **`/agent`** works too, and the same dialog is in the menu **Settings → Tools**).
2. The first line lists the tools that are always on.
3. Below it is a checklist: **↑↓** to move, **Space** to tick or untick a tool.
4. **Close** or **Esc** saves your choice straight away — no confirmation needed, and your choice is remembered the next time you start AgentBridge.

There is also a quicker way, if you already know what the conversation is about: name a ready-made combination after the command, for example **`/tools web-agent`** for a web assistant or **`/tools spreadsheet-files`** for a spreadsheet assistant. If you type a name that does not exist, AgentBridge answers with the list of the names you can use.

The status bar at the bottom of the window always shows the tools active in the conversation you are in, so you know at a glance what the agent can do. If you later add a new tool to the `Tools` folder, AgentBridge picks it up by itself within about half a minute — no restart, and no configuration.

## The documents area — the agent's knowledge of your office

If tools are the hands, the documents area is the **memory of the office**.

It is a normal folder on your computer, the one where your files already live. Everything you put in it becomes context for the agent: who your clients are, what your company does, the procedures, the paperwork, the documents you produced last month. The agent reads and searches that area when a task needs it, so it can answer and work using **your real documents** instead of guessing. Without a documents area the agent would still write well, but it would know nothing about *your* office — like a brilliant new colleague who has never seen the archive.

To set it up, from the chat window:

1. Type **`/setup`** (menu **Settings → Main settings**) and open the **General** tab.
2. In the **Documents path** field, choose the folder that holds your documents. The default is your personal Documents folder, and you can change it whenever you like.
3. Press **Save**.

AgentBridge starts reading that area in the background. The first pass over a large archive takes a few minutes on a big collection, and you can keep working while it happens. If one day you move to a different folder, just change the path again: the new area is read automatically.

Nothing is uploaded into a cloud storage: the area is a plain folder on your machine and it stays there. For what the assistant sends to the AI provider you choose — and how names and sensitive data can be hidden — see [Privacy and security](13-Privacy-and-Security.md).

## Adding context for a single job — attachments

Some tasks need material that is **not** part of the office archive yet: a file a client has just sent you, a specific practice, a couple of notes, or a document you want the agent to work on *exactly* — right now, for this one request. That material is given to the agent as an **attachment**, together with your message:

- **`/files add <path>`** uploads a file and attaches it to the conversation.
- Type **`@`** to pick a file you have already uploaded and attach or detach it.
- **`/files`** lists what is available, **`/files rm <id>`** removes one.

The attachment is converted into text the assistant can read, and it becomes part of the context of that request, together with the rest of the material the task concerns. If the agent delivers files back to you (a document, a report, a spreadsheet), they are saved for you next to the program, and **`/open`** shows them.

The important rule: **if a document already lives in the documents area, there is no need to attach it.** The agent reaches it on its own when the task calls for it — it is as if everything in that folder were already in its memory, ready when needed. Attaching is only for what is not there yet, or to point at one precise document.

## The two together

A concrete example: *"Draft the supply contract for Rossi S.r.l. based on our standard terms."*

- "our standard terms" is a template that already lives in the documents area, so the agent finds it by itself.
- The specific data of this client — the quote and the details of the practice — is material that arrived today: you attach it to the message.
- The document tool writes the contract, the file tool saves it in the workspace, and versioning keeps every version, so you can ask for changes without losing the previous draft.

You asked one thing, in plain language, and the agent used a permanent knowledge area plus the context of the moment to produce a finished file. If the result is not what you wanted, just say so in the chat: that is what the conversation is for.

## Where to go next

| If you want to | Read |
|---|---|
| Install AgentBridge and see it work | [Getting started](01-Getting-Started.md) |
| Choose the AI that powers the assistant | [Choosing your AI](02-Choosing-Your-AI.md) |
| The documents area in full detail | [Your documents area](04-Your-Documents-Area.md) |
| Produce Word, Excel, PowerPoint and PDF files | [Creating documents](05-Creating-Documents.md) |
| Understand what stays on your machine | [Privacy and security](13-Privacy-and-Security.md) |

The next guide in this series takes you through downloading and starting AgentBridge.
