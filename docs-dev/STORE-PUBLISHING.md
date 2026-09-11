# AgentBridge — publishing to the Microsoft Store (complete reference)

Status: 2026-09-11. Written to be resumed later without re-discovering anything: every mechanism,
file, command and open question is here. Read `tools/store/README.md` for the short operational
version.

## 1. The rule that governs everything

**Only the Microsoft Store channel may differ from the product. The app source and the GitHub
archives never change because of the Store.**

- No application source (`.cs`, `.csproj`) is modified for the Store. Any Store-specific
  behaviour must come from the packaging layer (a file shipped inside the MSIX package, or a
  Package Support Framework fixup), never from app code.
- The GitHub archives (`agentbridge-<rid>.tar.gz`) are the product as designed: full tree,
  kokoro.onnx in both places, plugin natives for every OS.
- A payload trimming step was written and **reverted** for this reason — see §7.

## 2. Where everything lives

| Path | What it is |
|---|---|
| `.github/workflows/release.yml` | the whole pipeline: `check-version` → `build` (5 RID archives) → `store-msi` (Windows MSI for the Store) → `release` (GitHub Release + assets) → `store-submit` (Partner Center API, manual fallback without secrets) |
| `tools/store/New-StoreInstaller.ps1` | builds the per-machine WiX v5 MSI from a win-x64 payload; optionally signs payload PEs + the MSI (`-SignPfx` / `-SignThumbprint` / `SIGN_*` env vars) |
| `tools/store/Submit-Store.ps1` | Store Submission API client: swaps the draft package URL, commits, submits (needs an Entra app; individual accounts use the manual flow) |
| `tools/store/New-SignedMsiUrl.ps1` | mints a non-redirecting GitHub CDN URL (valid ~1 h) — fallback when the VPS proxy is unavailable |
| `tools/store/vps/mirror-msi.py` | the VPS streaming proxy (systemd `agentbridge-mirror` on 127.0.0.1:8686) |
| `tools/store/vps/agentbridge-msi.nginx.conf` | nginx snippet exposing the proxy under `aitechnology.it` |
| `tools/store/vps/install-agentbridge-mirror.sh` | idempotent installer for the proxy (root; restarts the daemon, reloads nginx, checks headers) |
| `tools/store/msix/AppxManifest.xml` | MSIX manifest template (`{{IDENTITY_NAME}}`, `{{PUBLISHER}}`, `{{VERSION}}` tokens) |
| `tools/store/New-StoreMsix.ps1` | builds + validates a Store MSIX from a win-x64 payload (Store-only; nothing is installed) |
| This file | the reference for choices, mechanisms, open questions |

## 3. The Store channel in use: win32 EXE/MSI + external download URL

The Partner Center product "Graphene AgentBridge" (product id `a456c3f0-cd83-475b-a8b3-18a1172a1901`)
is a **win32 EXE/MSI product**: Partner Center has no package upload, it fetches the installer
from an **HTTPS URL we host** and must answer `200` **without redirects**.

Mechanics:

1. `store-msi` builds `GrapheneAgentBridge-<version>.msi` (~1.29 GB) from the win-x64 archive —
   the payload is the exact tree the archive ships (single source of truth).
2. `release` attaches the MSI to the GitHub Release.
3. The MSI is served at **`https://aitechnology.it/agentbridge/msi/<version>`** — pinned to release
   tag `v<version>`, immutable (policy 10.2.9 requires the binary behind the URL not to change).
   `https://aitechnology.it/agentbridge/msi` (no version) serves the latest release and is for
   manual downloads only.
4. `store-submit` (or a human, in the manual fallback) points the Partner Center draft package at
   the versioned URL, commits and submits for certification.

### The VPS proxy (why it exists and what it is not)

- Microsoft rejects redirecting URLs, and GitHub release URLs always redirect → a plain GitHub
  link cannot be used. The proxy performs that hop internally.
- It stores **nothing**: it resolves the tag and streams the bytes from GitHub on demand. The
  directory holds ~44 KB of config; the VPS disk has ~3 GB free and the MSI would not fit anyway.
- It relays `Content-Length`, `Accept-Ranges`, `Content-Range` and answers `HEAD`; a range request
  returns `206`. Verified 2026-09-11: `HEAD /agentbridge/msi/1.26.09.11` → 200,
  `Content-Length: 1354113780`, `Accept-Ranges: bytes`, no redirect.
