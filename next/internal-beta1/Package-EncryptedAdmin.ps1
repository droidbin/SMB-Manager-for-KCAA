param(
 [Parameter(Mandatory=$true)][string]$Installer,
 [Parameter(Mandatory=$true)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedSha256,
 [Parameter(Mandatory=$true)][string]$OutputZip,
 [string]$SevenZip='C:\Program Files\7-Zip\7z.exe'
)
# Run interactively: 7-Zip prompts for a new password; no password is accepted as an argument.
$ErrorActionPreference='Stop'
$source=(Resolve-Path -LiteralPath $Installer).Path
$target=[IO.Path]::GetFullPath($OutputZip)
if([IO.Path]::GetExtension($source) -ne '.msi' -or [IO.Path]::GetFileName($source) -notmatch '^SMB-Manager-.+\.msi$'){throw 'Supply only the reviewed administrator MSI, never a server data folder.'}
if([IO.Path]::GetExtension($target) -ne '.zip'){throw 'Output must be a ZIP file.'}
if(Test-Path -LiteralPath $target){throw 'Preserve existing artifacts. Choose a new output name.'}
if(!(Test-Path -LiteralPath $SevenZip -PathType Leaf)){throw '7-Zip is required.'}
if((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $ExpectedSha256){throw 'Installer does not match the reviewed SHA-256.'}
$parent=Split-Path $target
if(!(Test-Path -LiteralPath $parent -PathType Container)){throw 'Create the intended release output folder first.'}
Write-Host 'Enter a NEW distribution password in the 7-Zip prompt. Do not use Drive, Windows or administrator API passwords.'
& $SevenZip a -tzip -mem=AES256 -p -sse -- $target $source
if($LASTEXITCODE -ne 0){throw 'Encrypted archive creation failed. Do not publish the output.'}
Write-Host 'Re-enter the distribution password to verify the archive.'
& $SevenZip t -p -- $target
if($LASTEXITCODE -ne 0){throw 'Archive verification failed. Do not publish.'}
$listing=& $SevenZip l -slt -- $target
if($LASTEXITCODE -ne 0 -or !($listing -match '^Encrypted = \+$') -or !($listing -match '^Method = .*AES-256')){throw 'AES-256 encryption was not confirmed. Do not publish.'}
$hash=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
[IO.File]::WriteAllText($target+'.sha256',($hash+'  '+[IO.Path]::GetFileName($target)+[Environment]::NewLine))
Write-Output 'Encrypted administrator ZIP verified locally. No GitHub upload or channel publication was performed.'
