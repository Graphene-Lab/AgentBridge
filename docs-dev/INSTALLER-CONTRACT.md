# Tool installers and launchers — the AgentBridge contract

> **Audience:** maintainers of AgentBridge and of every tool that ships an installer
> (OdooTool, FreeCADTool, and future tools). This is a **developer document**: it lives in
> `docs-dev/` and is not shipped with the app.
>
> **The rule in one sentence:** an installer must leave the user in the single-agent
> configuration **AgentBridge + tool plugin + target application**, with a launcher icon that
> starts the application, starts AgentBridge in a **visible terminal**, and opens the **local start
> page** in the browser — a page the installer writes itself, which says what is running on this
> machine and links to the application, with the account. The assistant stays the terminal. Never
> the application alone,
> never a background assistant nobody can see, and never an ambiguous bare address that a module of
> the application can take over.

## The three-part stack

Every tool in this ecosystem is a **plugin**, not a standalone application. It needs a host
that runs the agent, and it needs the application it pilots. The installer is what puts the
three parts together and leaves them ready to use.

```mermaid
flowchart LR
  user["User"]
  launcher["Launcher icon<br/>(Desktop + application menu)"]
  ab["AgentBridge<br/>agent.exe / agent<br/>agent engine + terminal UI + API"]
  plugin["Tool plugin<br/>Tools/OdooTool, Tools/FreeCADTool, ..."]
  app["Target application<br/>Odoo at localhost:8069, FreeCAD, ..."]
  start["Local start page<br/>start.html<br/>(written by the installer)"]
  chat["Assistant chat<br/>localhost:5290/OfficeManager<br/>(second entrance, linked not opened)"]

  user -->|"double-click"| launcher
  launcher -->|"starts"| app
  launcher -->|"starts in a<br/>visible terminal"| ab
  launcher -->|"opens in the<br/>default browser"| start
  start -->|"one click:<br/>explicit application URL"| app
  start -->|"one click"| chat
  ab -->|"serves"| chat
  ab -->|"loads at startup"| plugin
  plugin -->|"drives the application"| app
  user -->|"prompt in the TUI"| ab
  app -.->|"running in the browser;<br/>the results appear here"| user
```

The user's first interaction is a **prompt in the assistant's terminal**: *"list the customers"*,
*"create a quotation for Acme"*, *"make this bracket 2 mm thicker"*. AgentBridge passes it to
the agent, the agent calls the tool plugin, and the plugin changes the application. The
application is already running and reachable one click from the start page, so the user watches the
result where it lands.

## AgentBridge and OfficeManager

`OfficeManager` is the **browser chat served by AgentBridge** at `/OfficeManager` (port 5290 in
the tool installers). It is a **second entrance**, not the first screen: the launcher starts the
terminal UI, which is where the user types the prompt and watches the agent work, and opens the
**start page**, which links to the target application so the result is visible where it lands. The
name is historical — the same page also carries the optional multi-agent view — but in a standard
tool installation it drives **one** agent.

| | **AgentBridge** | **OfficeManager** |
|---|---|---|
| What it is | The base program: agent engine, terminal UI, OpenAI-compatible API | The browser chat and office view served **by AgentBridge** at `/OfficeManager` |
| Role in a tool install | Hosts the agent, loads the tool plugin, and is the launcher's visible window | An alternative window for users who prefer a browser; reachable on purpose |
| Agents | One conversation/agent at a time — the normal local use | Can show many agent/subagent instances, but the installer does not configure them |
| Hardware | One model in VRAM, one agent working | Many agents would share the same GPU/VRAM |

