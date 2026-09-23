# Test only a generated portable-server directory; service/process commands are doubles.
$ErrorActionPreference='Stop'
$testDir=Join-Path ([IO.Path]::GetTempPath()) ('Link-Uninstall-Test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $testDir 'data') -Force | Out-Null
foreach($name in @('LinkServer.exe','Uninstall.exe','uninstall.ps1','keep-user-document.txt','data\.link-owned','data\state.json')){Set-Content -LiteralPath (Join-Path $testDir $name) -Value 'synthetic'}
$global:LinkUninstallTestRoot=$testDir;$global:LinkUninstallTestCalls=New-Object 'Collections.Generic.List[string]'
function Get-CimInstance {param($ClassName)
 if($ClassName -eq 'Win32_Service'){
  if(-not $global:LinkUninstallTestCalls.Contains('sc delete OwnedTestService')){[pscustomobject]@{Name='OwnedTestService';PathName='"'+(Join-Path $global:LinkUninstallTestRoot 'LinkServer.exe')+'" -data data'}}
  [pscustomobject]@{Name='ForeignService';PathName='C:\Unrelated\database.exe'}
 }else{
  if(-not $global:LinkUninstallTestCalls.Contains('process 424242')){[pscustomobject]@{ProcessId=424242;ExecutablePath=(Join-Path $global:LinkUninstallTestRoot 'LinkServer.exe')}}
  [pscustomobject]@{ProcessId=313131;ExecutablePath='C:\Unrelated\database.exe'}
 }
}
function Stop-Service {param($Name) $global:LinkUninstallTestCalls.Add('stop '+$Name)}
function Stop-Process {param($Id,[switch]$Force) $global:LinkUninstallTestCalls.Add('process '+$Id)}
function sc.exe {$global:LinkUninstallTestCalls.Add('sc '+($args -join ' '));$global:LASTEXITCODE=0}
try{
 $script=Join-Path $PSScriptRoot 'uninstall-windows-server.ps1'
 $null=& $script -ProgramRoot $testDir -RemoveIdentity -Plan
 if($global:LinkUninstallTestCalls.Count -ne 0 -or -not (Test-Path (Join-Path $testDir 'LinkServer.exe'))){throw 'Plan mutated resources'}
 & $script -ProgramRoot $testDir -RemoveIdentity
 if(-not (Test-Path (Join-Path $testDir 'keep-user-document.txt')) -or (Test-Path (Join-Path $testDir 'data'))){throw 'File ownership cleanup failed'}
 if(($global:LinkUninstallTestCalls -join ',') -ne 'stop OwnedTestService,sc delete OwnedTestService,process 424242'){throw 'Foreign service or process affected'}
 function Get-ItemProperty {param($LiteralPath,$ErrorAction) if($LiteralPath.EndsWith('\LinkAgent')){[pscustomobject]@{ImagePath='"C:\Unrelated\foreign.exe"'}}}
 $rejected=$false
 try{& (Join-Path (Split-Path $PSScriptRoot -Parent) 'client\uninstall.ps1') -Plan}catch{if($_.Exception.Message -like '*another installation*'){$rejected=$true}else{throw}}
 if(-not $rejected -or $global:LinkUninstallTestCalls.Count -ne 3){throw 'Client uninstaller accepted a foreign service'}
 Write-Output 'PASS: Windows uninstall plan, exact service/process ownership, retained user file, removed owned data'
}finally{
 if([IO.Path]::GetFullPath($testDir).StartsWith([IO.Path]::GetTempPath(),[StringComparison]::OrdinalIgnoreCase)){Remove-Item -LiteralPath $testDir -Recurse -Force}
 Remove-Variable -Scope Global -Name LinkUninstallTestRoot,LinkUninstallTestCalls -ErrorAction SilentlyContinue
}
