# Exercise the actual installer orchestration tail without services or network changes.
$ErrorActionPreference='Stop'
$source=Get-Content (Join-Path $PSScriptRoot '..\client\manage-components.ps1') -Raw -Encoding UTF8
$start=$source.IndexOf(" if(`$Action -eq 'repair' -and")
$end=$source.IndexOf('}catch{Log', $start)
if($start -lt 0 -or $end -lt 0){throw 'Installer sequence not found'}
$body=$source.Substring($start,$end-$start)
$dir=Join-Path $env:TEMP ('Link-Install-Test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null
$global:LinkComponentTestStops=0
function Set-Service {param($Name,$StartupType)}
function Stop-Service {param($Name) $global:LinkComponentTestStops++}
function Log($text){}
try {
 $stub=Join-Path $dir 'install-layer2.ps1'
 Set-Content $stub "param(`$ComponentSource,`$Report)`nWrite-Output 'SUCCESS'" -Encoding UTF8
 $runner=Join-Path $dir 'run.ps1'
 Set-Content $runner ("param(`$Action,`$cache,`$report)`n"+$body) -Encoding UTF8
 foreach($prior in @($null,0,1,1060)){
  $global:LASTEXITCODE=$prior;$before=$global:LinkComponentTestStops
  $null=& $runner -Action install -cache $dir
  if($global:LinkComponentTestStops -ne ($before+2)){throw 'Successful script did not finish with stale native exit code'}
 }
 Set-Content $stub "param(`$ComponentSource,`$Report)`nthrow 'Synthetic install failure'" -Encoding UTF8
 $before=$global:LinkComponentTestStops;$caught=$false
 try {$null=& $runner -Action install -cache $dir}catch{if($_.Exception.Message -eq 'Synthetic install failure'){$caught=$true}else{throw}}
 if(-not $caught -or $global:LinkComponentTestStops -ne $before){throw 'True install failure was ignored'}
 $installer=Get-Content (Join-Path $PSScriptRoot '..\client\install-layer2.ps1') -Raw -Encoding UTF8
 if($installer -notmatch "Log \('FAILED: '\+\`$_.Exception.Message\);throw"){throw 'Inner installer must propagate exceptions'}
 'PASS: null/stale native exit code cannot turn script SUCCESS into failure; real failures propagate'
}finally{
 Remove-Variable -Scope Global -Name LinkComponentTestStops -ErrorAction SilentlyContinue
 $resolved=[IO.Path]::GetFullPath($dir)
 if($resolved.StartsWith([IO.Path]::GetFullPath($env:TEMP)+'\',[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($resolved) -like 'Link-Install-Test-*'){Remove-Item -LiteralPath $resolved -Recurse -Force}
}