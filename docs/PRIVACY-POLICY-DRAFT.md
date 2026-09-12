# Privacy policy — Graphene Agent Bridge (DRAFT, not published)

> **Status: draft for the account owner's review.** This file is not a published policy and must
> not be linked from the Store listing until the owner has reviewed it and put it at a public URL.
> It is written from what the product actually does, as verified in this repository — see the
> "What this is based on" section at the end.

**Product:** Graphene Agent Bridge (Store product id `9P61PN50Q957`)
**Publisher:** Graphene-Lab
**Applies to:** the Windows package distributed through the Microsoft Store.

## In short

Graphene Agent Bridge runs on your own computer. It does not send your data to us. We do not
receive, store, or sell your content, and the package contains no telemetry.

## What we collect

Nothing. The package:

- does not contain analytics or advertising code;
- does not phone home, and has no update channel of its own — updates arrive through the Microsoft
  Store;
- contains the Package Support Framework built from Microsoft's public source, with the telemetry
  provider identifier left as the empty placeholder, so there is no destination for telemetry even
  if a component tried to emit it.

We have no user accounts, no sign-in, and no registration.

## What your data is, and where it goes

AgentBridge is a self-hosted assistant. It processes the content you give it — prompts, documents,
attachments — on the machine where you run it.

When you ask it to use a large language model, your content is sent **to the model provider that
you configure**, using **the API key that you supply**. That transfer is between your machine and
the provider you chose. The provider's own privacy policy governs what happens to the data after
that point. Which provider that is, and what it does with your content, is your choice and your
configuration — not ours.

Files you ask the assistant to work on are read from and written to the locations you point it at,
on your machine.

## Where settings are stored

Configuration and the working data listed below stay on your machine:

| Location | What it holds |
|---|---|
| `PersistentData\` | your settings, including the model provider names and the API keys you entered |
| `attachments\` | files you attached to a conversation |
| `tui-screenshots\` | screenshots you took from the terminal interface |
| `GiraffeAIWebClient\` | the bundled web client's files |

In the Store package these directories are redirected to your user profile
(`%LOCALAPPDATA%\Packages\41836WindowsPhne.GrapheneAgentBridge_6mfwch41bwvk2\LocalCache\`)
because the package itself is read-only. They are not copied anywhere else.

**Please treat the API keys in `PersistentData\` as secrets** and keep the machine they live on
protected.

## Children

The product is a professional office tool and is not intended for children.

## Changes to this policy

If this policy changes, the new version will be published at the same address with an updated date.

## Contact

Graphene-Lab — https://graphenelab.it

---

## What this is based on (internal, not part of the published text)

- **No vendor telemetry.** The Store package ships the Package Support Framework compiled from
  Microsoft's public source specifically so that the telemetry provider GUID in
  `include/Telemetry.h` stays the zeroed placeholder. The NuGet binaries, which carry Microsoft's
  real provider id, were deliberately rejected. See `docs-dev/STORE-PUBLISHING.md`, "PSF ships
  telemetry, and there is no switch for it".
- **No self-update in the Store build.** `New-StoreMsix.ps1` seeds
  `PersistentData\appsettings.json` with `AutoUpdate.Enabled=false`, because the package directory
  is read-only and the Store delivers updates.
- **Storage locations** are the ones the application actually uses: `PersistentData\`
  (`AppConfig.cs`), `attachments\` and `tui-screenshots\` (`Tui.cs`), `GiraffeAIWebClient\`
  (`WebClientUpdater.cs`) — recorded in `docs-dev/STORE-PUBLISHING.md` §5b from the payload audit.
- **Provider-side transfer** is the user's own configuration: the app talks to whatever model
  endpoint and key the user sets.
- The PFN quoted above is the one Partner Center assigned to this product
  (`41836WindowsPhne.GrapheneAgentBridge_6mfwch41bwvk2`).

**Before publishing, the owner should confirm:** the contact address and legal entity name are
correct and complete for their jurisdiction, and that nothing in the product has changed since this
was written.
