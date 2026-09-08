<#
.SYNOPSIS
Mints the non-redirecting (signed CDN) download URL of an AgentBridge MSI release
asset, for the manual Microsoft Store update.

.DESCRIPTION
Microsoft Partner Center rejects package URLs that redirect. GitHub download URLs
always 302 to a signed release-assets.githubusercontent.com URL that answers HTTP 200
directly but expires after ~1 hour. This helper resolves that final URL (HEAD follows
the redirect chain, nothing is downloaded), copies it to the clipboard and optionally
opens the Partner Center product page in the default browser, ready to paste.
See tools/store/README.md "Manual fallback (no Entra app)".

.PARAMETER Tag
Release tag to target, e.g. 1.26.09.08 or v1.26.09.08. Default: the LATEST release.

.PARAMETER ToClipboard
Copy the signed URL to the clipboard (default when no other action is given).

.PARAMETER OpenPage
Open the product page in Partner Center in the default browser after copying
(url override with -ProductPage).

.PARAMETER ProductPage
Partner Center page to open (default: the win32apps product where the package row
is edited). Override for a specific submission URL.

.EXAMPLE
powershell -File tools\store\New-SignedMsiUrl.ps1 -ToClipboard -OpenPage
#>
param(
    [string]$Tag = '',
    [switch]$ToClipboard,
    [switch]$OpenPage,
    [string]$ProductPage = 'https://partner.microsoft.com/dashboard/win32apps/a456c3f0-cd83-475b-a8b3-18a1172a1901'
)

$repo = 'Graphene-Lab/AgentBridge'
if (-not $Tag) {
    # Latest tag via the /releases/latest redirect Location (no GitHub API, no rate limit).
    $loc = curl.exe -s -o NUL -w '%{redirect_url}' "https://github.com/$repo/releases/latest"
    $Tag = ($loc -split '/')[-1]
}
$Tag = $Tag.Trim()
if ($Tag -notmatch '^v') { $Tag = 'v' + $Tag }
$name = 'GrapheneAgentBridge-' + $Tag.TrimStart('v') + '.msi'
$url = "https://github.com/$repo/releases/download/$Tag/$name"

# Follow the 302 chain with HEAD only: url_effective = the signed CDN URL (200, no
# redirect, ~1h validity). An existing MSI asset is required (CI ships it on release).
$final = curl.exe -s -I -L -o NUL -w '%{url_effective}' --max-time 90 $url
if (-not $final -or $final -notmatch '^https?://') {
    throw "could not resolve a signed URL for $url — does the release have the MSI asset?"
}
Write-Host "Release:  $Tag"
Write-Host "Asset:    $name"
Write-Host "Signed URL (no redirects, valid ~1h):"
Write-Host $final

if ($ToClipboard -or $OpenPage) {
    Set-Clipboard $final
    Write-Host '-> copied to clipboard.'
}
if ($OpenPage) {
    Start-Process $ProductPage
    Write-Host "-> opened $ProductPage in the default browser."
}