OfficeManager is not a separate product that competes with AgentBridge: it is a surface inside
AgentBridge. The launcher does not open it, because a user who double-clicks an icon for an ERP
expects to see that ERP; opening the assistant's chat instead hid the application behind a page
nobody had asked for (reversed 2026-11-14, see OdooTool's `INSTALLER-CONTRACT.md`). The old
behaviour also produced the opposite complaint, recorded as
[OdooTool issue #7](https://github.com/Graphene-Lab/OdooTool/issues/7): an Odoo-only shortcut
showed the ERP login and the assistant was nowhere to be found. Both mistakes have the same
root — opening *one* of the two halves instead of starting both and showing the one the user
asked for.

A third variant appeared on 2026-11-20 and is the reason the destination is now a **local start
page**. Opening the application's bare address (`http://localhost:8069`) is ambiguous: with Odoo's
`website` module installed, that address serves a public site with a placeholder logo, and the ERP
hides behind a small sign-in link. A reader reported seeing exactly that, plus the vendor's
documentation site opened by the installer at the end, and concluded the installation had failed.
The start page is a file the installer writes itself: no process, no port, no module can shadow it,
and it links to the login page by an explicit address (`/web/login?db=odoo_agent`).

## Why the default is a single agent

AgentBridge is **privacy-first**: the intended deployment is a local LLM on the user's own
machine. That changes the economics of parallelism compared with a datacenter.

- **Local hardware is fixed.** One machine has one GPU and one VRAM budget. Adding a second
  agent does not add a second GPU; both agents share the same model and the same memory.
- **More agents do not finish sooner.** Agents are instantiated in parallel to save time only
  when the hardware scales with them. On a local machine, two agents contend for the same
  compute: they slow each other down, the total time does not improve, and the user waits
  longer for each answer.
- **The stack can collapse.** A second model instance or a second concurrent conversation can
  exhaust VRAM or RAM. The result is not "twice the work" but an out-of-memory error, a
  swapped-to-disk machine, or a crashed agent — exactly when the user is in the middle of a
  task.
- **In a datacenter the hardware grows with the agents.** That is the scenario where
  multi-agent orchestration pays off: more agents come with more compute. A home or small
  office PC is not that scenario.

Therefore the standard configuration for the end user is **one agent**:

```
AgentBridge  <->  tool plugin  <->  target application
```

This is why the installer's launcher starts the application, starts AgentBridge in a visible
terminal, and opens the application in the browser — one agent, not a multi-agent workload, and
not the application alone. The office view at `/OfficeManager` is the one place where several
agents can be started, and it is therefore **never opened automatically**: with a local model it
is a trap, because parallel agents share one GPU and one VRAM budget, slow each other down
instead of finishing sooner, and can exhaust the memory and take the stack down. It stays
available for the user who deliberately wants it, and for providers whose hardware scales with
the number of agents.

## The installer contract

An installer for a tool that pilots an application **must**:

1. **Install AgentBridge** (the public release archive for the platform).
2. **Install the tool plugin** into `Tools/<Tool>/` next to the AgentBridge executable and
   make it **active by default** (write its configuration, credentials and API key).
3. **Install the target application** and make it reachable on its local address.
4. **Create launcher icons** in the system application menu/launcher **and** on the desktop.
5. **Start the stack from the launcher**:
   - start the target application (if it is not already running) and wait until it answers;
   - start **AgentBridge** with the plugin active (if the assistant chat is not already
     answering — do not start a second instance);
   - open the **local start page** in the **system default browser**. It is a file the installer
     writes on every run: what is running here, the credentials that exist, and a link to the
     application by an explicit URL. It does **not** link the assistant chat: the assistant is the
     terminal, and a browser chat offered as the way in splits the product in two.
6. **Leave the user able to type a prompt immediately**, with the application already running
   one click behind the start page. Print the application URL and login as well, so the UI can be
   opened to watch the result.
7. **Be safe to re-run**: keep existing configuration, credentials and API keys instead of
   replacing them. Read the account back from the application and print what really exists —
   never a password this run did not apply.
8. **Make the first run simple**: automate database creation, admin credentials, API keys and
   any other one-time setup. At the end of a successful run, **open the start page** in the
   default browser and print its path, so the guidance is visible even when no browser can be
   opened; the vendor's documentation URL is printed as a link, not opened.
9. **Explain the flow in the final message**: the one-click icon, what it starts, the login
   credentials, and the manual commands for troubleshooting.
10. **Ship an uninstaller** for the same platforms. It removes the application's data (the
    database), the installation folder, the shortcuts and the firewall rule, and asks about
    AgentBridge separately because it can host other tools. Deletion of data requires two
    confirmations: a yes/no that lists what disappears, and the phrase **DESTROY ALL** typed in
    capitals. A silent flag is allowed only for unattended runs and must be named explicitly.

The contract is about the **default** behaviour. A user who explicitly wants a different
shortcut can be given an additional, clearly separate one; the standard icon stays as
specified.

If the tool embeds the assistant in the application itself (FreeCADTool's Chat workbench),
opening the application with the document is the equivalent of opening the chat: the rule is
that the user lands in the assistant, not that a browser tab is mandatory.

## Anti-patterns — do not do these

- **A shortcut that opens only the application.** The user would get Odoo/FreeCAD with no
  assistant and would have to discover AgentBridge on their own. This is the most common
  regression request ("just open Odoo"), and it breaks the product's one-click promise.
- **A shortcut that opens the application's bare address.** With Odoo's `website` module that
  address serves a public site, and the reader sees a page with a placeholder logo instead of the
  login form (2026-11-20). The start page links to the explicit login URL instead.
- **A shortcut that opens the assistant chat and nothing else.** This is the regression behind
  OdooTool issue #7: the user sees a chat and cannot find the application they installed. Both
  halves are started; the start page shows both doors.
- **Opening the vendor's documentation site as the final step.** It is a page about the product,
  not about the machine in front of the user, and it reads as "the installation produced nothing".
  Print the link; open the start page.
- **Waiting for the assistant chat before starting AgentBridge.** The wait loop must come
  *after* the assistant is launched (and only when it is not already answering), otherwise a
  cold start waits for a process that was never started and then reports a failure.
- **Asking the user to start AgentBridge manually** after the installer finishes. The
  installer's job is to leave a working stack, not a list of instructions.
- **Installing the plugin but leaving it inactive.** The plugin must be in `Tools/<Tool>/`
  with its configuration written before the launcher is created.
- **Configuring a multi-agent workload by default.** One agent is the standard local
  configuration (see above). The multi-agent view remains available, but the installer does
  not enable or encourage it.
- **Making the user go through the application's first-run wizard** (for example the Odoo
  database manager) when the installer can create the database and credentials itself.
- **Changing or removing the default launcher to satisfy a single user request** without
  reviewing this contract. Add an optional shortcut instead, and keep the standard icon.

## First run

The installer must not leave the user at a setup screen it could have completed itself.

For **Odoo 20 Community**, the OdooTool installer already:

- creates the `odoo_agent` database with the `base` modules (`-i base --without-demo`);
- creates the admin account, or reads the existing login back from the database;
- creates the API key and writes it to `agentbridge/PersistentData/odoo.json`;
- starts Odoo and waits until `/web/login` answers;
- writes `start.html` with the account and the two links, and opens it.

The user must therefore **not** see the Odoo database manager. If they do, the installation did
not complete: run the installer again (it resumes) and check the final summary. The vendor
documentation for the installed version is:

- Odoo 20 User Docs — <https://www.odoo.com/documentation/20.0/applications.html>
- Odoo 20 installation — <https://www.odoo.com/documentation/20.0/administration/install.html>

The installer prints these links in its final message **and opens the start page** in the default
browser at the end of a successful run. An earlier version opened the vendor's user-docs page
instead, on the theory that users who reached a working stack did not know what to do next; the
report from a reader showed that a page about the product, in the browser's language, reads as
"nothing was installed". The start page is about the machine in front of the user.

## Reference implementations

| Installer | What it does |
|---|---|
| **OdooTool — Windows** (`OdooTool/installer/install.ps1`) | Writes `start.ps1`, `start.html` and the **AI Odoo** shortcut on the Desktop and in the Start Menu. The launcher starts Odoo, starts `agentbridge\agent.exe` in a visible console if 5290 is free, then opens `start.html`. The start page links to `/web/login?db=odoo_agent` and to the chat. At the end of the install it opens the same page. `uninstall.ps1` / `uninstall.bat` remove it all and require the phrase DESTROY ALL. |
| **OdooTool — Linux/WSL** (`OdooTool/installer/install.sh`) | The same, with `start.sh`, `start.html` and `ai-odoo.desktop` in `~/.local/share/applications` and `~/Desktop`; `xdg-open` for the page; `uninstall.sh` for removal. |
| **FreeCADTool** (`FreeCADTool/installer/`) | Creates the **AI FreeCAD** launcher; it starts AgentBridge and opens FreeCAD with the document, so the user can go from the desktop to a prompt that edits the open model. |

## Checklist for a new installer

- [ ] AgentBridge is installed and starts on the target platform.
- [ ] The tool plugin is in `Tools/<Tool>/` and active by default.
- [ ] The target application is installed and reachable on its local address.
- [ ] A launcher icon exists in the application menu/launcher **and** on the desktop.
- [ ] The launcher starts the application and AgentBridge, then opens the **local start page**
      in the default browser; the page links to the application by an explicit URL and does not
      link the assistant chat.
- [ ] The final message prints the application URL and login, so its UI can be opened to watch
      the result.
- [ ] The user can type a prompt immediately and the agent can act on the application.
- [ ] Re-running the installer preserves configuration and credentials, and prints only
      credentials that are true.
- [ ] First run is automated; the start page is opened in the default browser and its path is
      printed too.
- [ ] An uninstaller exists for each platform, requires the typed phrase, and asks about
      AgentBridge separately.
- [ ] The final message explains the one-click flow, the credentials and the manual commands.

## Where this contract is enforced

- **This document** — the architecture decision and its rationale.
- **`AgentBridge/AGENTS.md`** — the reminder every coding agent sees before touching an installer.
- **`OdooTool/INSTALLER-CONTRACT.md`** — the tool-side copy, with the OdooTool-specific details.
- **Header comments in `OdooTool/installer/install.ps1`, `install.sh` and `install.bat`** — the
  rule sits next to the code that creates the launcher.

If a future tool ships an installer, copy the relevant parts of this contract into that
repository and add the same header comment to its installer sources.

## Decision record

- **Date:** 2026-11-09
- **Decision:** the default launcher for a tool installation starts the target application and
  AgentBridge (single agent) with the tool plugin already active, and opens the assistant chat
  (`/OfficeManager`, served by AgentBridge) in the default browser. The application URL and
  login are printed; its UI is opened by the user when they want to watch the result.
- **Rationale:** privacy-first local LLM deployment; fixed GPU/VRAM makes parallel agents
  contend instead of scale, with no time benefit and a risk of exhausting memory.
- **Consequences:** every tool installer creates the launcher in the application menu and on
  the desktop, automates the first run where possible, opens the vendor's first-steps page at
  the end of a successful installation, and keeps the standard icon stable even when a user
  asks for an application-only shortcut.

### Revised 2026-11-14 (OdooTool issue #7)

- **Decision:** the browser opens the **application**, not the assistant chat. The chat becomes a
  second entrance, reached on purpose. Rationale: a user who double-clicks an icon for an ERP
  expects the ERP, and the chat hid it.

### Revised 2026-11-20 (reader report)

- **Decision:** the browser opens a **local start page** written by the installer, which links to
  the application by an explicit URL (`/web/login?db=odoo_agent` for Odoo), shows the account that
  really exists, and says which window is which. The assistant stays the terminal window, so the
  page does not link the office chat. The vendor's documentation URL is printed, not opened.
- **Rationale:** the application's bare address is ambiguous — with Odoo's `website` module it
  serves a public site with a placeholder logo — and the documentation site reads as "nothing was
  installed". A reader reported both on 2026-11-20 and concluded the installation had failed.
- **Consequences:** every tool installer writes a start page and opens it; re-runs print the login
  read back from the application and never a password that was not applied; each installer ships
  an uninstaller whose data deletion requires the typed phrase **DESTROY ALL**.