- Throughput measured on the VPS: **~33 MB/s** (522 MB in 15 s through the daemon). The 2 MB/s
  seen from the developer laptop was the local downlink, not the server.
- It has nothing to do with app updates: the app updates from GitHub (archives), and Store users
  are updated by the Store.

## 4. What the policies actually require (verified on learn.microsoft.com)

| Rule | Text / meaning |
|---|---|
| 10.2.9 | URL-delivered installers: `.msi`/`.exe` only; **the installer and all of its PE files must be signed** with a certificate chaining to a CA in the Microsoft Trusted Root Program; a **versioned URL whose binary does not change**; silent install (UAC allowed); standalone installer, **not a downloader stub** |
| 10.4.4 | the download must not take an unreasonable time and the install success rate must not be unreasonably low |
| MSI/EXE vs MSIX signing | "If you are submitting an MSI or EXE installer to the Store, the Store does not re-sign those files. You must Authenticode-sign your MSI/EXE installer yourself." — for **MSIX/AppX** instead: "The Microsoft Store will automatically re-sign your MSIX/AppX packages with a Microsoft certificate" (no certificate to buy) |
| MSIX package size | 25 GB max per package/bundle (this limit does **not** apply to MSI/EXE: there is no published size cap) |
| MSIX version | sections are integers ≤ 65535 and the **fourth must be 0** |
| MSIX elevation | "apps that require elevation for any part of their functionality won't be accepted into the Store" |
| MSIX install dir | "Your application writes to the install directory… This isn't supported, so you'll need to find another location"; the documented remedy is a **Package Support Framework** runtime fixup |
| MSIX utilities | the packaging guide warns against starting `cmd.exe`/PowerShell from a packaged app (Windows 10 S compatibility) — to confirm with Partner Center |

## 5. History: the 2026-09 certification failure and what was fixed

Report: policy **10.3.4 "App is Testable"** — "The product failed to install through the Store"
(tested on a Microsoft Surface Laptop), reviewed 2026-09-09.

Three defects were found; two are fixed and verified, one is open:

1. **Unsigned artifacts** (open). `agent.exe` and the MSI were `NotSigned`; no build step signed
   anything. Store MSI/EXE installers must be signed by us (§4). This is the remaining blocker.
2. **Mutable package URL** (fixed). The proxy served whatever the latest release was — it returned
   `1.26.09.11` while the submission reviewed on 09/09 shipped an older build. Now the versioned
   URL is pinned to the tag, and `store-submit` uses it.
3. **Unreliable download headers** (fixed). The proxy answered close-delimited over nginx (chunked,
   no `Content-Length`), ignored ranges and answered `501` to `HEAD`. It now relays the upstream
   length/range headers and implements `HEAD`.

Note: the download speed was *not* a defect — see §3.

## 5b. Payload audit of the flagged package (2026-09-12)

Run while waiting on `reportapp@microsoft.com`, to try to find the file behind the 10.2.3 flag,
which named neither a file nor a detection.

Package under audit: `GrapheneAgentBridge-1.26.9.6.msi`, 896,618,740 bytes,
SHA-256 `e32abbd509aa8cdb3e5b07578d13de493d4c74b112a33029b9d65ddb386d8323`, as served by
`https://aitechnology.it/agentbridge/msi/1.26.09.06`.

**How to read an MSI without installing it** (repeatable, nothing executed):

- Tables and streams: the `WindowsInstaller.Installer` COM object, `OpenDatabase(path, 0)`. In
  PowerShell 5.1 the `InvokeMember` flags must be `[System.Reflection.BindingFlags]::InvokeMethod`
  and `::GetProperty` — `'GetMethod'` is not a valid `BindingFlags` and fails at the cast.
- Real file names: `msiexec /a <msi> /qn TARGETDIR=<dir>` (an *admin* extract lays files out with
  their long names and does not register the product). **7-Zip is not enough**: it opens the MSI and
  its cabinets but yields only the 8.3 short names (`F2`, `F117`), which hides extensions and makes
  a PE/signature audit useless.

