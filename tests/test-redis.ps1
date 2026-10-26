param([int]$Port = 16379)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$container = 'core-gateway-test-' + [guid]::NewGuid().ToString('N')
$previousConnection = $env:GATEWAY_TEST_REDIS
try {
    docker run --detach --name $container --publish "127.0.0.1:${Port}:6379" redis:7.4-alpine | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot start isolated Redis container.' }
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        $reply = docker exec $container redis-cli ping 2>$null
        if ($LASTEXITCODE -eq 0 -and $reply -eq 'PONG') { $ready = $true; break }
        Start-Sleep -Milliseconds 200
    }
    if (-not $ready) { throw 'Redis did not become ready.' }
    $env:GATEWAY_TEST_REDIS = "127.0.0.1:$Port"
    dotnet test (Join-Path $root 'Core.Gateway.sln') --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Gateway test suite failed.' }
}
finally {
    $env:GATEWAY_TEST_REDIS = $previousConnection
    docker rm --force $container | Out-Null
}
