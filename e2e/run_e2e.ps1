param(
    [string]$BaseUrl = 'http://127.0.0.1:5290',
    [string]$Corpus = 'C:\Users\andre\AppData\Local\Temp\aioffice_e2e_corpus',
    [string]$Work = 'C:\Users\andre\AppData\Local\Temp\aioffice_e2e_work',
    [string]$LogsDir = 'C:\Users\andre\OneDrive\Sorgenti\AgentBridge\bin\Debug\net10.0\logs',
    [string]$Bridge = 'http://127.0.0.1:8787'
)
$ErrorActionPreference = 'Continue'
$script:pass = 0; $script:fail = 0
$out = Join-Path $Work 'out'; $bodies = Join-Path $Work 'bodies'
New-Item -ItemType Directory -Force -Path $out | Out-Null
New-Item -ItemType Directory -Force -Path $bodies | Out-Null

function T([string]$name, [bool]$cond, [string]$detail = '') {
    if ($cond) { $script:pass++; Write-Host "  OK   $name" }
    else { $script:fail++; Write-Host "  FAIL $name   $detail" }
}

function HttpReq([string]$method, [string]$url, [string]$bodyFile, [string]$outFile, [int]$timeout = 120, [string[]]$extraArgs = @()) {
    $a = @('-s', '-m', "$timeout", '-X', $method, $url, '-o', $outFile, '-w', '%{http_code}')
    if ($bodyFile -ne '') { $a += @('-H', 'Content-Type: application/json', '--data-binary', "@$bodyFile") }
    if ($extraArgs.Count -gt 0) { $a += $extraArgs }
    return curl.exe @a
}

function CurlUpload([string]$url, [string]$file, [string]$outFile) {
    return curl.exe -s -m 90 -X POST $url -F "file=@$file" -F 'purpose=assistants' -o $outFile -w '%{http_code}'
}

function NewBody([string]$name, [string]$json) {
    $p = Join-Path $bodies "$name.json"
    [System.IO.File]::WriteAllText($p, $json, (New-Object System.Text.UTF8Encoding($false)))
    return $p
}

function ReadJson([string]$file) {
    $raw = Get-Content $file -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    return $raw | ConvertFrom-Json
}

function LatestLog() {
    return (Get-ChildItem $LogsDir -Filter '*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
}

function MaxPromptLen() {
    $log = LatestLog
    if ($null -eq $log) { return 0 }
    $content = Get-Content $log.FullName -Raw
    $m = [regex]::Matches($content, 'prompt_len=(\d+)')
    $mx = 0
    foreach ($x in $m) { $v = [int]$x.Groups[1].Value; if ($v -gt $mx) { $mx = $v } }
    return $mx
}

# ── pre-flight: bridge must return real content, not empty deltas ──
# NB: PowerShell 5.1 strips double quotes when passing args to native apps, so
# JSON bodies are always written to files and sent with --data-binary @file.
Write-Host '=== Pre-flight: DeepSeekBridge probe ==='
$probeBody = Join-Path $bodies 'probe.json'
[System.IO.File]::WriteAllText($probeBody, '{"model":"deepseek-web/deepseek-chat","messages":[{"role":"user","content":"Say banana"}]}', (New-Object System.Text.UTF8Encoding($false)))
$probe = curl.exe -s -m 30 -X POST "$Bridge/v1/chat/completions" -H 'Content-Type: application/json' --data-binary "@$probeBody"
$probeOk = $false
if ($probe) { $probeOk = (($probe -join "`n") -match '"content":"[^"]+') }
if (-not $probeOk) {
    Write-Host "BRIDGE NOT RESPONSIVE: $probe"
    exit 2
}
Write-Host '  OK   bridge responds with real tokens'

Write-Host ''
Write-Host '=== C1: chat (all agent sets, stream, auto-search) ==='

# T01 default-agent, non-stream, trivial (lowercase → no auto-search)
$b = NewBody 't01' "{""model"":""default-agent"",""messages"":[{""role"":""user"",""content"":""rispondi in una sola frase: quanto fa 7 per 8?""}],""max_tokens"":200}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't01.json')
$j = ReadJson (Join-Path $out 't01.json')
$c1 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T01 default-agent non-stream' ($code -eq '200' -and $j -and $j.choices[0].finish_reason -eq 'stop' -and $c1 -ne '' -and $c1 -notmatch 'Max iterations')

# T02 default-agent with NameOrKey (auto-search path — was 500 before fix)
$b = NewBody 't02' "{""model"":""default-agent"",""messages"":[{""role"":""user"",""content"":""Rispondi in una sola frase: qual è la capitale dell'Italia?""}],""max_tokens"":200}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't02.json')
$j = ReadJson (Join-Path $out 't02.json')
$c2 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T02 auto-search capitalized → no 500' ($code -eq '200' -and $c2 -ne '' -and $c2 -notmatch 'Max iterations')

# T03 streaming SSE
$b = NewBody 't03' "{""model"":""default-agent"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: 2+2?""}],""stream"":true,""max_tokens"":200}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't03.txt') 120 @('-N')
$stream = Get-Content (Join-Path $out 't03.txt') -Raw
T 'T03 streaming SSE' ($code -eq '200' -and $stream.StartsWith('data:') -and $stream.Contains('[DONE]') -and $stream.Contains('"finish_reason":"stop"'))

