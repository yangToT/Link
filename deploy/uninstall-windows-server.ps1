param([Parameter(Mandatory=$true)][string]$ProgramRoot,[switch]$RemoveIdentity,[switch]$Plan)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($ProgramRoot).TrimEnd('\')
if($root -eq [IO.Path]::GetPathRoot($root).TrimEnd('\')){throw 'Drive root is not an installation directory'}
if(-not (Test-Path -LiteralPath (Join-Path $root 'LinkServer.exe'))){throw 'Select the portable Link server directory'}
if((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse point not allowed'}
$data=Join-Path $root 'data'
if($RemoveIdentity -and (Test-Path -LiteralPath $data)){
 if(-not (Test-Path -LiteralPath (Join-Path $data '.link-owned'))){throw 'Data ownership unknown; retain data until ownership is verified'}
 $queue=New-Object 'Collections.Generic.Queue[string]';$queue.Enqueue($data)
 while($queue.Count){$current=$queue.Dequeue();if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse point in instance data'};foreach($item in Get-ChildItem -LiteralPath $current -Force){if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse point in instance data'};if($item.PSIsContainer){$queue.Enqueue($item.FullName)}}}
}
$binary=Join-Path $root 'LinkServer.exe'
$services=@(Get-CimInstance Win32_Service | Where-Object {$_.PathName -and ($_.PathName.StartsWith('"'+$binary+'"',[StringComparison]::OrdinalIgnoreCase) -or $_.PathName.Equals($binary,[StringComparison]::OrdinalIgnoreCase))})
if($Plan){[pscustomobject]@{program=$binary;data=$data;services=@($services.Name);removeIdentity=[bool]$RemoveIdentity;externalBackend='Preserved; not installed by this portable package'}|ConvertTo-Json;return}
foreach($service in $services){Stop-Service -Name $service.Name;& sc.exe delete $service.Name | Out-Null;if($LASTEXITCODE -notin @(0,1060)){throw 'Cannot delete owned server service'}}
Get-CimInstance Win32_Process | Where-Object {$_.ExecutablePath -and $_.ExecutablePath.Equals($binary,[StringComparison]::OrdinalIgnoreCase)} | ForEach-Object {Stop-Process -Id $_.ProcessId -Force}
if($RemoveIdentity -and (Test-Path -LiteralPath $data)){Remove-Item -LiteralPath $data -Recurse -Force}
# Portable packages may sit alongside user documents: remove only our named programs.
foreach($name in @('LinkServer.exe','Uninstall.exe','uninstall.ps1')){$path=Join-Path $root $name;if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Force}}
Write-Output 'Portable Link server removed. External network backends and other files were preserved.'
