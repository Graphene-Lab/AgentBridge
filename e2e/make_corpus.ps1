# Creates the e2e corpus for run_e2e.ps1: documents (md/csv/txt), a DOCX, a PDF,
# a PNG (image without OCR key -> graceful skip), an unsupported .dat and a broken junction.
$ErrorActionPreference = 'Stop'
$root = 'C:\Users\andre\AppData\Local\Temp\aioffice_e2e_corpus'
if (Test-Path $root) { Remove-Item $root -Recurse -Force }
New-Item -ItemType Directory -Path "$root\documenti" | Out-Null
New-Item -ItemType Directory -Path "$root\immagini" | Out-Null

[System.IO.File]::WriteAllText("$root\documenti\rapporto.txt", "Rapporto interno del progetto. La capitale dell'Italia e Roma, circa 2,8 milioni di abitanti. L'Europa conta molti stati membri.", [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText("$root\documenti\note.md", "# Note di riunione`n`n- punto uno`n- punto due`n`nLa riunione si e conclusa alle 18.", [System.Text.Encoding]::UTF8)
[System.IO.File]::WriteAllText("$root\documenti\dati.csv", "Citta,Paese,Abitanti`nRoma,Italia,2800000`nParigi,Francia,2100000`nBerlino,Germania,3600000", [System.Text.Encoding]::UTF8)

# 1x1 PNG (image without OCR key -> graceful skip)
[System.IO.File]::WriteAllBytes("$root\immagini\foto.png", [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="))

# unsupported format
[System.IO.File]::WriteAllBytes("$root\documenti\codice.dat", [byte[]](0x00,0x01,0x02,0x03))

# minimal DOCX (zip with content types + document.xml)
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$docx = "$root\documenti\relazione.docx"
$zip = [System.IO.Compression.ZipFile]::Open($docx, 'Create')
try {
    $ct = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>'
    $rels = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>'
    $doc = '<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Relazione di prova: il documento descrive il progetto AIOffice.</w:t></w:r></w:p></w:body></w:document>'
    foreach ($e in @(@('[Content_Types].xml',$ct), @('_rels/.rels',$rels), @('word/document.xml',$doc))) {
        $entry = $zip.CreateEntry($e[0])
        $sw = New-Object System.IO.StreamWriter($entry.Open())
        $sw.Write($e[1]); $sw.Close()
    }
} finally { $zip.Dispose() }

# minimal PDF (ASCII-safe text, computed xref offsets)
$pdf = "$root\documenti\manuale.pdf"
$txt = 'Manuale utente AIOffice versione 1.0'
$streamContent = 'BT /F1 24 Tf 72 720 Td (' + $txt + ') Tj ET'
$objs = @(
  '<< /Type /Catalog /Pages 2 0 R >>',
  '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
  '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>',
  '<< /Length ' + $streamContent.Length + ' >> stream' + [char]10 + $streamContent + [char]10 + 'endstream',
  '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'
)
$sb = New-Object System.Text.StringBuilder
[void]$sb.Append('%PDF-1.4' + [char]10)
$offsets = @()
for ($i = 0; $i -lt $objs.Count; $i++) {
    $offsets += $sb.Length
    [void]$sb.Append(([string]($i+1)) + ' 0 obj' + [char]10 + $objs[$i] + [char]10 + 'endobj' + [char]10)
}
$xrefPos = $sb.Length
[void]$sb.Append('xref' + [char]10 + '0 ' + ($objs.Count+1) + [char]10 + '0000000000 65535 f ' + [char]10)
foreach ($o in $offsets) { [void]$sb.Append($o.ToString('0000000000') + ' 00000 n ' + [char]10) }
[void]$sb.Append('trailer' + [char]10 + '<< /Size ' + ($objs.Count+1) + ' /Root 1 0 R >>' + [char]10 + 'startxref' + [char]10 + $xrefPos + [char]10 + '%%EOF')
[System.IO.File]::WriteAllText($pdf, $sb.ToString(), [System.Text.Encoding]::ASCII)

# broken junction (inaccessible dir -> enumeration must skip it)
cmd /c mklink /J "$root\broken" "C:\nonexistent\e2e-target" | Out-Null

Write-Host 'CORPUS READY'
Get-ChildItem $root -Recurse -File | ForEach-Object { Write-Host ('  ' + $_.FullName.Substring($root.Length+1) + '  (' + $_.Length + ' B)') }