# T04 word-agent (guard ACTIVE + bounded)
$b = NewBody 't04' "{""model"":""word-agent"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: quanto fa 3+3?""}],""max_tokens"":300}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't04.json')
$j = ReadJson (Join-Path $out 't04.json')
$c4 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T04 word-agent (guard bounded)' ($code -eq '200' -and $c4 -ne '')

# T05 spreadsheet-agent (guard ACTIVE)
$b = NewBody 't05' "{""model"":""spreadsheet-agent"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: qual è la capitale della Spagna?""}],""max_tokens"":300}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't05.json')
$j = ReadJson (Join-Path $out 't05.json')
$c5 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T05 spreadsheet-agent' ($code -eq '200' -and $c5 -ne '')

# T06 search-agent (index path)
$b = NewBody 't06' "{""model"":""search-agent"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: qual è la capitale dell'Italia secondo i documenti?""}],""max_tokens"":200}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't06.json')
$j = ReadJson (Join-Path $out 't06.json')
$c6 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T06 search-agent' ($code -eq '200' -and $c6 -ne '')

# T07 multi-agent
$b = NewBody 't07' "{""model"":""multi-agent"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: qual è la capitale della Francia?""}],""max_tokens"":300}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't07.json')
$j = ReadJson (Join-Path $out 't07.json')
$c7 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T07 multi-agent' ($code -eq '200' -and $c7 -ne '')

# T08 unknown model → default fallback
$b = NewBody 't08' "{""model"":""unknown-xyz"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: 1+1?""}],""max_tokens"":200}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't08.json')
$j = ReadJson (Join-Path $out 't08.json')
$c8 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T08 unknown model fallback' ($code -eq '200' -and $c8 -ne '')

Write-Host ''
Write-Host '=== C2: request validation / error paths ==='

# T09 no user message → 400
$b = NewBody 't09' "{""model"":""default-agent"",""messages"":[{""role"":""system"",""content"":""ciao""}]}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't09.json') 30
T 'T09 no user message → 400' ($code -eq '400')

# T10 invalid JSON → 400
$b = NewBody 't10' '{"model":'
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't10.json') 30
T 'T10 invalid JSON → 400' ($code -eq '400')

# T11 empty messages → 400
$b = NewBody 't11' '{""model"":""default-agent"",""messages"":[]}'
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't11.json') 30
T 'T11 empty messages → 400' ($code -eq '400')

Write-Host ''
Write-Host '=== C3: file uploads (multipart, server-side conversion) ==='

# T12-T18 upload matrix
$code = CurlUpload "$BaseUrl/v1/files" (Join-Path $Corpus 'documenti\dati.csv') (Join-Path $out 'u_csv.json')
$j = ReadJson (Join-Path $out 'u_csv.json')
T 'T12 csv → processed' ($code -eq '200' -and $j.status -eq 'processed')
$script:csvId = if ($j) { $j.id } else { '' }

$code = CurlUpload "$BaseUrl/v1/files" (Join-Path $Corpus 'documenti\note.md') (Join-Path $out 'u_md.json')
$j = ReadJson (Join-Path $out 'u_md.json')
T 'T13 md → processed' ($code -eq '200' -and $j.status -eq 'processed')
$script:mdId = if ($j) { $j.id } else { '' }

$code = CurlUpload "$BaseUrl/v1/files" (Join-Path $Corpus 'documenti\rapporto.txt') (Join-Path $out 'u_txt.json')
$j = ReadJson (Join-Path $out 'u_txt.json')
T 'T14 txt → processed' ($code -eq '200' -and $j.status -eq 'processed')
$script:txtId = if ($j) { $j.id } else { '' }

