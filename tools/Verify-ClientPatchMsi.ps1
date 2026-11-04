param([Parameter(Mandatory=$true)][string]$Path)
$ErrorActionPreference='Stop'
$file=(Get-Item -LiteralPath $Path).FullName
$signature=Get-AuthenticodeSignature -LiteralPath $file
if($signature.Status-ne'Valid'-or$signature.SignerCertificate.Subject-ne'CN=KCAA(LEE HYEONGBIN)'){throw 'Invalid candidate signature'}
$installer=New-Object -ComObject WindowsInstaller.Installer
$database=$installer.OpenDatabase($file,0)
function Rows([string]$sql) {
 $view=$database.OpenView($sql);$null=$view.Execute()
 try{while($record=$view.Fetch()){[pscustomobject]@{key=$record.StringData(1);value=$record.StringData(2)}}}finally{$null=$view.Close()}
}
$properties=@{};Rows 'SELECT `Property`,`Value` FROM `Property`'|ForEach-Object {$properties[$_.key]=$_.value}
$sequence=@{};Rows 'SELECT `Action`,`Sequence` FROM `InstallExecuteSequence`'|ForEach-Object {$sequence[$_.key]=[int]$_.value}
if($properties.ProductVersion-ne'2.0.145'){throw 'Version drift'}
if($properties.REINSTALLMODE-ne'emus'){throw 'Equal-version files would be skipped'}
if(!($sequence.InstallInitialize-lt$sequence.CloseInstalledAppsElevated-and$sequence.CloseInstalledAppsElevated-lt$sequence.InstallFiles-and$sequence.InstallExecute-lt$sequence.RemoveExistingProducts-and$sequence.RemoveExistingProducts-lt$sequence.InstallFinalize)){throw 'Unsafe same-version replacement sequence'}
[pscustomobject]@{sha256=(Get-FileHash -LiteralPath $file).Hash;signature='Valid';productVersion=$properties.ProductVersion;installInitialize=$sequence.InstallInitialize;closeApps=$sequence.CloseInstalledAppsElevated;removeOld=$sequence.RemoveExistingProducts;installFiles=$sequence.InstallFiles;sameVersionSequenceVerified=$true}|ConvertTo-Json
