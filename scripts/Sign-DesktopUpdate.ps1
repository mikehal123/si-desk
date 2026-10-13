param([Parameter(Mandatory=$true)][string]$Version,[Parameter(Mandatory=$true)][string]$Archive,[Parameter(Mandatory=$true)][string]$CertificateThumbprint,[string]$Notes='xAI Desk desktop update.',[string]$Output='update.json')
$ErrorActionPreference='Stop'
if($Version -notmatch '^\d+\.\d+\.\d+$'){throw 'Use a stable numeric version.'}
$cert=Get-Item -LiteralPath ('Cert:\CurrentUser\My\'+$CertificateThumbprint)
$rsa=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($cert)
if(!$rsa){throw 'Release signing key is not available on this Windows account.'}
$manifest=[ordered]@{Version=$Version;Url="https://github.com/mikehal123/xai-desk/releases/download/v$Version/xAIDesk-Windows-$Version.zip";Sha256=(Get-FileHash -LiteralPath $Archive -Algorithm SHA256).Hash.ToLowerInvariant();Bytes=(Get-Item -LiteralPath $Archive).Length;Notes=$Notes;ExpiresUtc=[DateTimeOffset]::UtcNow.AddDays(90).ToString('o')}
$data=[Text.Encoding]::UTF8.GetBytes(($manifest | ConvertTo-Json -Compress))
$signature=$rsa.SignData($data,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
$envelope=@{Payload=[Convert]::ToBase64String($data);Signature=[Convert]::ToBase64String($signature)} | ConvertTo-Json
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Output),$envelope,(New-Object Text.UTF8Encoding($false)))
Write-Output "Signed update $Version. Private key remains in the Windows certificate store."