| Check | Result |
|---|---|
| Files / size | 929 files, 1.407 GB |
| PE files | 24 |
| — validly signed | 2, both Microsoft-signed BCL libs (`System.CommandLine.dll`, `System.ServiceModel.Syndication.dll`) |
| — unsigned | 22, all of ours: `agent.EXE` (731 MB), the six tool DLLs, the `ownaudio_ffi` / `ownvst3` native libs, `officecli.dll`, `AngleSharp.dll`, `HtmlToOpenXml.dll` |
| — untrusted / hash mismatch | 0 |
| The MSI itself | `NotSigned` |
| Defender custom scan of the **fully extracted** tree | "found no threats" |
| Custom actions | **none** — `InstallExecuteSequence` is entirely standard Windows Installer actions |
| Install location | `%ProgramFiles64%\Graphene Lab\AgentBridge` (`ALLUSERS=1`, per-machine) |
| Shortcuts | Start Menu + Desktop → `[INSTALLFOLDER]agent.exe`, both named "Graphene AgentBridge" |
| `Registry` table | **absent** — the Add/Remove Programs entry comes from Windows Installer's own registration (`DisplayName` = `ProductName`, `Publisher` = `Manufacturer`) |

Scanning the extracted tree is the stronger test than scanning the `.msi`: a whole-file scan does not
necessarily recurse as deep into the five embedded cabinets.

Nothing in the payload looked like a detection target, which supports the false-positive reading of
10.2.3 — but only Microsoft can name the file, hence the email.

Two real defects this audit surfaced (both fixed):

1. **`ProductVersion` collapsed every release within a month to one value.** `New-StoreInstaller.ps1`
   took the first three sections of the release version, so `1.26.09.06` and `1.26.09.11` both
   produced `ProductVersion 1.26.9` (read straight out of the shipped MSI). `MajorUpgrade` does not
   treat a same-version product as related, so installing a newer build over an older one did not
   upgrade — it left duplicate Add/Remove Programs entries and orphaned components. The date's month
   and day are now folded into the build field as `MM*100+DD` (`1.26.906`, `1.26.911`, …
   `1.26.1231`): unique, monotonically increasing, far below the 65535 build limit.
2. **`ProductLanguage = 0`.** The summary template is `x64;0`, not a valid LCID (normally
   `x64;1033`) because the build passes no `-culture`. `msiexec` tolerates it, but it leaves the
   package metadata ambiguous to anything reading the summary stream — set it explicitly before the
   next Store submission.

Naming note: the MSI's `Manufacturer` is **"Graphene Lab"** (space) while the Store publisher is
**`Graphene-Lab`** (hyphen). Windows Installer writes the ARP publisher from `Manufacturer`, so the
two spellings will not line up if the Store compares them. Pick one spelling and use it everywhere.

## 6. Signing: the open problem and the routes

No code-signing certificate exists (2026-09-11). Visual Studio can only create **self-signed test**
certificates, which do not satisfy 10.2.9.

| Route | Cost | Status / blocker |
|---|---|---|
| **SignPath Foundation** | free | **blocked by licensing, see below** |
| **OV/EV certificate from a CA** (DigiCert, Sectigo, SSL.com, Certum, GlobalSign) | ~one hundred €/year | works for individuals; hardware token or cloud HSM is mandatory; no open-source condition |
| **Azure Artifact Signing** (ex Trusted Signing) | cheapest | **excluded**: Public Trust for individuals only in the US/Canada; in Italy it needs a legal entity |
| MSIX route | free | Store re-signs (§4) — no certificate **and no open-source condition**, but see §8 for what packaging would require |

### Why SignPath is not available as things stand (checked 2026-09-11)

SignPath's criteria (signpath.org/terms) require an "OSI-approved open source license **for all
components**, without commercial dual-licensing" and "no proprietary code … especially code
published by the maintainer or an affiliated person/organization" (System Libraries excepted).
The signed MSI embeds every component below:

