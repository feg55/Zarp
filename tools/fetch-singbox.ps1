# Downloads the pinned sing-box release (the engine for "use my server instead of WARP"), checks its SHA-256
# and packs sing-box.exe into vendor\singbox.zip, which is embedded into Zarp.exe.
# Unlike zapret2, the version is pinned on purpose: Zarp generates sing-box configuration for exactly this release,
# and a newer major version may change the format. To update, change $Version and $Sha256 together
# (the digest is shown on the release page: https://github.com/SagerNet/sing-box/releases).
param(
    [string]$Out = (Join-Path $PSScriptRoot '..\vendor\singbox.zip'),
    [string]$Version = '1.13.14',
    [string]$Sha256 = 'f580782c6dd10f7691c66cea1d7c421813c5fbf7e305d1ee7ce0c3a40d196341'
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$tag = "v$Version"
$name = "sing-box-$Version-windows-amd64"
$assetUrl = "https://github.com/SagerNet/sing-box/releases/download/$tag/$name.zip"

if (Test-Path $Out) {
    $z = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Out))
    $v = $z.GetEntry('version.txt')
    $have = if ($v) { (New-Object IO.StreamReader($v.Open())).ReadToEnd().Trim() } else { '' }
    $hasExe = $null -ne $z.GetEntry('sing-box.exe')
    $z.Dispose()
    if ($have -eq $tag -and $hasExe) { Write-Output "sing-box $tag already in $Out"; return }
}

Write-Output "Downloading sing-box $tag ..."
$wc = New-Object Net.WebClient
$wc.Headers['User-Agent'] = 'Zarp-build'
$bytes = $wc.DownloadData($assetUrl)
$sha = [Security.Cryptography.SHA256]::Create()
$actual = ([BitConverter]::ToString($sha.ComputeHash($bytes)) -replace '-', '').ToLowerInvariant()
if ($actual -ne $Sha256.ToLowerInvariant()) { throw "SHA-256 of $name.zip is $actual, expected $Sha256" }

$src = New-Object IO.Compression.ZipArchive((New-Object IO.MemoryStream(, $bytes)), [IO.Compression.ZipArchiveMode]::Read)
$ms = New-Object IO.MemoryStream
$dst = New-Object IO.Compression.ZipArchive($ms, [IO.Compression.ZipArchiveMode]::Create, $true)
$entry = $src.Entries | Where-Object { $_.Name -eq 'sing-box.exe' } | Select-Object -First 1
if (-not $entry) { throw "sing-box.exe not found in $name.zip" }
$e = $dst.CreateEntry('sing-box.exe', [IO.Compression.CompressionLevel]::Optimal)
$w = $e.Open(); $r = $entry.Open(); $r.CopyTo($w); $r.Close(); $w.Close()
$ve = $dst.CreateEntry('version.txt')
$w = New-Object IO.StreamWriter($ve.Open()); $w.Write($tag); $w.Close()
$dst.Dispose(); $src.Dispose()

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $ms.ToArray())
Write-Output ("sing-box {0} -> {1} ({2:N0} KB)" -f $tag, $Out, ($ms.Length / 1KB))
