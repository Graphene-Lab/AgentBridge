# Microsoft Store publishing (tools/store)

**Full reference: `docs-dev/STORE-PUBLISHING.md`** — mechanisms, policy requirements, decision log,
open items and the MSIX evaluation. This file is the short operational version.

Makes **every AgentBridge release also update the Microsoft Store** version of
"Graphene AgentBridge" (win32 EXE/MSI product).

MSI/EXE Store products reference the installer by an **external package URL** —
Partner Center accepts no file upload and **rejects URLs that redirect** (GitHub
download URLs always 302 → rejected, 2026-09-07). The MSI is therefore served by
a streaming proxy on the AIOffice VPS at a stable non-redirecting URL.

## Architecture

```
release.yml
   ├─ build (win-x64 matrix)      → win-x64 payload archive
   ├─ store-msi  (windows)        → New-StoreInstaller.ps1 → GrapheneAgentBridge-<v>.msi
   │                                 (WiX v5, tools/store/dotnet-tools.json)
   ├─ release    (ubuntu)         → GitHub Release (5 archives + the MSI asset)
   └─ store-submit (ubuntu)       → ONLY when the STORE_* secrets exist: Submit-Store.ps1
                                     points the Partner Center draft package at the stable
                                     MSI URL + submits for certification. Without secrets the
                                     job exits cleanly (continue-on-error) and the Store update
                                     is done manually (~3 min, see section 4).

VPS proxy (tools/store/vps/): a python3 daemon (systemd, 127.0.0.1:8686) resolves
the Graphene-Lab/AgentBridge release (redirect-based, no GitHub API) and streams
its MSI through nginx at:

    https://aitechnology.it/agentbridge/msi/<version>  → the MSI of release tag v<version>
    https://aitechnology.it/agentbridge/msi            → the MSI of the LATEST release

Both answer HTTP 200 without redirects, with Content-Length, Accept-Ranges and
HEAD support; no file is stored on the VPS disk. Store submissions use the
VERSIONED url: policy 10.2.9 requires the binary behind the submitted URL to stay
frozen, while /agentbridge/msi follows every new release.
```

The MSI is built by the `store-msi` job and attached to the GitHub release by the
`release` job; `store-submit` then swaps the draft package URL to the proxy URL and
commits + submits — the proxy answers 200 with whatever the latest GitHub release
ships, which is the version just released (store-msi gates store-submit so the MSI is
on GitHub first).

## 1. Build the installer (store-msi, automatic)

```powershell
dotnet tool restore          # in tools/store (wix v5, pinned in dotnet-tools.json)
powershell -File tools\store\New-StoreInstaller.ps1 `
    -PayloadDir <win-x64 publish folder> -Version 1.26.09.08 -OutDir store-msi