| Component in the payload | Repository / license | Meets the criteria? |
|---|---|---|
| AgentBridge | public, AGPL-3.0 | yes |
| Tool plugins (DocumentTool, SpreadsheetTool, OfficeTool, PresentationTool, OfficeSupportTool, PodcastTool) | public, AGPL-3.0 | yes |
| AIOffice.VoiceAgent (STT) | public, AGPL-3.0 | yes |
| AIOffice.VoiceAgent.Win (voice bridge) | public, AGPL-3.0 since 2026-09-11 (the file was missing) | yes |
| **AIOrchestrator** (the engine, inside `agent.exe`) | **private**, "Andrea Bruno License 1.4" | **no** |
| Third-party binary blobs (NVIDIA cuDNN/cuBLAS via `Microsoft.ML.OnnxRuntime.Gpu.Windows`, the Kokoro model, Playwright's node driver) | not ours | to disclose; System Libraries are allowed but a redistributable-GPU-runtime argument must be made explicitly |

"Andrea Bruno License 1.4" is source-available, personal use only, royalties for any other use —
not OSI-approved, i.e. exactly the commercial dual-licensing the criteria exclude. SignPath also
state they are not obliged to accept any project, and that every release needs manual approval by
an approver. The practical consequence: **SignPath requires either releasing the engine (and the
voice bridge) under an OSI license, or buying an OV certificate.** MSIX is the one free route that
does not care about licensing at all.

If SignPath is pursued anyway (the form is open to anyone), disclose the AIOrchestrator licence
first: the answer decides whether any of the wiring in §6 is worth doing.

### Ready-to-apply SignPath plan (not wired yet, deliberately)

The step is **not** in `release.yml` because SignPath's inputs (policy slug, artifact
configuration, project/org slugs) are account-specific and cannot be verified before approval —
writing guessed YAML into a production workflow would be worse than a documented plan.

Plan, once the account is approved (all of it inside the `store-msi` job, so the archives never
change):

1. New secret `SIGNPATH_API_TOKEN` (plus org/project/policy slugs); the step must **exit cleanly
   when the secret is absent** (the repo's established pattern: guard in the script, because the
   `secrets` context is not usable in `job/step if`).
2. Two signing requests, in this order: (a) the extracted payload must have its PE files signed
   **before** the MSI embeds them — the MSI is built from a payload whose files are signed;
   (b) the finished MSI is signed last.
3. Upload the signed MSI as the `store-msi` artifact instead of the local one.
4. Each release needs manual approval by a SignPath approver.

Open questions to send to `support@signpath.io`:

1. Hard limits on artifact size, file count per request and request duration? Our MSI is ~1.29 GB
   and embeds hundreds of PE files.
2. Is a free listing on the Microsoft Store compatible with the "no commercial dual-licensing"
   rule?
3. Recommended configuration for that MSI: one deep-signing request (`msi-file` → `pe-file`), or
   pre-signed payload + one MSI request?

## 7. Decision log (choices made, and what was rejected)

