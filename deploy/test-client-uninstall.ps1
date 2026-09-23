# Real file/process fixture, with system networking/service/registry operations mocked.
# No real Link installation is stopped or removed by this test.
$ErrorActionPreference='Stop'
$root=Join-Path $env:TEMP ('Link-Uninstall-Fixture-'+[guid]::NewGuid().ToString('N'))
$root=[IO.Path]::GetFullPath($root)
$oldProgram=$env:ProgramFiles;$oldData=$env:ProgramData
$process=$null;$locked=$null
New-Item -ItemType Directory -Path $root | Out-Null
try {
 $env:ProgramFiles=[IO.Path]::GetFullPath((Join-Path $root 'programs'));$env:ProgramData=[IO.Path]::GetFullPath((Join-Path $root 'state'))
 $program=Join-Path $env:ProgramFiles 'Link';$data=Join-Path $env:ProgramData 'Link';$source=Join-Path $root 'package'
 foreach($dir in @($program,$data,$source)){New-Item -ItemType Directory -Path $dir -Force | Out-Null}
 $fixtureCode=Join-Path $root 'Fixture.cs';Set-Content $fixtureCode 'class Fixture {static void Main(){System.Threading.Thread.Sleep(600000);}}'
 & (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe') /nologo /target:winexe ('/out:'+(Join-Path $source 'Link.exe')) ('/resource:'+$fixtureCode+',Link.AppIcon') ('/resource:'+$fixtureCode+',Link.Layer2Network') ('/resource:'+$fixtureCode+',Link.Script.uninstall.ps1') $fixtureCode
 if($LASTEXITCODE -ne 0){throw 'Fixture build failed'}
 $older=Join-Path $root 'older-package';New-Item -ItemType Directory -Path $older | Out-Null
 Copy-Item (Join-Path $source 'Link.exe') (Join-Path $older 'Link.exe')
 # Append an unused byte to the new package: older active copy has a different hash.
 $bytes=[IO.File]::ReadAllBytes((Join-Path $source 'Link.exe'));[IO.File]::WriteAllBytes((Join-Path $source 'Link.exe'),[byte[]]($bytes+0))
 Copy-Item (Join-Path $source 'Link.exe') (Join-Path $program 'Link.exe')
 Set-Content (Join-Path $source 'keep-user-document.txt') 'keep'
 Set-Content (Join-Path $data 'device.bin') 'synthetic identity'
 @{program=$program}|ConvertTo-Json|Set-Content (Join-Path $data 'install-owned.json')
 $process=Start-Process (Join-Path $older 'Link.exe') -WindowStyle Hidden -PassThru
 $script=Get-Content (Join-Path $PSScriptRoot '..\client\uninstall.ps1') -Raw -Encoding UTF8
 # Remove ONLY the administrator check from this isolated copy. All mutating
 # system commands below are doubles, and deletion roots point into this fixture.
 $script=$script.Replace("if(-not `$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Run as administrator'}",'')
 $runner=Join-Path $root 'uninstall.ps1';Set-Content $runner $script -Encoding UTF8
 $global:FixtureService=$true;$global:FixtureDisposed=$false;$global:FixtureStop=$false
 function Get-ItemProperty {param($LiteralPath,$ErrorAction) if($LiteralPath -like '*\Services\LinkAgent' -and $global:FixtureService){[pscustomobject]@{ImagePath='"'+(Join-Path $env:ProgramFiles 'Link\Link.exe')+'" --service'}}}
 function Remove-ItemProperty {param($LiteralPath,$Name,$ErrorAction)}
 function Get-Service {param($Name,$ErrorAction)
  if($Name -eq 'LinkAgent' -and $global:FixtureService){$s=[pscustomobject]@{Status='Running'};$s|Add-Member ScriptMethod WaitForStatus {param($status,$time) if(-not $global:FixtureStop){throw 'Service not stopped'}};$s|Add-Member ScriptMethod Dispose {$global:FixtureDisposed=$true};$s}
 }
 function Stop-Service {param($Name) if($Name -ne 'LinkAgent'){throw 'Unrelated service touched'};$global:FixtureStop=$true}
 function sc.exe {if($args[0] -eq 'delete'){if(-not $global:FixtureDisposed){throw 'Service handle leaked before delete'};$global:FixtureService=$false};$global:LASTEXITCODE=0}
 function Get-NetAdapter {param([switch]$IncludeHidden,$ErrorAction)}
 function Get-NetFirewallRule {param($Name,$ErrorAction)}
 function Get-CimInstance {param($ClassName)
  if($ClassName -eq 'Win32_Process'){
   $process.Refresh();if(-not $process.HasExited){[pscustomobject]@{Name='Link.exe';ExecutablePath=(Join-Path $older 'Link.exe');ProcessId=$process.Id}}
   # Same filename with different contents must remain untouched.
   [pscustomobject]@{Name='Link.exe';ExecutablePath=(Join-Path $root 'foreign\Link.exe');ProcessId=$PID}
  }
 }
 New-Item -ItemType Directory -Path (Join-Path $root 'foreign') | Out-Null
 Set-Content (Join-Path $root 'foreign\Link.exe') 'unrelated executable'
 $plan=& $runner -Plan -SourceRoot $source | ConvertFrom-Json
 if($plan.portableFiles -notcontains (Join-Path $source 'Link.exe') -or $plan.portableFiles -notcontains (Join-Path $older 'Link.exe')){throw 'Portable executable missed'}
 $process.Refresh();if($process.HasExited -or $global:FixtureStop){throw 'Plan changed resources'}
 # Failed network recovery must stop before files or identity are deleted.
 Set-Content (Join-Path $data 'layer2-journal.json') '{}'
 function Start-Process {param($FilePath,$ArgumentList,$WindowStyle,[switch]$Wait,[switch]$PassThru)
  if($ArgumentList -ne '--cleanup-layer2'){throw 'Unexpected helper launch'};[pscustomobject]@{ExitCode=1}
 }
 $failed=$false
 try{$null=& $runner -SourceRoot $source -RemoveIdentity}catch{if($_.Exception.Message -like 'Network cleanup incomplete*'){$failed=$true}else{throw}}
 if(-not $failed -or -not (Test-Path (Join-Path $program 'Link.exe')) -or -not (Test-Path (Join-Path $data 'device.bin'))){throw 'Recovery failure lost installation data'}
 Remove-Item -LiteralPath (Join-Path $data 'layer2-journal.json')
 # A file lock must prevent success, retain identity and allow retry.
 Set-Content (Join-Path $program 'locked.bin') 'locked'
 $locked=[IO.File]::Open((Join-Path $program 'locked.bin'),'Open','Read','None')
 $failed=$false
 try{$output=@(& $runner -SourceRoot $source -RemoveIdentity);if($output -contains 'LINK_COMPLETE|uninstall'){throw 'False success with locked file'}}catch{$failed=$true}
 if(-not $failed -or -not (Test-Path (Join-Path $data 'device.bin'))){throw 'Failure did not retain identity'}
 $process.Refresh();if(-not $process.HasExited){throw 'Portable UI process left running'}
 $locked.Dispose();$locked=$null
 # Source executable was already removed; resume from the owned installation journal.
 $output=@(& $runner -RemoveIdentity)
 if($output -notcontains 'LINK_COMPLETE|uninstall'){throw 'No verified completion'}
 if((Test-Path $program) -or (Test-Path $data) -or (Test-Path (Join-Path $source 'Link.exe')) -or (Test-Path (Join-Path $older 'Link.exe'))){throw 'Owned files remain'}
 if(-not (Test-Path (Join-Path $source 'keep-user-document.txt')) -or -not (Test-Path (Join-Path $root 'foreign\Link.exe'))){throw 'User or foreign files removed'}
 'PASS: real portable process exited; locked files cannot report success; retry removes owned files and identity; unrelated files retained'
}finally {
 if($locked){$locked.Dispose()};if($process){if(-not $process.HasExited){$process.Kill();$process.WaitForExit()};$process.Dispose()}
 $env:ProgramFiles=$oldProgram;$env:ProgramData=$oldData
 $resolved=[IO.Path]::GetFullPath($root)
 if($resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP)+'\',[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -like 'Link-Uninstall-Fixture-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
 Remove-Variable -Scope Global -Name FixtureService,FixtureDisposed,FixtureStop -ErrorAction SilentlyContinue
}





