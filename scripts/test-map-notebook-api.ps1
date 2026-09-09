param([Parameter(Mandatory=$true)][string]$GameRoot, [switch]$ThroughHost, [ValidateSet('Debug','Release')][string]$HostConfiguration='Debug')
$ErrorActionPreference='Stop'
$repoRoot=Split-Path $PSScriptRoot -Parent
$package=Join-Path $repoRoot 'LauncherGo.Services/EmbeddedMods/ServerMapMod/bin/Release/servermap.zip'
$fixture=Join-Path $PSScriptRoot 'test-fixtures/MapNotebook/bin/Release/net10.0'
if(!(Test-Path -LiteralPath (Join-Path $fixture 'MapNotebook.dll'))){throw 'Build the map mod and MapNotebook test fixture first.'}
$dataRoot=Join-Path ([IO.Path]::GetTempPath()) ('launchergo-map-notebook-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $dataRoot 'Mods'),(Join-Path $dataRoot 'ServerMap') | Out-Null
Copy-Item -LiteralPath $package -Destination (Join-Path $dataRoot 'Mods/servermap.zip')
$fixtureTarget=Join-Path $dataRoot 'Mods/mapnotebooktestfixture'
New-Item -ItemType Directory -Path $fixtureTarget | Out-Null
Copy-Item -LiteralPath (Join-Path $fixture 'MapNotebook.dll'),(Join-Path $fixture 'modinfo.json') -Destination $fixtureTarget
function New-TestPort {
    $listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $listener.Start();$value=$listener.LocalEndpoint.Port;$listener.Stop();return $value
}
& (Join-Path $GameRoot 'VintagestoryServer.exe') --dataPath $dataRoot --genconfig
if($LASTEXITCODE -ne 0){throw 'Could not create isolated game configuration.'}
$configPath=Join-Path $dataRoot 'serverconfig.json'
$config=Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
$config.ServerName='LauncherGo isolated notebook test';$config.Ip='127.0.0.1';$config.Port=New-TestPort;$config.AdvertiseServer=$false;$config.Upnp=$false;$config.MaxClients=1;$config.MaxChunkRadius=2
$config | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $configPath
$mapPort=New-TestPort
@{Enabled=$true;BindAddress='127.0.0.1';Port=$mapPort;RenderThreads=1} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $dataRoot 'ServerMap/servermap.json')
$info=[Diagnostics.ProcessStartInfo]::new((Join-Path $GameRoot 'VintagestoryServer.exe'))
$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.WorkingDirectory=$GameRoot;$info.RedirectStandardInput=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$info.ArgumentList.Add('--dataPath');$info.ArgumentList.Add($dataRoot)
$process=[Diagnostics.Process]::Start($info)
$hostProcess=$null
$output=$process.StandardOutput.ReadToEndAsync();$errors=$process.StandardError.ReadToEndAsync()
Write-Output "Isolated notebook test data: $dataRoot; PID=$($process.Id)"
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(120);$ready=$false;$lastReport=[DateTime]::MinValue
    while([DateTime]::UtcNow -lt $deadline -and !$process.HasExited){
        $logPath=Join-Path $dataRoot 'Logs/server-main.log'
        if(Test-Path -LiteralPath $logPath){
            $log=Get-Content -LiteralPath $logPath -Raw
            if($log -match 'Map notebook test fixture ready'){$ready=$true;break}
            if(([DateTime]::UtcNow-$lastReport).TotalSeconds -gt 15){Get-Content -LiteralPath $logPath -Tail 2;$lastReport=[DateTime]::UtcNow}
        }
        Start-Sleep -Milliseconds 500
    }
    if(!$ready){throw 'Notebook fixture did not start.'}
    $env:MAP_TEST_API="http://127.0.0.1:$mapPort/api/v1"
    if($ThroughHost){
        $hostDirectory=Join-Path $dataRoot 'MapHost'
        New-Item -ItemType Directory -Path $hostDirectory | Out-Null
        $hostPort=New-TestPort
        $hostConfig=Join-Path $hostDirectory 'host.json'
        @{ListenAddress='127.0.0.1';ListenPort=$hostPort;BackendPort=$mapPort;UseHttps=$false} | ConvertTo-Json | Set-Content -LiteralPath $hostConfig
        $hostExe=Join-Path $repoRoot "LauncherGo.ServerMapHost/bin/$HostConfiguration/net10.0/LauncherGo.ServerMapHost.exe"
        $hostInfo=[Diagnostics.ProcessStartInfo]::new($hostExe)
        $hostInfo.UseShellExecute=$false;$hostInfo.CreateNoWindow=$true;$hostInfo.RedirectStandardOutput=$true;$hostInfo.RedirectStandardError=$true
        $hostInfo.ArgumentList.Add('--config');$hostInfo.ArgumentList.Add($hostConfig)
        $hostProcess=[Diagnostics.Process]::Start($hostInfo)
        $hostOutput=$hostProcess.StandardOutput.ReadToEndAsync();$hostErrors=$hostProcess.StandardError.ReadToEndAsync()
        $env:MAP_TEST_API="http://127.0.0.1:$hostPort/api/v1"
        $hostReady=$false
        for($i=0;$i -lt 60 -and !$hostProcess.HasExited;$i++){
            try { $null=Invoke-WebRequest -Uri "$env:MAP_TEST_API/auth/me" -TimeoutSec 2; $hostReady=$true; break } catch { Start-Sleep -Milliseconds 250 }
        }
        if(!$hostReady){throw 'Isolated map Host did not start.'}
        Write-Output "Testing through Map Host on $hostPort; PID=$($hostProcess.Id)"
    }
    $worldRoot=Get-ChildItem -LiteralPath (Join-Path $dataRoot 'ServerMap') -Directory | Select-Object -First 1
    $env:MAP_TEST_CONTROL=Join-Path $worldRoot.FullName 'revoke-admin.test'
    & node (Join-Path $PSScriptRoot 'test-map-notebook-api.cjs')
    if($LASTEXITCODE -ne 0){throw 'Notebook API assertions failed.'}
    $process.StandardInput.WriteLine('/stop')
    if(!$process.WaitForExit(20000)){throw 'Isolated server failed to stop.'}
} finally {
    if($hostProcess){
        if(!$hostProcess.HasExited){$hostProcess.Kill($true);$hostProcess.WaitForExit()}
        $hostOutput.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $dataRoot 'host-stdout.txt')
        $hostErrors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $dataRoot 'host-stderr.txt')
        $hostProcess.Dispose()
    }
    if(!$process.HasExited){$process.Kill($true);$process.WaitForExit()}
    $output.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $dataRoot 'stdout.txt')
    $errors.GetAwaiter().GetResult() | Set-Content -LiteralPath (Join-Path $dataRoot 'stderr.txt')
    $process.Dispose();Write-Output "Test logs retained: $dataRoot"
}
