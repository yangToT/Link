param([switch]$RemoveIdentity)
# Run in an elevated PowerShell. Only Link-owned services, rules and files are affected.
$ErrorActionPreference = 'Stop'
$service = Get-Service -Name LinkAgent -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name LinkAgent }
    & sc.exe delete LinkAgent | Out-Null
}
Get-NetFirewallRule -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Link-Map-*' -or $_.DisplayName -like 'Link-Service-*' } | Remove-NetFirewallRule
$data = [IO.Path]::GetFullPath((Join-Path $env:ProgramData 'Link'))
$program = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Link'))
if ($data -ne ([IO.Path]::GetFullPath($env:ProgramData).TrimEnd('\') + '\Link')) { throw 'Unsafe data path' }
if ($program -ne ([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\Link')) { throw 'Unsafe program path' }
if ($RemoveIdentity -and (Test-Path -LiteralPath $data)) {
    $pem = Join-Path $data 'ca.pem'
    if (Test-Path -LiteralPath $pem) {
        $text = (Get-Content -LiteralPath $pem -Raw).Replace('-----BEGIN CERTIFICATE-----','').Replace('-----END CERTIFICATE-----','')
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(,[Convert]::FromBase64String($text))
        $store = New-Object System.Security.Cryptography.X509Certificates.X509Store 'Root','LocalMachine'
        try { $store.Open('ReadWrite'); $store.Remove($cert) } finally { $store.Close() }
    }
    Remove-Item -LiteralPath $data -Recurse -Force
}
if (Test-Path -LiteralPath $program) { Remove-Item -LiteralPath $program -Recurse -Force }
Write-Output 'Link removed. Device identity is retained unless -RemoveIdentity was supplied.'