$code = CurlUpload "$BaseUrl/v1/files" (Join-Path $Corpus 'documenti\relazione.docx') (Join-Path $out 'u_docx.json')
$j = ReadJson (Join-Path $out 'u_docx.json')
$st = if ($j) { $j.status } else { $code }
T 'T15 docx → processed' ($code -eq '200' -and $j.status -eq 'processed') "status=$st"
$script:docxId = if ($j) { $j.id } else { '' }

$code = CurlUpload "$BaseUrl/v1/files" (Join-Path $Corpus 'documenti\manuale.pdf') (Join-Path $out 'u_pdf.json')
$j = ReadJson (Join-Path $out 'u_pdf.json')
$st = if ($j) { $j.status } else { $code }
T 'T16 pdf → processed with real content' ($code -eq '200' -and $j.status -eq 'processed' -and -not [string]::IsNullOrWhiteSpace($j.extracted_content)) "status=$st content='$($j.extracted_content)'"

# T16b minimal PDF that parses but yields empty text → must NOT be reported as processed
$code = CurlUpload "$BaseUrl/v1/files" (Join-Path $Work 'min_pdf.pdf') (Join-Path $out 'u_minpdf.json')
$j = ReadJson (Join-Path $out 'u_minpdf.json')
T 'T16b empty-extraction PDF → unsupported' ($code -eq '200' -and $j.status -eq 'unsupported')

$code = CurlUpload "$BaseUrl/v1/files" (Join-Path $Corpus 'immagini\foto.png') (Join-Path $out 'u_png.json')
$j = ReadJson (Join-Path $out 'u_png.json')
T 'T17 png (no OCR key) → unsupported, no 500' ($code -eq '200' -and $j.status -eq 'unsupported')

$code = CurlUpload "$BaseUrl/v1/files" (Join-Path $Corpus 'documenti\codice.dat') (Join-Path $out 'u_dat.json')
$j = ReadJson (Join-Path $out 'u_dat.json')
T 'T18 unsupported format → unsupported' ($code -eq '200' -and $j.status -eq 'unsupported')
$script:datId = if ($j) { $j.id } else { '' }

# T19 empty file → 400
$empty = Join-Path $Work 'vuoto.txt'
[System.IO.File]::WriteAllText($empty, '')
$code = CurlUpload "$BaseUrl/v1/files" $empty (Join-Path $out 'u_empty.json')
T 'T19 empty upload → 400' ($code -eq '400')

# T20 list files
$code = HttpReq 'GET' "$BaseUrl/v1/files" '' (Join-Path $out 'list.json') 30
$j = ReadJson (Join-Path $out 'list.json')
$hasCsv = $false
if ($j -and $j.data) { $hasCsv = @($j.data | Where-Object { $_.id -eq $script:csvId }).Count -gt 0 }
T 'T20 GET /v1/files lists uploads' ($code -eq '200' -and $hasCsv)

# T21 get single file with content
$code = HttpReq 'GET' "$BaseUrl/v1/files/$script:csvId" '' (Join-Path $out 'get.json') 30
$j = ReadJson (Join-Path $out 'get.json')
T 'T21 GET /v1/files/{id} returns markdown' ($code -eq '200' -and $j.status -eq 'processed' -and $j.extracted_content -match '\|')

# T22 unknown id → 404
$code = HttpReq 'GET' "$BaseUrl/v1/files/file-unknown" '' (Join-Path $out 'get404.json') 30
T 'T22 GET unknown file → 404' ($code -eq '404')

Write-Host ''
Write-Host '=== C4: chat with attachments ==='

# T23 file_ids chat: context must be injected (check max prompt_len in server log)
$b = NewBody 't23' "{""model"":""default-agent"",""messages"":[{""role"":""user"",""content"":""in base al file allegato, quale città ha più abitanti e quanti sono?""}],""file_ids"":[""$script:csvId""],""max_tokens"":300}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't23.json')
$j = ReadJson (Join-Path $out 't23.json')
$c23 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T23 chat with file_ids → 200 + answer' ($code -eq '200' -and $c23 -ne '')
$maxLen = MaxPromptLen
T 'T23b attachment context injected (prompt_len>200)' ($maxLen -gt 200) "max prompt_len=$maxLen"

# T24 multiple file_ids
$b = NewBody 't24' "{""model"":""default-agent"",""messages"":[{""role"":""user"",""content"":""in base ai file allegati, riassumi brevemente il contenuto del report e della nota""}],""file_ids"":[""$script:txtId"",""$script:mdId""],""max_tokens"":300}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't24.json')
$j = ReadJson (Join-Path $out 't24.json')
$c24 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T24 multi file_ids' ($code -eq '200' -and $c24 -ne '')