```

Produces a per-machine MSI (Program Files\Graphene Lab\AgentBridge, Start-menu +
desktop shortcut, uninstall entry). In CI the payload is the published
`agentbridge-win-x64` archive.

## 2. Submit to the Store (store-submit, automatic)

`Submit-Store.ps1` drives the **Store Submission API** (`api.store.microsoft.com`,
`/submission/v1/product/{productId}/...`):

1. token (Entra ID client credentials, scope `https://api.store.microsoft.com/.default`)
2. `GET .../packages` → the current draft's MSI package
3. `PATCH .../packages/{packageId}` with the new `packageUrl`
4. `POST .../packages/commit`
5. wait until `GET .../status` reports the draft ready
6. `POST .../submit` (certification starts; it takes hours-days on Microsoft's side)

Manual run:

```powershell
powershell -File tools\store\Submit-Store.ps1 `
    -PackageUrl https://aitechnology.it/agentbridge/msi/1.26.09.12 -Version 1.26.09.12
# add -DryRun to only resolve config/token/draft and print the PATCH, changing nothing
```

### One-time account setup (human step, cannot be automated)

1. [Partner Center → Account settings → Users](https://partner.microsoft.com/dashboard/account/v3/usermanagement) →
   add an **Azure AD application** (or use portal.azure.com → App registrations):
   name `AgentBridge CI`, supported account type "Work/School", no redirect needed.
   In the Partner Center association keep **Manager** role.
2. In the app registration create a **client secret**; note **TenantId**, **ClientId**,
   **ClientSecret**.
3. Consent: the first token request must be granted (admin consent in the tenant).
4. **Seller ID** (`X-Seller-Account-Id`): Partner Center dashboard, Account settings.

### Config — GitHub Actions secrets

Never commit secrets. The `store-submit` job reads them from repo secrets:

| Secret | Value |
|---|---|
| `STORE_TENANT_ID` | Entra tenant id |
| `STORE_CLIENT_ID` | Entra app client id |
| `STORE_CLIENT_SECRET` | Entra app client secret |
| `STORE_PRODUCT_ID` | `a456c3f0-cd83-475b-a8b3-18a1172a1901` (Graphene AgentBridge) |
| `STORE_SELLER_ID` | Partner Center Seller ID |

For local runs the same names are read from the environment, or from
`tools/store/store-secrets.local.json` (gitignored): keys `tenantId`, `clientId`,
`clientSecret`, `productId`, `sellerId`.

## 3. The stable MSI URL — VPS streaming proxy (one-time install)

The URL the Store fetches must answer **200 without redirects** and point at a file
large enough (~1.8 GB) that GitHub Pages cannot host. The AIOffice VPS
(185.48.117.20, nginx, Let's Encrypt) runs a tiny python3 daemon that streams the
latest release's MSI from GitHub on demand — nothing is stored on the VPS disk.

Files to deploy (versioned under `tools/store/vps/`):

| File | Destination |
|---|---|
| `mirror-msi.py` | `/home/agent/store-proxy/mirror-msi.py` |
| `agentbridge-mirror.service` | `/etc/systemd/system/agentbridge-mirror.service` |
| `agentbridge-msi.nginx.conf` | `/etc/nginx/snippets/agentbridge-msi.conf` + `include` in the aitechnology.it `:443` server block |

Smoke test after install: `curl -sI https://aitechnology.it/agentbridge/msi/1.26.09.12`
must answer `200 OK`, `Content-Type: application/octet-stream` and a `Content-Length`
(the Store downloader needs the length and the ranges; a `HEAD` must answer 200 too).

## 4. Manual fallback (no Entra app — individual developer accounts)

Individual developers with a personal Microsoft account cannot create the Entra ID
app that the Store API/CLI requires (no tenant; the M365 Dev Program sandbox is not
granted to every account). Until a tenant is available the Store update is a ~3 minute
manual step per release — the MSI itself is always built and attached to the GitHub
release by CI, so nothing else is needed:

1. Take the immutable package URL of the release being submitted:

   ```
   https://aitechnology.it/agentbridge/msi/<version>      e.g. .../msi/1.26.09.12
   ```

   It answers 200 without redirects and is pinned to tag `v<version>`, so it stays valid
   and unchanged for the whole certification. If the VPS proxy is unavailable,
   `powershell -File tools\store\New-SignedMsiUrl.ps1 -ToClipboard -OpenPage` mints the
   GitHub CDN URL instead: it also answers 200 without redirects, but it expires after
   about 1 hour.

2. Partner Center → product **Graphene AgentBridge** → start a **new submission** →
   section **Packages** → edit the existing MSI package row → replace the **Package URL**
   with that URL (keep Architecture `x64`, languages, silent install).
3. Save → complete/submit the submission for certification.

The versioned form is also what the automatic `Submit-Store.ps1` path uses; the bare
`https://aitechnology.it/agentbridge/msi` exists only for quick manual downloads of the
newest release. If a tenant becomes available later (e.g. the Dev Program sandbox flips
to eligible), set the `STORE_*` secrets and the `store-submit` job takes over
automatically.

## Store certification notes

- **Signing is mandatory** (policy 10.2.9): the MSI and every PE file it ships must be
  signed by a certificate that chains to a CA in the Microsoft Trusted Root Program.
  `New-StoreInstaller.ps1` signs the payload before the files are put in the cabinet and
  the MSI after it is built when given `-SignPfx`/`-SignThumbprint` (or the `SIGN_*` env
  vars, fed in CI by the `SIGN_PFX_BASE64` / `SIGN_PFX_PASSWORD` / `SIGN_THUMBPRINT`
  repo secrets); without a certificate it warns and produces an UNSIGNED MSI, which
  fails certification.
- **The package URL must be versioned and immutable**: submit
  `https://aitechnology.it/agentbridge/msi/<version>`, never the bare `/msi`.
- **The download must be reliable**: the proxy answers `HEAD` and serves
  `Content-Length` plus `Accept-Ranges`. The 2026-09 submission failed policy 10.3.4
  ("the product failed to install through the Store") with a response that was chunked,
  had no length, ignored ranges and served a different release than the one submitted.
- The EXE/MSI product requires a real installer; the WiX MSI above is a standard
  per-machine MSI (silent install, uninstall registry) — acceptable to the App
  Certification Kit.
- The app is a console/TUI (Terminal.Gui). When launched from the Store Start-menu
  tile the console host opens normally for a packaged EXE/MSI desktop app, so the TUI
  renders; headless (`--headless`) stays supported.
- Each release is a new certification (Microsoft review); `store-submit` is
  `continue-on-error` in CI so a Store problem never blocks or fails the GitHub release.

## MSIX (the licence-free Store channel)

`tools/store/msix/AppxManifest.xml` + `tools/store/msix/config.json` + `tools/store/New-StoreMsix.ps1`
build a Store **MSIX** from the same win-x64 payload — the one Store channel where Microsoft signs
the package for you, so no certificate and no open-source condition apply. The script bundles the
Package Support Framework (pinned NuGet, MIT) and redirects the app's in-package writes
(`PersistentData\`, `attachments\`, `tui-screenshots\`, `GiraffeAIWebClient\`) to the per-user VFS,
because an MSIX package is read-only. The `store-msix` CI job builds it on every release (artifact
only: an unsigned MSIX cannot be sideloaded, so it is not attached to the release). What remains is
account-side — PSF acceptance, product reservation, the `STORE_IDENTITY_NAME`/`STORE_PUBLISHER`
secrets and the artwork: `docs-dev/STORE-PUBLISHING.md` §8 and §8b.
