# Reject unsigned, untrusted, altered or untimestamped release binaries.
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'

$file = (Resolve-Path -LiteralPath $Path).Path
$signature = Get-AuthenticodeSignature -LiteralPath $file
if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or $signature.SignatureType -ne 'Authenticode') {
    throw "Release signature is not valid: $($signature.Status). $($signature.StatusMessage)"
}
if ($null -eq $signature.TimeStamperCertificate) {
    throw 'Release signature has no trusted timestamp. Sign with an RFC 3161 timestamp before publishing.'
}
Write-Output "Verified publisher: $($signature.SignerCertificate.Subject)"
