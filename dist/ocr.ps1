# ocr.ps1 - read text + positions from an image using the built-in Windows OCR engine.
#
# Output: one line per word, machine readable:
#     x|y|w|h|text
#
# Server mode (the normal way RobloxAuto.exe uses it):
#     powershell -NoProfile -ExecutionPolicy Bypass -File ocr.ps1 -Server
#   Stays alive and reads image paths from stdin, one per line. For each path it prints
#   the words and then "===END===". Keeping the process alive means the OCR engine stays
#   warm, so a read costs ~100ms instead of ~1s of PowerShell + WinRT startup.
#
# One-shot mode (fallback):
#     powershell -NoProfile -ExecutionPolicy Bypass -File ocr.ps1 <image>

param([string]$Path, [switch]$Server)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Runtime.WindowsRuntime

[Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime] | Out-Null
[Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime] | Out-Null
[Windows.Graphics.Imaging.SoftwareBitmap, Windows.Graphics, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Ocr.OcrEngine, Windows.Media, ContentType = WindowsRuntime] | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
})[0]

function Await($op, $type) {
    $m = $asTaskGeneric.MakeGenericMethod($type)
    $t = $m.Invoke($null, @($op))
    $t.Wait(-1) | Out-Null
    $t.Result
}

function Write-Line([string]$s) {
    [Console]::Out.WriteLine($s)
    [Console]::Out.Flush()
}

$engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()

function Read-Frame([string]$file) {
    if (-not $file) { return }
    try {
        $sf      = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($file)) ([Windows.Storage.StorageFile])
        $stream  = Await ($sf.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
        $decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
        $bitmap  = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])
        $result  = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])
        foreach ($line in $result.Lines) {
            foreach ($w in $line.Words) {
                $r = $w.BoundingRect
                $t = $w.Text -replace '\|', ' '
                Write-Line ("{0}|{1}|{2}|{3}|{4}" -f [int]$r.X, [int]$r.Y, [int]$r.Width, [int]$r.Height, $t)
            }
        }
        $stream.Dispose()
    } catch {
        # a broken frame must not kill the helper - report nothing and keep serving
    }
}

if ($Server) {
    if (-not $engine) { Write-Line '===ERROR==='; exit 3 }
    Write-Line '===READY==='
    while ($true) {
        $line = [Console]::In.ReadLine()
        if ($null -eq $line) { break }
        if ($line -eq 'QUIT') { break }
        if ($line.Length -eq 0) { Write-Line '===END==='; continue }
        Read-Frame $line
        Write-Line '===END==='
    }
    exit 0
}

if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { exit 2 }
if (-not $engine) { exit 3 }
Read-Frame $Path
