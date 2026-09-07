# Microsoft Store publishing (tools/store)

Makes **every AgentBridge release also update the Microsoft Store** version.

Pipeline:

```
release.yml (win-x64 publish)                     # existing
   └─ New-StoreInstaller.ps1  → GrapheneAgentBridge-<v>.msi
        └─ Submit-Store.ps1   → Partner Center submission (draft → commit)
```

## What is already done (2026-09-07)

- Product created in Partner Center: **"Graphene AgentBridge"** (EXE or MSI app,
  ProductId `a456c3f0-cd83-475b-a8b3-18a1172a1901`), Free, discoverable, 240 markets.
- Name reserved; Availability + partial Properties in the draft submission.

## 1. Build the installer

```powershell
# From the AgentBridge repo root
dotnet tool restore          # in tools/store (wix v5, pinned in dotnet-tools.json)
powershell -File tools\store\New-StoreInstaller.ps1 `
    -PayloadDir <win-x64 publish folder> -Version 1.26.09.06 -OutDir store-msi
```

Produces a per-machine MSI (Program Files\Graphene Lab\AgentBridge, Start-menu +
desktop shortcut, uninstall entry). Verified end-to-end locally (silent install +
uninstall) on a staged copy of the real payload.

## 2. Submit to the Store (Submit-Store.ps1)

Uses the Partner Center **ingestion API v2** (Microsoft Entra ID client-credentials).

### One-time account setup (human step, cannot be automated)

1. [Partner Center → Account settings → Users](https://partner.microsoft.com/dashboard/account/v3/usermanagement) →
   add an **Azure AD application** (or use portal.azure.com → App registrations):
   name `AgentBridge CI`, supported account type "Work/School", no redirect needed.
   In the Partner Center association keep **Manager** role.
2. In the app registration create a **client secret**; note **TenantId**, **ClientId**,
   **ClientSecret**.
3. Consent: the first token request must be granted (admin consent in the tenant).

### Config

Never commit secrets. Create `tools/store/store-secrets.local.json` (gitignored):

```json
{
  "tenantId": "…",
  "clientId": "…",
  "clientSecret": "…",
  "productId": "a456c3f0-cd83-475b-a8b3-18a1172a1901"
}
```

Or set env vars `STORE_TENANT_ID`, `STORE_CLIENT_ID`, `STORE_CLIENT_SECRET`, `STORE_PRODUCT_ID`.

### Run

```powershell
powershell -File tools\store\Submit-Store.ps1 -Msi <path.msi> -Version 1.26.09.06
# optional: -Commit   (submit for certification; default = draft only)
```

## 3. CI wiring (release.yml)

Add a Windows job on the release (gate-off) run:

```yaml
store-msi:
  needs: build-windows   # job that published win-x64 into ./publish
  runs-on: windows-latest
  if: needs.check-version.outputs.do_release == 'true'
  steps:
    - uses: actions/checkout@v4
    - run: dotnet tool restore --tool-manifest tools/store/dotnet-tools.json
    - run: powershell -File tools/store/New-StoreInstaller.ps1 -PayloadDir publish -Version $VERSION -OutDir store-msi
    - run: powershell -File tools/store/Submit-Store.ps1 -Msi (Get-ChildItem store-msi\*.msi).FullName -Version $VERSION -Commit
      env: { STORE_TENANT_ID: ${{ secrets.STORE_TENANT_ID }}, STORE_CLIENT_ID: …, STORE_CLIENT_SECRET: …, STORE_PRODUCT_ID: … }
```

## Store certification notes

- The EXE/MSI product requires a real installer; the WiX MSI above is a standard
  per-machine MSI (silent install, uninstall registry) — acceptable to the App
  Certification Kit.
- The app is a console/TUI (Terminal.Gui). When launched from the Store Start-menu
  tile the console host opens normally for a packaged EXE/MSI desktop app, so the TUI
  renders; headless (`--headless`) stays supported. Verify once with the certifier in
  mind: first run must show the UI or the server note, never a crash.
