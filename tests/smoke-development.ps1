param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$dotnetPath = (Get-Command dotnet).Source
$logDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('gateway-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $logDirectory | Out-Null
$processes = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
$logFiles = [System.Collections.Generic.List[string]]::new()

function Start-ApiProcess([string]$Project, [string]$Environment, [string[]]$ExtraArguments = @()) {
    $assemblyPath = Join-Path $root "$Project/bin/$Configuration/net10.0/$(Split-Path -Leaf $Project).dll"
    if (-not (Test-Path -LiteralPath $assemblyPath)) {
        throw "Build the solution in $Configuration first: missing $assemblyPath"
    }
    $logPrefix = Join-Path $logDirectory ([guid]::NewGuid().ToString('N'))
    $stdout = "$logPrefix.out"
    $stderr = "$logPrefix.err"
    $logFiles.Add($stdout)
    $logFiles.Add($stderr)
    $startOptions = @{
        FilePath = $dotnetPath
        ArgumentList = @("`"$assemblyPath`"", "--environment=$Environment", '--urls=http://127.0.0.1:0') + $ExtraArguments
        WorkingDirectory = Split-Path -Parent $assemblyPath
        RedirectStandardOutput = $stdout
        RedirectStandardError = $stderr
        PassThru = $true
    }
    if ($IsWindows) { $startOptions.WindowStyle = 'Hidden' }
    $process = Start-Process @startOptions
    $processes.Add($process)
    return @{ Process = $process; Stdout = $stdout; Stderr = $stderr }
}

function Wait-ApiUrl($Api) {
    $timer = [System.Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 15) {
        if ($Api.Process.HasExited) {
            throw "API exited during startup: $(Get-Content -LiteralPath $Api.Stderr -Raw)"
        }
        $output = Get-Content -LiteralPath $Api.Stdout -Raw
        if ($output -match 'Now listening on: (http://127\.0\.0\.1:\d+)') { return $Matches[1] }
        Start-Sleep -Milliseconds 100
    }
    throw "API startup timed out: $(Get-Content -LiteralPath $Api.Stdout -Raw)"
}

try {
    $services = @(
        @{ Project = 'services/User.Api'; Name = 'user'; Path = 'users'; Json = '[{"id":1,"name":"User A"},{"id":2,"name":"User B"}]' }
        @{ Project = 'services/Order.Api'; Name = 'order'; Path = 'orders'; Json = '[{"id":1,"code":"ORD001"}]' }
        @{ Project = 'services/Product.Api'; Name = 'product'; Path = 'products'; Json = '[{"id":1,"name":"Product A"}]' }
    )
    $gatewayArguments = @('--Authentication:Enabled=false', '--Observability:OtlpExporterEnabled=false')
    foreach ($service in $services) {
        $service.Url = Wait-ApiUrl (Start-ApiProcess $service.Project 'Development')
        $health = Invoke-RestMethod "$($service.Url)/health" -TimeoutSec 5
        if ($health.status -ne 'Healthy') { throw "$($service.Project): unhealthy" }
        $gatewayArguments += "--ReverseProxy:Clusters:$($service.Name)-cluster:Destinations:$($service.Name)-api:Address=$($service.Url)/"
    }
    $gatewayUrl = Wait-ApiUrl (Start-ApiProcess 'src/Core.Gateway.Api' 'Development' $gatewayArguments)
    foreach ($service in $services) {
        foreach ($url in @("$($service.Url)/api/$($service.Path)", "$gatewayUrl/api/$($service.Path)", "$gatewayUrl/$($service.Path)")) {
            try { $response = Invoke-WebRequest $url -TimeoutSec 15 }
            catch { throw "Request failed at ${url}: $_" }
            if ($response.StatusCode -ne 200 -or $response.Content -ne $service.Json) {
                throw "Unexpected contract at ${url}: $($response.Content)"
            }
        }
    }
    foreach ($path in @('/health/live', '/health/ready')) {
        $health = Invoke-RestMethod "$gatewayUrl$path" -TimeoutSec 5
        if ($health.status -ne 'Healthy') { throw "Gateway probe $path failed" }
    }
    foreach ($service in $services) {
        foreach ($environmentName in @('Production', 'Staging')) {
            $guard = Start-ApiProcess $service.Project $environmentName
            $guardTimer = [System.Diagnostics.Stopwatch]::StartNew()
            do {
                $errorOutput = Get-Content -LiteralPath $guard.Stderr -Raw
                if ($errorOutput -match 'Unhandled exception\. System.InvalidOperationException: .*Development-only mock API') { break }
                Start-Sleep -Milliseconds 100
            } while (-not $guard.Process.HasExited -and $guardTimer.Elapsed.TotalSeconds -lt 15)
            # Windows crash reporting can delay process exit after the fatal startup exception.
            $errorOutput = Get-Content -LiteralPath $guard.Stderr -Raw
            $startupOutput = Get-Content -LiteralPath $guard.Stdout -Raw
            if ($errorOutput -notmatch 'Unhandled exception\. System.InvalidOperationException: .*Development-only mock API' -or
                $startupOutput -match 'Now listening on:' -or
                ($guard.Process.HasExited -and $guard.Process.ExitCode -eq 0)) {
                throw "Missing $environmentName startup guard: $errorOutput"
            }
        }
    }
    Write-Output 'Development smoke passed: 3 APIs, 9 direct/proxy/alias contracts, 5 health endpoints, 6 non-Development startup guards.'
}
catch {
    foreach ($logFile in $logFiles) {
        if (Test-Path -LiteralPath $logFile) { Get-Content -LiteralPath $logFile | Write-Host }
    }
    throw
}
finally {
    foreach ($process in $processes) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
    foreach ($logFile in $logFiles) {
        if (Test-Path -LiteralPath $logFile) { Remove-Item -LiteralPath $logFile }
    }
    Remove-Item -LiteralPath $logDirectory
}