# T25 unknown file_id → graceful skip, no crash
$b = NewBody 't25' "{""model"":""default-agent"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: 5+5?""}],""file_ids"":[""file-nonesiste""],""max_tokens"":200}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't25.json')
$j = ReadJson (Join-Path $out 't25.json')
$c25 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T25 unknown file_id → graceful' ($code -eq '200' -and $c25 -ne '')

# T26 file_id of an unsupported upload → graceful (no context, no crash)
$b = NewBody 't26' "{""model"":""default-agent"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: 6+6?""}],""file_ids"":[""$script:datId""],""max_tokens"":200}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't26.json')
$j = ReadJson (Join-Path $out 't26.json')
$c26 = if ($j -and $j.choices -and $j.choices[0].message.content) { $j.choices[0].message.content } else { '' }
T 'T26 unsupported file_id → graceful' ($code -eq '200' -and $c26 -ne '')

# T27 concurrency: two parallel chats
$b = NewBody 't27' "{""model"":""default-agent"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: 8+8?""}],""max_tokens"":200}"
$ra = Join-Path $out 'conc_a.txt'; $rb = Join-Path $out 'conc_b.txt'
$pa = Start-Process curl.exe -ArgumentList @('-s','-m','120','-X','POST',"$BaseUrl/v1/chat/completions",'-H','"Content-Type: application/json"','--data-binary',"@$b",'-o',(Join-Path $out 'ca.json'),'-w','%{http_code}') -RedirectStandardOutput $ra -WindowStyle Hidden -PassThru
$pb = Start-Process curl.exe -ArgumentList @('-s','-m','120','-X','POST',"$BaseUrl/v1/chat/completions",'-H','"Content-Type: application/json"','--data-binary',"@$b",'-o',(Join-Path $out 'cb.json'),'-w','%{http_code}') -RedirectStandardOutput $rb -WindowStyle Hidden -PassThru
Wait-Process -Id $pa.Id, $pb.Id -ErrorAction SilentlyContinue
$ca = (Get-Content $ra -Raw -ErrorAction SilentlyContinue).Trim()
$cb = (Get-Content $rb -Raw -ErrorAction SilentlyContinue).Trim()
T 'T27 two concurrent chats → both 200' ($ca -eq '200' -and $cb -eq '200') "a=$ca b=$cb"

# T31 concurrency stress: 4 parallel chats (shared orchestrator + shared log file)
$b = NewBody 't31' "{""model"":""default-agent"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: 9+9?""}],""max_tokens"":200}"
$procs = @()
for ($i = 1; $i -le 4; $i++) {
    $rf = Join-Path $out "conc$i.txt"
    $p = Start-Process curl.exe -ArgumentList @('-s','-m','120','-X','POST',"$BaseUrl/v1/chat/completions",'-H','"Content-Type: application/json"','--data-binary',"@$b",'-o',(Join-Path $out "cc$i.json"),'-w','%{http_code}') -RedirectStandardOutput $rf -WindowStyle Hidden -PassThru
    $procs += $p
}
foreach ($p in $procs) { Wait-Process -Id $p.Id -ErrorAction SilentlyContinue }
$all200 = $true; $dets = @()
foreach ($i in 1..4) {
    $c = (Get-Content (Join-Path $out "conc$i.txt") -Raw -ErrorAction SilentlyContinue).Trim()
    if ($c -ne '200') { $all200 = $false; $dets += "r$i=$c" }
}
T 'T31 four concurrent chats → all 200' ($all200) ($dets -join ' ')

# T28 upload > 25 MB → 400
$big = Join-Path $Work 'big.bin'
if (-not (Test-Path $big)) { cmd /c "fsutil file createnew $big 27262976" | Out-Null }
$code = CurlUpload "$BaseUrl/v1/files" $big (Join-Path $out 'u_big.json')
T 'T28 upload >25MB → 400' ($code -eq '400')

Write-Host ''
Write-Host '=== C5: misc endpoints ==='

# T29 models (agent sets + LLM providers)
$code = HttpReq 'GET' "$BaseUrl/v1/models" '' (Join-Path $out 'models.json') 30
$j = ReadJson (Join-Path $out 'models.json')
$count = if ($j -and $j.data) { @($j.data).Count } else { 0 }
$hasDefault = $false
if ($j -and $j.data) { $hasDefault = @($j.data | Where-Object { $_.id -eq 'default-agent' }).Count -gt 0 }
T 'T29 models list (agents + providers)' ($code -eq '200' -and $count -ge 12 -and $hasDefault) "count=$count"