- **Payload trimming: rejected (reverted).** Removing the duplicated `kokoro.onnx` inside
  `voiceagent/` and the foreign-arch plugin natives saved ~103 MB (measured) / ~370 MB for the
  model, but it would have changed what the packaged app contains: the tree is a deliberate design
  (a duplicated model so the voice agent needs no first-use download; every RID's natives). There
  is **no size limit** forcing it (§4) and the download speed is fine (§3). If it is ever
  revisited, the change belongs in the `store-msi` job only — never in `build`.
- **CUDA EP out of the Store build: deferred.** It is embedded in the 731 MB single-file
  `agent.exe`, so removing it needs a dedicated publish (`-p:IncludeCudaEp=<...>` would have to be
  introduced), which breaks "the MSI is the archive tree" and drops GPU TTS acceleration for Store
  users. Lever available if the Store channel ever needs to shrink.
- **MSIX: evaluated, not adopted yet** — see §8. The free-signing advantage is real, but for
  AgentBridge it is not a packaging-only change.

## 8. MSIX evaluation (Store-only route, no app changes)

Why it is attractive: the Store signs for you (§4) and hosts/serves the package (no VPS proxy).

What was verified in the app (2026-09-11):

| Finding | Evidence |
|---|---|
| No admin/elevation requirement, all ports > 1024 | no `requireAdministrator`/role checks anywhere; HTTP on 5290, SIP UDP 5060 (off by default) |
| **User configuration lives INSIDE the install directory** → read-only in MSIX | `AppConfig.cs:21` `PersistentData` = `AppContext.BaseDirectory\PersistentData`; the same pattern in `WebClientUpdater.cs:31` and the TUI attachments |
| Self-update replaces files in the install directory | `AutoUpdate.cs` (download → extract → swap → restart) |
| A scheduled task is created with `RunLevel HighestAvailable` | `SystemExtra/Service.cs:355-380` via `cmd.exe /C schtasks` (fails silently today) |

Consequences:

- The package needs a **PSF `FileRedirectionFixup`** so the app can keep writing `PersistentData\`
  next to its executable. Microsoft documents PSF as the remedy for exactly this case, but Store
  eligibility of a PSF-bearing package is **not explicitly documented either way** → confirm with
  Partner Center before relying on it.
- Self-update is switched off by shipping `PersistentData\appsettings.json` with
  `AutoUpdate.Enabled=false` inside the package — no code change (`New-StoreMsix.ps1` does this).
- Elevation and the `cmd.exe`/S-mode guidance (§4) need a decision: the scheduled-task feature and
  the child processes (`node` for Playwright, the voice agents) are the risky parts.
- The current Partner Center product is type "EXE or MSI app"; an MSIX submission may need a **new
  product reservation** (new listing, new reviews).

**PSF is integrated in the tooling** (Store-only): `New-StoreMsix.ps1` fetches the pinned x64 PSF
binaries (`Microsoft.PackageSupportFramework` `1.0.240212.1`, MIT, cached in
`%LOCALAPPDATA%\AgentBridge\psf`), copies `PSFLauncher64.exe` + `PsfRuntime64.dll` +
`FileRedirectionFixup64.dll` into the layout, makes **PSFLauncher64.exe** the manifest entry point
and writes `config.json` (`tools/store/msix/config.json`) redirecting `PersistentData\`,
`attachments\`, `tui-screenshots\` and `GiraffeAIWebClient\` to the per-user VFS. `-SkipPsf` builds
the bare structure without it. The `store-msix` CI job (release.yml) does this on every release
from the same win-x64 archive and uploads `store-msix/*.msix` as a CI artifact (not on the GitHub
release: an unsigned MSIX cannot be sideloaded).

**Artifacts ready today** (Store-only, nothing installed):

```powershell
# Build + validate the package from a payload (stub or the real one; -Verify round-trips it)
powershell -File tools\store\New-StoreMsix.ps1 -PayloadDir <win-x64 payload> -Version 1.26.09.12 [-Verify]
# Real Store values (from Partner Center > View app identity details):
#   -IdentityName <Package/Identity Name>  -Publisher "<Publisher DN>"
# Bare structure without the file-redirection fixup (not usable as an app):
#   ... -SkipPsf
```

Verified 2026-09-11: on a stub payload, `makeappx pack` succeeds (it validates the manifest against
the schema), the round-trip unpack returns `AppxManifest.xml`, the layout carries the PSF trio +
`config.json`, the manifest entry point is `PSFLauncher64.exe`, and the seeded
`PersistentData\appsettings.json` has `AutoUpdate.Enabled=false`. A full-size run on the real
1.34 GB payload (`D:\ab-msi-payload`, v1.26.9.6) produced `GrapheneAgentBridge-1.26.09.06.msix`
(**906 MB**) with every piece in place — well inside the 25 GB MSIX limit.

**Still missing before this package could be submitted:** confirmation from Partner Center that a
PSF-bearing package is acceptable, real branding assets (`New-StoreMsix.ps1` generates flat
placeholders), the two identity secrets, a decision on the features listed above, and a local
install test — which needs a self-signed certificate whose subject equals the manifest Publisher
plus trusting it on the machine (admin, deliberate manual step).

## 8b. The last mile: what only the account owner can do

Everything up to the upload is automated. Partner Center itself cannot be automated for this
account: an individual developer account has no Entra tenant, so the Store Submission API
(`Submit-Store.ps1`) is unavailable, and the product identity values live behind the account login
(+ MFA). Remaining manual steps, in order:

1. **Decide the product type.** The existing product (id `a456c3f0-…`) is "EXE or MSI app". An MSIX
   submission of the same app likely needs a **new product reservation** in Partner Center
   (new name/listing; reviews start over). Confirm with Partner Center support before reserving.
2. **Copy the identity values** from Partner Center → *View app identity details*:
   `Package/Identity/Name` → secret `STORE_IDENTITY_NAME`, `Package/Identity/Publisher` → secret
   `STORE_PUBLISHER` (`gh secret set … --repo Graphene-Lab/AgentBridge`).
3. **Run a release** (`IsPrerelease=false`, tag `v1.yy.MM.dd`). The `store-msix` job then produces
   `GrapheneAgentBridge-<version>.msix` as a CI artifact, built with the real identity.
4. **Upload** that `.msix` in Partner Center → Packages → submit for certification. The Store
   re-signs it; no certificate and no licence conditions apply to this channel.
5. Optional but recommended before step 4: **install it locally** (self-signed certificate whose
   subject equals the Publisher) to see the PSF redirection working — this is also the quickest way
   to find anything in the app that assumes a writable install directory.

## 9. Pitfalls learned (do not repeat)

- **PowerShell 5.1 parses `.ps1` as ANSI**: a UTF-8 em dash inside a *string literal* becomes a
  typographic quote and ends the string → the script stops parsing. Keep literals **ASCII-only**;
  non-ASCII is only safe inside comments.
- `makeappx` in the current SDK has **no `validate` command** (it prints usage); `pack` already
  enforces the manifest schema. Use `unpack` for a round-trip check.
- `windows.appExecutionAlias` is neither a `uap:` nor a plain `desktop:` extension Category in
  this schema (it needs the `uap5` namespace). It was dropped from the test manifest; re-add only
  with a validation run.
- A `Set-Content -Encoding UTF8` in PS 5.1 adds a BOM; write UTF-8 without BOM with
  `[System.IO.File]::WriteAllText(path, text, (New-Object System.Text.UTF8Encoding($false)))`.
- The VPS proxy must answer `HEAD`, `Content-Length` and ranges — a close-delimited chunked body
  is not something the Store downloader can size or resume.
- **The MSI asset name does not always match the tag**: `v1.26.09.06` ships
  `GrapheneAgentBridge-1.26.9.6.msi` (unpadded) while `v1.26.09.11` ships the padded name, so the
  proxy tries both forms and uses the first that exists (found 2026-09-11 when
  `/agentbridge/msi/1.26.09.06` answered 404 and the download looked like a broken Store URL).
- **The tag form varies too, not just the asset name.** Building the tag verbatim from the requested
  version made `/msi/1.26.9.6` ask for the non-existent tag `v1.26.9.6` and 404 (2026-09-12). The
  proxy now crosses tag form × asset-name form. Two traps when doing this: pad **only** the month/day
  sections (padding major/minor turns `1.26.x` into the bogus `01.26.x`), and keep the `v` prefix on
  the tag only — an early cut leaked it into the asset name on the latest-release path
  (`GrapheneAgentBridge-v1.26.9.11.msi`), which 404s.
- **7-Zip yields only 8.3 short names out of an MSI's cabinets** (`F2`, `F117`), so it cannot be
  used to audit PE files or signatures. Use `msiexec /a <msi> /qn TARGETDIR=<dir>` instead.
- **`Get-ChildItem -Include` silently returns nothing without a wildcarded `-Path`.** Use
  `-Recurse -File` and filter on `.Extension` in `Where-Object`, or the audit reports zero files.
- **Case-insensitive path collisions in the payload**: the app already ships `assets\`, so a
  generated `Assets\` for the manifest resolves to the *same* directory on NTFS (the pack succeeds
  for the wrong reason and the app's asset tree gets polluted). Generated package assets therefore
  live in `StoreAssets\` — check for collisions whenever a generated package path could match a
  payload path.

## 10. Open items (resume here)

1. **Malware flag 10.2.3 — waiting on Microsoft.** The validation flagged the package but named
   neither a file nor a detection, and its UI exposes neither. The full payload audit in §5b found
   nothing: Defender reports no threats against the hash-verified MSI *and* against the fully
   extracted tree. Escalation is by email to `reportapp@microsoft.com` (sent by the account owner
   from `andrea_bruno@hotmail.com`), asking for the detection name and the offending file, and
   giving the package identity, the clean-Defender evidence and the open-source repository list.
   `WDSI` cannot take the file (500 MB limit vs an 855 MB payload) and `aka.ms/storedevsupport`
   refuses personal accounts, so email is the only route.
2. **Certificate** (§6): SignPath is blocked by the AIOrchestrator licence — the engine is the
   only non-OSI component in the payload (AIOffice.VoiceAgent.Win was licensed AGPL-3.0 on
   2026-09-11). Decide whether the engine may be released under an OSI licence; if yes, wire the two
   signing steps in `store-msi`; if no, MSIX is the free Store route (Store re-signs, §8) or buy an
   OV certificate (no licensing conditions). Until an installer is signed, a resubmission of the
   MSI/EXE product fails 10.2.9.
3. **`ProductLanguage = 0`** (§5b): pass a real culture to the WiX build so the summary template
   reads `x64;1033` instead of `x64;0`. Small, but it removes an ambiguity in the package metadata
   before the next submission.
4. **Publisher spelling** (§5b): `Manufacturer = "Graphene Lab"` in the MSI vs `Graphene-Lab` as the
   Store publisher. Align deliberately — one spelling everywhere.
5. **MSIX**: the tooling is complete (PSF included, `store-msix` job in CI). What remains is
   Partner Center-facing: confirm that a PSF-bearing package is accepted, reserve the MSIX product
   if needed, set `STORE_IDENTITY_NAME`/`STORE_PUBLISHER`, replace the placeholder artwork, and do
   the local install test — see §8b for the ordered click-list.
6. **Resubmission**: after the malware flag is cleared *and* a signed MSI exists, `IsPrerelease=false`
   release → tag `v1.yy.MM.dd` → MSI on the versioned URL → Partner Center resubmit (or
   `store-submit` with the `STORE_*` secrets).
7. Nice-to-have: `HEAD`/range smoke test in `install-agentbridge-mirror.sh` is already there;
   extend the release notes with the Store URL.
8. **Check the MSI channel with a standard (non-admin) user.** The MSI installs per-machine into
   `%ProgramFiles%\Graphene Lab\AgentBridge` and the app writes `PersistentData\` next to
   `agent.exe` (`AppConfig.cs:21`); a standard user may not be allowed to write there, so the
   configuration could fail to save. Reproduce with a plain user account (and note the same root
   cause blocks MSIX, where the package directory is read-only by design).
9. **Release asset naming is inconsistent** — `v1.26.09.06` attached the unpadded MSI name,
   `v1.26.09.11` the padded one. The proxy tolerates both now, but emitting one convention from CI
   would remove a whole class of confusion.

## 11. Useful commands

```bash
# Store URL smoke test (must be 200, no Location, with Content-Length)
curl -sI https://aitechnology.it/agentbridge/msi/1.26.09.11

# Deploy the proxy (root on the VPS; files are staged first over scp as agent@)
sudo bash /home/agent/store-proxy/install-agentbridge-mirror.sh

# MSI + MSIX locally
powershell -File tools\store\New-StoreInstaller.ps1 -PayloadDir <payload> -Version 1.26.09.12
powershell -File tools\store\New-StoreMsix.ps1      -PayloadDir <payload> -Version 1.26.09.12 -Verify

# Read an MSI without installing it (§5b): lays out real file names, registers nothing
msiexec /a GrapheneAgentBridge-1.26.9.6.msi /qn TARGETDIR=D:\ab-admin

# Per-file Authenticode status over an extracted payload (see the -Include trap in §9)
powershell -NoProfile -Command "$r='D:\ab-admin'; Get-ChildItem -LiteralPath $r -Recurse -File -Force |
  ? { $_.Extension -in '.exe','.dll','.sys','.ocx' } | % { $s=Get-AuthenticodeSignature $_.FullName;
      if ($s.Status -ne 'Valid') { '{0,-14} {1}' -f $s.Status, $_.FullName.Substring($r.Length+1) } }"

# Defender scan of the extracted tree (not just the .msi — cabinets may not be recursed)
"C:\ProgramData\Microsoft\Windows Defender\Platform\<version>\MpCmdRun.exe" `
  -Scan -ScanType 3 -File D:\ab-admin -DisableRemediation
```

CI artifacts of a release run: `store-msi` (the `.msi` for the EXE/MSI product, attached to the
GitHub release) and `store-msix` (the `.msix` for the MSIX product, CI-only).
