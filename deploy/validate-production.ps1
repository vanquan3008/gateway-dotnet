param(
    [string]$KustomizePath = (Join-Path $PSScriptRoot 'kubernetes'),
    [switch]$RenderOnly
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$manifest = (kubectl kustomize $KustomizePath) -join "`n"
if ($LASTEXITCODE -ne 0) {
    throw 'Kustomize rendering failed.'
}
if (-not $RenderOnly -and $manifest -match 'gateway\.example\.com|registry\.example\.com|192\.0\.2\.0/24') {
    throw 'Replace the sample gateway domain, container image and trusted ingress CIDR before production deployment.'
}
Write-Output $manifest
