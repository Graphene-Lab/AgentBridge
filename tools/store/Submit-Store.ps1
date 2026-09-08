<#
.SYNOPSIS
Points the Microsoft Store draft package of "Graphene AgentBridge" (EXE/MSI product)
at a new installer URL and submits it for certification.

.DESCRIPTION
MSI/EXE Store products reference the installer by an EXTERNAL package URL (there is no
file upload): Partner Center requires the URL to answer HTTP 200 without redirects, so the
MSI is served by the streaming proxy on the AIOffice VPS
(https://aitechnology.it/agentbridge/msi — see tools/store/vps/). This script updates the
current draft's package URL, commits the packages module and creates the submission, all
through the Store Submission API:

    https://api.store.microsoft.com/submission/v1/product/{productId}/...

Auth is an Entra ID (Azure AD) app associated with the Partner Center account in the
Manager role. Config: tools/store/store-secrets.local.json (gitignored) or the env vars
STORE_TENANT_ID / STORE_CLIENT_ID / STORE_CLIENT_SECRET / STORE_PRODUCT_ID /
STORE_SELLER_ID (Seller ID: Partner Center → Account settings, shown on the dashboard).

.PARAMETER PackageUrl
Stable non-redirecting URL of the MSI for this release (e.g. https://aitechnology.it/agentbridge/msi).

.PARAMETER Version
Release version, e.g. 1.26.09.08 (used only for logging).

.PARAMETER DryRun
Resolve config + token + current draft packages and print the PATCH that would be sent,
without changing anything.

.EXAMPLE
powershell -File tools\store\Submit-Store.ps1 -PackageUrl https://aitechnology.it/agentbridge/msi -Version 1.26.09.08
#>
param(
    [Parameter(Mandatory)][string]$PackageUrl,
    [Parameter(Mandatory)][string]$Version,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

# ── Configuration ─────────────────────────────────────────────────────────
$cfg = @{}
$localCfg = Join-Path $root 'store-secrets.local.json'
if (Test-Path $localCfg) { $cfg = Get-Content $localCfg -Raw | ConvertFrom-Json }
function Get-Cfg([string]$name) {
    $envName = 'STORE_' + $name.ToUpper()
    if (Get-Item env:$envName -ErrorAction SilentlyContinue) { return (Get-Item env:$envName).Value }
    return $cfg.$name
}
$tenantId = Get-Cfg 'tenantId'; $clientId = Get-Cfg 'clientId'; $clientSecret = Get-Cfg 'clientSecret'
$productId = Get-Cfg 'productId'; $sellerId = Get-Cfg 'sellerId'
if (-not ($tenantId -and $clientId -and $clientSecret -and $productId -and $sellerId)) {
    throw 'Missing Store configuration — set store-secrets.local.json or the STORE_* env vars incl. STORE_SELLER_ID (see tools/store/README.md).'
}
$base = 'https://api.store.microsoft.com'
$ver = $Version.TrimStart('v')

# ── Token (Entra ID client credentials) ───────────────────────────────────
$tokenBody = @{
    grant_type    = 'client_credentials'
    client_id     = $clientId
    client_secret = $clientSecret
    scope         = ($base + '/.default')
}
$tok = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token" -Body $tokenBody
$headers = @{
    Authorization         = "Bearer $($tok.access_token)"
    'X-Seller-Account-Id' = $sellerId
}
$sub = "submission/v1/product/$productId"

# ── Current draft ─────────────────────────────────────────────────────────
$status = (Invoke-RestMethod -Method Get -Uri "$base/$sub/status" -Headers $headers)
Write-Host "Draft status: $($status | ConvertTo-Json -Depth 8 -Compress)"

$packs = (Invoke-RestMethod -Method Get -Uri "$base/$sub/packages" -Headers $headers).responseData.packages
$pkg = $packs | Where-Object { $_.packageType -eq 'msi' } | Select-Object -First 1
if (-not $pkg) { $pkg = $packs | Select-Object -First 1 }
if (-not $pkg) { throw 'No package found in the current draft — create one in Partner Center first (Packages page).' }
Write-Host "Current package: $($pkg | ConvertTo-Json -Depth 8 -Compress)"

# ── Build the PATCH body (writable fields only, URL swapped) ─────────────
$body = [ordered]@{
    packageUrl          = $PackageUrl
    languages           = @($pkg.languages)
    architectures       = @($pkg.architectures)
    isSilentInstall     = [bool]$pkg.isSilentInstall
    packageType         = $pkg.packageType
}
if (-not $body.isSilentInstall -and $pkg.installerParameters) { $body.installerParameters = $pkg.installerParameters }
if ($pkg.genericDocUrl) { $body.genericDocUrl = $pkg.genericDocUrl }
$bodyJson = $body | ConvertTo-Json -Depth 10
Write-Host "PATCH /packages/$($pkg.packageId): $bodyJson"
if ($DryRun) {
    Write-Host 'DryRun: no change was made. Re-run without -DryRun to update and submit.'
    exit 0
}

$null = Invoke-RestMethod -Method Patch -Uri "$base/$sub/packages/$($pkg.packageId)" -Headers $headers -ContentType 'application/json' -Body $bodyJson
Write-Host "Package URL updated to $PackageUrl (v$ver)."

# ── Commit packages ───────────────────────────────────────────────────────
$commit = Invoke-RestMethod -Method Post -Uri "$base/$sub/packages/commit" -Headers $headers
Write-Host "Commit: $($commit | ConvertTo-Json -Depth 8 -Compress)"

# ── Wait until the draft is ready for submission (bounded) ───────────────
for ($i = 1; $i -le 30; $i++) {
    Start-Sleep -Seconds 10
    $s = (Invoke-RestMethod -Method Get -Uri "$base/$sub/status" -Headers $headers)
    if ($s.responseData.isReady) { Write-Host "Draft ready after $($i*10)s."; break }
    Write-Host "waiting for draft readiness ($($i*10)s)..."
    if ($i -eq 30) { throw 'Draft did not become ready within 5 minutes — check the Partner Center dashboard.' }
}

# ── Create the submission (certification) ────────────────────────────────
$submit = Invoke-RestMethod -Method Post -Uri "$base/$sub/submit" -Headers $headers
$submitJson = $submit | ConvertTo-Json -Depth 8
Write-Host "Submit response: $submitJson"

# The submission continues on Microsoft's side (certification takes from hours to
# days). If the API exposes a submission id, print the initial status for the log.
$subId = $null
try { $subId = $submit.responseData.ongoingSubmissionId } catch {}
if (-not $subId) {
    try { $s2 = Invoke-RestMethod -Method Get -Uri "$base/$sub/status" -Headers $headers; $subId = $s2.responseData.ongoingSubmissionId } catch {}
}
if ($subId) {
    Start-Sleep -Seconds 20
    $ps = Invoke-RestMethod -Method Get -Uri "$base/$sub/submission/$subId/status" -Headers $headers
    Write-Host "Submission $subId initial status: $($ps | ConvertTo-Json -Depth 8 -Compress)"
} else {
    Write-Host 'Submission created. Certification continues on the Partner Center side — monitor https://partner.microsoft.com.'
}
Write-Host "Done: AgentBridge v$ver submitted to the Microsoft Store (package URL $PackageUrl)."
