param([string]$Version = '0.2.0-alpha.4')
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    & .\client\build.ps1
    if ($LASTEXITCODE -ne 0) { throw 'Client build failed' }
    & .\deploy\build-uninstallers.ps1
    Push-Location server
    try {
        go test ./...
        if ($LASTEXITCODE -ne 0) { throw 'Server tests failed' }
        go vet ./...
        if ($LASTEXITCODE -ne 0) { throw 'Server vet failed' }
        $env:CGO_ENABLED = '0'; $env:GOARCH = 'amd64'
        foreach ($platform in @('windows','linux')) {
            $env:GOOS = $platform
            $target = if ($platform -eq 'windows') { '..\artifacts\server-windows-amd64\LinkServer.exe' } else { '..\artifacts\server-linux-amd64\link-server' }
            go build -trimpath -ldflags="-s -w -X main.version=$Version" -o $target .
            if ($LASTEXITCODE -ne 0) { throw "Server build failed: $platform" }
        }
    } finally { Remove-Item Env:GOOS,Env:GOARCH,Env:CGO_ENABLED -ErrorAction SilentlyContinue; Pop-Location }
    Write-Output "Built Link $Version for Windows amd64 and Linux amd64."
} finally { Pop-Location }
