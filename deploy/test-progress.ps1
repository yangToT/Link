$ErrorActionPreference='Stop'
$framework=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$exe=Join-Path $env:TEMP ('Link-Progress-Test-'+[guid]::NewGuid().ToString('N')+'.exe')
try {
 & (Join-Path $framework 'csc.exe') /nologo /target:exe ('/out:'+$exe) /main:ProgressWindowTests /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'ProgressWindow.cs') (Join-Path $PSScriptRoot 'ProgressWindowTests.cs')
 if($LASTEXITCODE -ne 0){throw 'Progress test build failed'}
 & $exe
 if($LASTEXITCODE -ne 0){throw 'Progress tests failed'}
}finally{if(Test-Path -LiteralPath $exe){Remove-Item -LiteralPath $exe -Force}}
