# AgentBridge one-line installer (Windows PowerShell).
#
#   irm https://graphene-lab.github.io/AgentBridge/install.ps1 | iex
#
# Downloads the latest Windows release archive from GitHub, extracts it into
# %LOCALAPPDATA%\AgentBridge and prints how to start the agent. The archive is
# self-contained (~460 MB): no .NET installation needed, Kokoro TTS voices included.

$ErrorActionPreference = "Stop"

$Repo = "Graphene-Lab/AgentBridge"
$Base = "https://github.com/$Repo/releases/latest/download"
$Dest = Join-Path $env:LOCALAPPDATA "AgentBridge"

# Only a win-x64 archive is published (no win-arm64 asset yet).
$Asset = "agentbridge-win-x64.tar.gz"

Write-Host "Installing AgentBridge into $Dest ..."
Write-Host "Downloading $Base/$Asset"

New-Item -ItemType Directory -Force -Path $Dest | Out-Null
$tmp = Join-Path $env:TEMP ("agentbridge-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $tmp | Out-Null

try {
    $archive = Join-Path $tmp $Asset
    Invoke-WebRequest -Uri "$Base/$Asset" -OutFile $archive -UseBasicParsing
    tar -xzf $archive -C $tmp
    Remove-Item $archive
    Copy-Item -Path (Join-Path $tmp "*") -Destination $Dest -Recurse -Force
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

# TTS GPU acceleration: if a CUDA-capable GPU is present, offer the CUDA Toolkit + cuDNN
# (AgentBridge ships the GPU ONNX Runtime — the TTS uses CUDA automatically when the toolkit
# is installed; the app probes the toolkit dirs at runtime, no manual PATH setup needed).
try {
    $gpu = (nvidia-smi --query-gpu=name --format=csv,noheader 2>$null | Select-Object -First 1)
} catch { $gpu = $null }
if ($gpu) {
    Write-Host ""
    Write-Host "CUDA-capable GPU detected: $gpu"
    $toolkit = Test-Path "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA"
    $cudnn = Test-Path "C:\Program Files\NVIDIA\CUDNN"
    if ($toolkit -and $cudnn) {
        Write-Host "CUDA Toolkit + cuDNN already installed — the TTS will run on the GPU automatically."
    }
    else {
        $ans = Read-Host "The TTS can run ~1.5x faster on the GPU. Install the CUDA Toolkit 12.8 + cuDNN 9.7 (large download)? [y/N]"
        if ($ans -match "^[yY]") {
            Write-Host "Downloading the CUDA Toolkit 12.8 network installer..."
            curl.exe -L -o "$env:TEMP\cuda-12.8.2-net.exe" "https://developer.download.nvidia.com/compute/cuda/12.8.2/network_installers/cuda_12.8.2_windows_network.exe"
            Write-Host "Installing CUDA Toolkit (silent)..."
            Start-Process -Wait "$env:TEMP\cuda-12.8.2-net.exe" -ArgumentList "-s","-noreboot"
            Write-Host "Downloading cuDNN 9.7..."
            curl.exe -L -o "$env:TEMP\cudnn-9.7.exe" "https://developer.download.nvidia.com/compute/cudnn/9.7.0/local_installers/cudnn_9.7.0_windows.exe"
            Write-Host "Installing cuDNN 9.7 (silent)..."
            Start-Process -Wait "$env:TEMP\cudnn-9.7.exe" -ArgumentList "/s"
            Write-Host "CUDA Toolkit installed — the TTS will use the GPU automatically."
        }
        else {
            Write-Host "TTS will run on the CPU (slower). You can install CUDA later — AgentBridge picks it up automatically at runtime."
        }
    }
}
else {
    Write-Host "No CUDA-capable GPU detected — the TTS will use the CPU."
}

Write-Host ""
Write-Host "AgentBridge installed to $Dest."
Write-Host "TTS is ready: the Kokoro voices and model are included — nothing else to install."
Write-Host "Run:  $(Join-Path $Dest 'agent.exe')"
