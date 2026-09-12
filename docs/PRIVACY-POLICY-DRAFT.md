# Privacy policy — Graphene Agent Bridge (DRAFT, not published)

> **Status: draft.** The owner should read it once before it is used anywhere public.
> Everything below is taken from what the product actually does in this repository.

**Product:** Graphene Agent Bridge (Store product id `9P61PN50Q957`)
**Publisher:** Graphene-Lab

## In short

We do not collect your data. Not your name, not your files, not your prompts, not your settings.
The product has no account, no sign-in, no registration, and no telemetry. Nothing is sent to us,
ever.

The program runs on your own machine. Your data stays there.

## What we collect

Nothing.

- No analytics, no advertising, no usage tracking.
- No phone-home and no update channel of our own — updates come from the Microsoft Store.
- The Package Support Framework inside the package was **compiled from Microsoft's public source
  code on purpose**, with its telemetry provider identifier left as the empty placeholder. The
  binaries from the NuGet package, which carry Microsoft's real telemetry destination, were
  rejected. So there is no address to send telemetry to, even if a component tried.

## Where your data lives

Everything stays on your machine:

| Location | What it holds |
|---|---|
| `PersistentData\` | your settings, including model provider names and the API keys you typed |
| `attachments\` | files you attached to a conversation |
| `tui-screenshots\` | screenshots you took from the terminal |
| `GiraffeAIWebClient\` | the bundled web client's files |

In the Store package these folders are redirected into your own user profile
(`%LOCALAPPDATA%\Packages\41836WindowsPhne.GrapheneAgentBridge_6mfwch41bwvk2\LocalCache\`),
because the package itself is read-only. They are not copied anywhere else.

**Please treat the API keys in `PersistentData\` as secrets** and keep that machine protected.

## This product is built to keep your data at home

AgentBridge was made for people and organisations that do not want to depend on outside AI
services. You run it yourself. You choose what it talks to.

**For the strongest privacy, run a local model on your own hardware.** In that setup your prompts
and your files never leave your machine at any point. This is the intended way to use the product
when privacy is the goal. You need enough local hardware for the model you want to run.

## If you connect an outside AI provider, that provider is yours to choose

The product can also talk to an external model provider. When you do that, your content goes from
**your** machine to **the provider you picked**, using **the API key you supplied**. We are not in
that path and we cannot see it.

**That provider's privacy policy governs your data from that moment.** Before connecting one, read
its privacy policy and decide whether you accept it. The choice, and the key, are yours.

### The anonymisation switch

AgentBridge has a built-in anonymisation feature for this case
(`LLM:Anonymize`, on the command line: `--LLM:Anonymize true`).

When it is on, the program replaces personal details — names, keys and other sensitive identifiers —
before the request is sent to the outside model, and puts them back when the answer comes. The agent
keeps working at full strength, but the outside machine never sees what should stay private. It adds
no noticeable delay and is invisible in normal use.

This is optional. It is there for people who use an outside provider and still want to keep
personal details out of the request.

**Note:** we do not collect the anonymised data either. Anonymisation protects what you send to the
provider you chose. It is not a collection channel, and nothing arrives at us in any form.

## Children

This is a professional office tool. It is not made for children.

## Changes

If this policy changes, the new version will appear at the same address with a new date.

## Contact

Graphene-Lab — https://graphenelab.it

---

## What this is based on (internal, not part of the published text)

- **No vendor telemetry.** The Store package ships the Package Support Framework compiled from
  Microsoft's public source so the telemetry provider GUID in `include/Telemetry.h` stays the
  zeroed placeholder; the NuGet binaries carrying Microsoft's real provider id were rejected.
  See `docs-dev/STORE-PUBLISHING.md`, "PSF ships telemetry, and there is no switch for it".
- **No self-update in the Store build.** `New-StoreMsix.ps1` seeds
  `PersistentData\appsettings.json` with `AutoUpdate.Enabled=false`, because the package directory
  is read-only and the Store delivers updates.
- **Storage locations** are the ones the application really uses: `PersistentData\`
  (`AppConfig.cs`), `attachments\` and `tui-screenshots\` (`Tui.cs`), `GiraffeAIWebClient\`
  (`WebClientUpdater.cs`) — recorded from the payload audit in `docs-dev/STORE-PUBLISHING.md` §5b.
- **Anonymisation** is `LLM:Anonymize` (`Program.cs:287-290`), threaded into every agent path
  (`AgentHarness`, `SessionStore`, `OfficeBridge`, `SipBridge`, `TelegramBridge`); described in
  `README.md` §"GDPR-Ready Anonymization" as stripping names, keys and sensitive identifiers before
  the request reaches an external provider and restoring them in the reply. Default is `false`.
- **Local-model operation** is the self-hosted design: the app is a local server, and running a
  local model keeps all traffic on the user's machine.
- The PFN quoted above is the one Partner Center assigned to this product
  (`41836WindowsPhne.GrapheneAgentBridge_6mfwch41bwvk2`).

**Before publishing, the owner should confirm** the contact address and legal entity name are
correct and complete for their jurisdiction.
