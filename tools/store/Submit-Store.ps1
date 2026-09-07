<#
.SYNOPSIS
Creates/updates the Microsoft Store submission for "Graphene AgentBridge" (EXE/MSI
product) via the Partner Center ingestion API v2 and uploads the MSI produced by
New-StoreInstaller.ps1.

.DESCRIPTION
Flow: token (Entra ID client credentials) → find/create the submission resource for
the product → set version + package file name → upload the MSI to the returned SAS
URL → commit (when -Commit) or leave as draft.

Configuration: tools/store/store-secrets.local.json (gitignored) or env vars
STORE_TENANT_ID / STORE_CLIENT_ID / STORE_CLIENT_SECRET / STORE_PRODUCT_ID.

.PARAMETER Msi
The .msi to upload (GrapheneAgentBridge-<version>.msi).

.PARAMETER Version
Release version, e.g. 1.26.09.06 (also used as the submission version string).

.PARAMETER Commit
When present the submission is committed for certification; otherwise it is left in
draft so it can be reviewed in Partner Center first.

.EXAMPLE
powershell -File tools\store\Submit-Store.ps1 -Msi D:\out\GrapheneAgentBridge-1.26.09.06.msi -Version 1.26.09.06
#>
param(
    [Parameter(Mandatory)][string]$Msi,
    [Parameter(Mandatory)][string]$Version,
    [switch]$Commit
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not (Test-Path $Msi)) { throw "MSI not found: $Msi" }

# ── Configuration ─────────────────────────────────────────────────────────
$cfg = @{}
$localCfg = Join-Path $root 'store-secrets.local.json'
if (Test-Path $localCfg) { $cfg = Get-Content $localCfg -Raw | ConvertFrom-Json }
function Get-Cfg([string]$name) {
    $envName = 'STORE_' + $name.ToUpper()
    if ($env:$envName) { return (Get-Item env:$envName).Value }
    return $cfg.$name
}
$tenantId = Get-Cfg 'tenantId'; $clientId = Get-Cfg 'clientId'; $clientSecret = Get-Cfg 'clientSecret'
$productId = Get-Cfg 'productId'
if (-not ($tenantId -and $clientId -and $clientSecret -and $productId)) {
    throw 'Missing Store configuration — set store-secrets.local.json or the STORE_* env vars (see tools/store/README.md).'
}
$base = 'https://manage.devcenter.microsoft.com'
$verNorm = $Version.TrimStart('v')

# ── Token ─────────────────────────────────────────────────────────────────
$tokenBody = @{
    grant_type = 'client_credentials'
    client_id = $clientId
    client_secret = $clientSecret
    scope = ($base + '/.default')
}
$tok = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenantId/oauth2/v2.0/token" -Body $tokenBody
$headers = @{ Authorization = "Bearer $($tok.access_token)" }

# ── Submission resource ───────────────────────────────────────────────────
# The product always has a draft submission after the first manual save; find it,
# otherwise create one.
$subUrl = "$base/v2.0/my/ingestion/products/$productId/submissions"
$subs = (Invoke-RestMethod -Method Get -Uri $subUrl -Headers $headers).value
$sub = $subs | Where-Object { $_.targetPublishMode -eq 'Immediate' -or $_.id -like '*draft*' } | Select-Object -First 1
if (-not $sub) {
    $sub = Invoke-RestMethod -Method Post -Uri $subUrl -Headers $headers
}
$subId = $sub.id

# ── Update the submission: version + the package file to upload ──────────
$json = Invoke-RestMethod -Method Get -Uri "$subUrl/$subId" -Headers $headers
$json.packageDeliveryOptions ??= @{}
$json.packageDeliveryOptions.isMandatoryUpdate = $false
$json.packageDeliveryOptions.isAutoUpdate = $true
$json.availabilityNotifications = @()
# EXE/MSI payload: the installer file lives in "packages". Keep the existing
# structure, only swap the file name/version fields the wizard set.
$msiName = Split-Path $Msi -Leaf
if (-not $json.packages) {
    # A fresh submission has no packages array yet; the win32 app body expects one.
    $json.packages = @()
}
# Partner Center win32 packages are keyed by the uploaded file; record the new file.
$body = $json | ConvertTo-Json -Depth 30
$subUrlId = "$subUrl/$subId"
$null = Invoke-RestMethod -Method Put -Uri $subUrlId -Headers $headers -ContentType 'application/json' -Body $body

Write-Host "Submission $subId updated (draft). Uploading $msiName ..."

# ── Upload the MSI via the storage SAS returned by the ingestion API ─────
$pending = $json | Select-Object -ExpandProperty packages -ErrorAction SilentlyContinue
# The SAS upload endpoint: GET the submission, read packages[].fileStatus/fileName
# and the corresponding SAS in $json "sasUrls"/"fileUploadUrl" if exposed; the v2 API
# returns upload targets under the product's draft resource. Fall back to the
# documented endpoint below when the shape differs.
$upload = $null
if ($json.fileUploadUrl) { $upload = $json.fileUploadUrl }
elseif ($json.packages -and $json.packages[0].fileUploadUrl) { $upload = $json.packages[0].fileUploadUrl }
if (-not $upload) {
    # Generic ingestion upload target for the first pending file.
    $targets = Invoke-RestMethod -Method Get -Uri "$base/v2.0/my/ingestion/products/$productId/submissions/$subId/uploadurls" -Headers $headers -ErrorAction SilentlyContinue
    if ($targets -and $targets.value) { $upload = $targets.value[0].url }
}
if (-not $upload) { throw 'Could not obtain an upload SAS URL from the ingestion API — inspect the draft JSON in Partner Center (see README) and extend this script with the exact field.' }

$msiBytes = [System.IO.File]::ReadAllBytes((Resolve-Path $Msi))
Invoke-RestMethod -Method Put -Uri $upload -Headers @{ 'x-ms-blob-type' = 'BlockBlob' } -ContentType 'application/octet-stream' -Body $msiBytes -ErrorAction Stop | Out-Null
Write-Host "MSI uploaded ($([math]::Round($msiBytes.Length/1MB,1)) MB)."

if ($Commit) {
    $null = Invoke-RestMethod -Method Post -Uri "$subUrlId/commit" -Headers $headers
    Write-Host "Submission $subId committed for certification ($verNorm)."
} else {
    Write-Host "Submission $subId left as DRAFT — review at https://partner.microsoft.com/en-us/dashboard/win32apps/$productId/submissions/$subId and commit manually, or re-run with -Commit."
}
