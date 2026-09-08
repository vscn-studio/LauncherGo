param(
    [string]$PayloadRoot,
    [string]$LaunchHost,
    [string]$StateRoot,
    [switch]$Gateway
)
$ErrorActionPreference = 'Stop'

# Run this branch in a short-lived parent process, then verify its Host survives.
if ($LaunchHost) {
    $arguments = @('--config', ('"' + (Join-Path $StateRoot 'host.json') + '"'),
        '--state', ('"' + (Join-Path $StateRoot 'host.state.json') + '"'))
    if ($Gateway) {
        $arguments += @('--stop-signal', ('"' + (Join-Path $StateRoot 'host.stop') + '"'),
            '--reload-signal', ('"' + (Join-Path $StateRoot 'host.reload') + '"'))
    } else {
        $arguments += @('--stop', ('"' + (Join-Path $StateRoot 'host.stop') + '"'))
    }
    $child = Start-Process -FilePath $LaunchHost -ArgumentList $arguments -WindowStyle Hidden -PassThru
    $child.Id | Set-Content -LiteralPath (Join-Path $StateRoot 'child.pid')
    exit 0
}

if (!$PayloadRoot) { throw 'Specify -PayloadRoot with a published installer or portable directory.' }
$payload = (Resolve-Path -LiteralPath $PayloadRoot).Path
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('launchergo-host-smoke-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null

foreach ($kind in @('ServerMapHost', 'GatewayHost')) {
    $directory = Join-Path $testRoot $kind
    New-Item -ItemType Directory -Path $directory | Out-Null
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $config = if ($kind -eq 'ServerMapHost') {
        @{ ListenAddress = '127.0.0.1'; ListenPort = $port; BackendPort = 1;
           WebRoot = (Join-Path $payload 'WebRoot'); UseHttps = $false }
    } else {
        @{ ListenHost = '127.0.0.1'; ListenPort = $port; ConnectTimeoutSec = 1;
           Backends = @(@{ Id = 'smoke'; Name = 'smoke'; Host = '127.0.0.1'; Port = 1; Weight = 100 }) }
    }
    $config | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory 'host.json') -Encoding UTF8
    $executable = Join-Path $payload "LauncherGo.$kind.exe"
    $parentArgs = @('-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
        '-LaunchHost', $executable, '-StateRoot', $directory)
    if ($kind -eq 'GatewayHost') { $parentArgs += '-Gateway' }
    $process = $null
    try {
        $quotedArgs = $parentArgs | ForEach-Object { '"' + $_ + '"' }
        $parent = Start-Process -FilePath "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" `
            -ArgumentList $quotedArgs -WindowStyle Hidden -PassThru
        try {
            if (!$parent.WaitForExit(10000) -or $parent.ExitCode -ne 0) { throw "Could not launch $kind." }
        } finally { $parent.Dispose() }
        $childId = Get-Content -LiteralPath (Join-Path $directory 'child.pid')
        $process = Get-Process -Id ([int]$childId)
        $null = $process.Handle
        $statePath = Join-Path $directory 'host.state.json'
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        $state = $null
        do {
            if ($process.HasExited) { throw "$kind exited unexpectedly. State: $statePath" }
            if (Test-Path -LiteralPath $statePath) {
                $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
                if ($state.isRunning) { break }
            }
            Start-Sleep -Milliseconds 150
        } while ([DateTime]::UtcNow -lt $deadline)
        if (!$state.isRunning -or $state.processId -ne $process.Id -or !$state.processStartTimeUtcTicks) {
            throw "$kind did not publish a valid running snapshot."
        }
        $heartbeat = [DateTimeOffset]::Parse($state.heartbeatUtc)
        Start-Sleep -Seconds 2
        $next = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ([DateTimeOffset]::Parse($next.heartbeatUtc) -le $heartbeat -or $process.HasExited) {
            throw "$kind did not survive its parent with a continuing heartbeat."
        }
        if ($kind -eq 'ServerMapHost') {
            $response = Invoke-WebRequest -UseBasicParsing -Uri "http://127.0.0.1:$port/" -TimeoutSec 5
            if ($response.StatusCode -ne 200) { throw 'Map web root is unavailable.' }
        }
        'stop' | Set-Content -LiteralPath (Join-Path $directory 'host.stop')
        if (!$process.WaitForExit(10000)) { throw "$kind ignored its stop signal." }
        $finalState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        if ($finalState.isRunning -or ($null -ne $process.ExitCode -and $process.ExitCode -ne 0)) {
            throw "$kind did not shut down cleanly (exit $($process.ExitCode))."
        }
        Write-Output "PASS: $kind survived parent exit, updated heartbeat, and stopped cleanly."
    }
    finally {
        if ($process) {
            if (!$process.HasExited) { $process.Kill() ; $process.WaitForExit() }
            $process.Dispose()
        }
    }
}
Write-Output "Smoke-test state retained at: $testRoot"