# T32 models include LLM providers with characteristics (context_window, model_name)
$providers = @($j.data | Where-Object { $_.owned_by -eq 'llm-provider' })
$zai = @($providers | Where-Object { $_.id -eq 'Zai' } | Select-Object -First 1)
T 'T32 models include LLM providers w/ characteristics' ($providers.Count -ge 5 -and $zai.context_window -gt 0 -and $zai.model_name -ne '')

# T33 GET /v1/models/{id} — provider detail + unknown → 404
$code = HttpReq 'GET' "$BaseUrl/v1/models/Zai" '' (Join-Path $out 'm_zai.json') 30
$code2 = HttpReq 'GET' "$BaseUrl/v1/models/nope" '' (Join-Path $out 'm_nope.json') 30
T 'T33 single model detail (200) + unknown (404)' ($code -eq '200' -and $code2 -eq '404')

# T34 file raw content + DELETE lifecycle (200 → deleted → 404)
$code = HttpReq 'GET' "$BaseUrl/v1/files/$script:txtId/content" '' (Join-Path $out 'raw.txt') 30
$raw = Get-Content (Join-Path $out 'raw.txt') -Raw -ErrorAction SilentlyContinue
$codeD = HttpReq 'DELETE' "$BaseUrl/v1/files/$script:txtId" '' (Join-Path $out 'del.json') 30
$del = ReadJson (Join-Path $out 'del.json')
$codeD2 = HttpReq 'DELETE' "$BaseUrl/v1/files/$script:txtId" '' (Join-Path $out 'del2.json') 30
T 'T34 file content + DELETE lifecycle' ($code -eq '200' -and $raw -ne '' -and $codeD -eq '200' -and $del.deleted -eq $true -and $codeD2 -eq '404')

# T35 /v1/control: create session → state
$b = NewBody 't35' '{"create":true}'
$code = HttpReq 'POST' "$BaseUrl/v1/control" $b (Join-Path $out 'ctl_create.json') 30
$j = ReadJson (Join-Path $out 'ctl_create.json')
$sid = if ($j) { $j.session_id } else { '' }
T 'T35 control create session' ($code -eq '200' -and $sid -ne '' -and $j.llm -and $j.llm.provider -ne '')

# T36 /v1/control: switch LLM in use (config-only, no connectivity needed) + features + capabilities
$b = NewBody 't36' "{""session_id"":""$sid"",""llm_provider"":""Zai"",""features"":{""tts"":true}}"
$code = HttpReq 'POST' "$BaseUrl/v1/control" $b (Join-Path $out 'ctl_switch.json') 30
$j = ReadJson (Join-Path $out 'ctl_switch.json')
$switched = $j -and $j.llm -and $j.llm.provider -eq 'Zai' -and $j.features.tts -eq $true
$codeC = HttpReq 'GET' "$BaseUrl/v1/control" '' (Join-Path $out 'ctl_caps.json') 30
$cap = ReadJson (Join-Path $out 'ctl_caps.json')
T 'T36 control switch LLM + capabilities' ($code -eq '200' -and $switched -and $codeC -eq '200' -and $cap.capabilities -and @($cap.capabilities.providers).Count -ge 5)

# T37 chat with explicit llm_provider (stateless per-request provider)
$b = NewBody 't37' "{""model"":""default-agent"",""llm_provider"":""DeepSeekBridge"",""messages"":[{""role"":""user"",""content"":""rispondi in una frase: 3+3?""}],""max_tokens"":200}"
$code = HttpReq 'POST' "$BaseUrl/v1/chat/completions" $b (Join-Path $out 't37.json')
$j = ReadJson (Join-Path $out 't37.json')
$c37 = if ($j -and $j.choices) { $j.choices[0].message.content } else { '' }
T 'T37 chat with explicit llm_provider' ($code -eq '200' -and $c37 -ne '')

# T38 TTS voices endpoint responds (kokoro engine; available or 501 depending on assets)
$code = HttpReq 'GET' "$BaseUrl/v1/audio/voices" '' (Join-Path $out 'voices.json') 60
$j = ReadJson (Join-Path $out 'voices.json')
T 'T38 /v1/audio/voices responds' ($code -eq '200' -and $j -and $j.engine -eq 'kokoro')

# T30 health
$code = HttpReq 'GET' "$BaseUrl/health" '' (Join-Path $out 'health.json') 30
$h = Get-Content (Join-Path $out 'health.json') -Raw
T 'T30 health' ($code -eq '200' -and $h.Contains('healthy'))

Write-Host ''
Write-Host "RESULT: $($script:pass) passed, $($script:fail) failed"
if ($script:fail -eq 0) { exit 0 } else { exit 1 }
