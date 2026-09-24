# Downloads the latest zapret2 release and packs the files Zarp needs into vendor\zapret2.zip,
# which is embedded into Zarp.exe. Everything is done in memory: Windows Defender quarantines
# the full release archive if it is written to disk.
param([string]$Out = (Join-Path $PSScriptRoot '..\vendor\zapret2.zip'))
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

# Only what winws2 needs on Windows x64 plus the fake packets Zarp strategies use.
$keep = @(
    'binaries/windows-x86_64/winws2.exe',
    'binaries/windows-x86_64/cygwin1.dll',
    'binaries/windows-x86_64/WinDivert.dll',
    'binaries/windows-x86_64/WinDivert64.sys',
    'lua/zapret-lib.lua',
    'lua/zapret-antidpi.lua',
    'lua/zapret-auto.lua',
    'files/fake/quic_initial_www_google_com.bin',
    'files/fake/quic_initial_vk_com.bin',
    'files/fake/tls_clienthello_www_google_com.bin',
    'files/fake/tls_clienthello_vk_com.bin',
    'files/fake/stun.bin'
)

function New-Client {
    $wc = New-Object Net.WebClient
    $wc.Headers['User-Agent'] = 'Zarp-build'
    # in GitHub Actions the token lifts the API rate limit
    if ($env:GITHUB_TOKEN) { $wc.Headers['Authorization'] = "Bearer $($env:GITHUB_TOKEN)" }
    $wc
}

# The latest tag comes from the releases/latest redirect: unlike the API it has no rate limit
# (the API allows 60 requests per hour per IP, which a shared VPN/WARP IP exhausts quickly).
$req = [Net.HttpWebRequest]::Create('https://github.com/bol-van/zapret2/releases/latest')
$req.AllowAutoRedirect = $false
$req.UserAgent = 'Zarp-build'
$resp = $req.GetResponse()
$location = $resp.Headers['Location']
$resp.Close()
if ($location -notmatch '/releases/tag/(v[\d.]+)$') { throw "unexpected releases/latest redirect: $location" }
$tag = $Matches[1]
$assetUrl = "https://github.com/bol-van/zapret2/releases/download/$tag/zapret2-$tag.zip"

if (Test-Path $Out) {
    $z = [IO.Compression.ZipFile]::OpenRead((Resolve-Path $Out))
    $v = $z.GetEntry('version.txt')
    $have = if ($v) { (New-Object IO.StreamReader($v.Open())).ReadToEnd().Trim() } else { '' }
    $z.Dispose()
    if ($have -eq $tag) { Write-Output "zapret2 $tag already in $Out"; return }
}

Write-Output "Downloading zapret2 $tag ..."
$bytes = (New-Client).DownloadData($assetUrl)

$src = New-Object IO.Compression.ZipArchive((New-Object IO.MemoryStream(, $bytes)), [IO.Compression.ZipArchiveMode]::Read)
$ms = New-Object IO.MemoryStream
$dst = New-Object IO.Compression.ZipArchive($ms, [IO.Compression.ZipArchiveMode]::Create, $true)
$found = 0
foreach ($e in $src.Entries) {
    $inner = (($e.FullName -split '/') | Select-Object -Skip 1) -join '/'
    if ($keep -notcontains $inner) { continue }
    # layout inside the embedded zip = layout of the zapret2 folder Zarp uses
    $name = if ($inner -like 'binaries/*') { $e.Name } else { $inner }
    $entry = $dst.CreateEntry($name, [IO.Compression.CompressionLevel]::Optimal)
    $w = $entry.Open(); $r = $e.Open(); $r.CopyTo($w); $r.Close(); $w.Close()
    $found++
}
$ve = $dst.CreateEntry('version.txt')
$w = New-Object IO.StreamWriter($ve.Open()); $w.Write($tag); $w.Close()
$dst.Dispose(); $src.Dispose()
if ($found -ne $keep.Count) { throw "expected $($keep.Count) files in zapret2 $tag, found $found" }

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
[IO.File]::WriteAllBytes([IO.Path]::GetFullPath($Out), $ms.ToArray())
Write-Output ("zapret2 {0} -> {1} ({2:N0} KB)" -f $tag, $Out, ($ms.Length / 1KB))
