param(
    [Parameter(Mandatory = $true)][string]$PayloadRoot,
    [switch]$CheckOnly
)
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$logPath = Join-Path $PayloadRoot 'dotnet-install.log'
try {
    $required = @{}
    foreach ($hostName in @('ServerHost', 'GatewayHost', 'ServerMapHost')) {
        $config = Get-Content -LiteralPath (Join-Path $PayloadRoot "LauncherGo.$hostName.runtimeconfig.json") -Raw | ConvertFrom-Json
        $frameworks = @($config.runtimeOptions.framework) + @($config.runtimeOptions.frameworks)
        foreach ($framework in $frameworks) {
            if ($null -eq $framework) { continue }
            $version = [version]$framework.version
            if ($version.Major -ne 10 -or $version.Minor -ne 0) { throw 'Only stable .NET 10.0 hosts are supported.' }
            if (!$required.ContainsKey($framework.name) -or $required[$framework.name] -lt $version) {
                $required[$framework.name] = $version
            }
        }
    }
    foreach ($name in @('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App')) {
        if (!$required.ContainsKey($name)) { throw "Missing framework requirement: $name" }
    }
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    $key = $registry.OpenSubKey('SOFTWARE\dotnet\Setup\InstalledVersions\x64')
    try { $dotnetRoot = if ($key) { $key.GetValue('InstallLocation') } else { $null } }
    finally { if ($key) { $key.Dispose() }; $registry.Dispose() }
    if (!$dotnetRoot) { $dotnetRoot = Join-Path $env:ProgramW6432 'dotnet' }
    $dotnetExe = Join-Path $dotnetRoot 'dotnet.exe'
    function Get-InstalledRuntimes {
        $installed = @{}
        if (Test-Path -LiteralPath $dotnetExe) {
            foreach ($line in @(& $dotnetExe --list-runtimes)) {
                if ($line -match '^(Microsoft\.(?:NETCore|AspNetCore)\.App) (10\.0\.\d+) \[') {
                    $name = $Matches[1]; $version = [version]$Matches[2]
                    if (!$installed.ContainsKey($name) -or $installed[$name] -lt $version) { $installed[$name] = $version }
                }
            }
        }
        return $installed
    }
    function Test-Requirements($installed) {
        foreach ($name in $required.Keys) {
            if (!$installed.ContainsKey($name) -or $installed[$name] -lt $required[$name]) { return $false }
        }
        # ASP.NET selects its latest patch; that patch may require the matching Core patch.
        return $installed['Microsoft.NETCore.App'] -ge $installed['Microsoft.AspNetCore.App']
    }
    $installed = Get-InstalledRuntimes
    if (Test-Requirements $installed) { exit 0 }
    if ($CheckOnly) { exit 1 }

    $metadata = Invoke-RestMethod -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $release = $metadata.releases | Where-Object {
        $_.runtime.version -match '^10\.0\.\d+$' -and $_.'aspnetcore-runtime'.version -eq $_.runtime.version
    } | Sort-Object { [version]$_.runtime.version } -Descending | Select-Object -First 1
    if (!$release) { throw 'No stable matching .NET 10 runtime pair found.' }
    $selected = [version]$release.runtime.version
    foreach ($version in @($required.Values) + @($installed.Values)) {
        if ($selected -lt $version) { throw "Published runtime $selected is older than required/installed $version." }
    }
    $restart = $false
    foreach ($item in @(
        @{ Name = 'Microsoft.NETCore.App'; Component = $release.runtime; Prefix = 'dotnet-runtime-' },
        @{ Name = 'Microsoft.AspNetCore.App'; Component = $release.'aspnetcore-runtime'; Prefix = 'aspnetcore-runtime-' }
    )) {
        if ($installed.ContainsKey($item.Name) -and $installed[$item.Name] -ge $selected) { continue }
        $file = $item.Component.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq ($item.Prefix + 'win-x64.exe') } | Select-Object -First 1
        if (!$file) { throw "Missing official win-x64 installer for $($item.Name)." }
        $uri = [uri]$file.url
        if ($uri.Scheme -ne 'https' -or $uri.Host -notin @('builds.dotnet.microsoft.com', 'download.visualstudio.microsoft.com')) {
            throw "Unexpected installer origin: $uri"
        }
        $target = Join-Path $PayloadRoot ($item.Prefix + "$selected-win-x64.exe")
        "Downloading $uri" | Out-File -LiteralPath $logPath -Append
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA512).Hash -ne $file.hash) { throw 'Runtime SHA512 verification failed.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $target
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(^|,\s*)O=Microsoft Corporation(,|$)') {
            throw 'Runtime Microsoft signature verification failed.'
        }
        $installer = Start-Process -FilePath $target -ArgumentList '/install', '/quiet', '/norestart' -WindowStyle Hidden -PassThru -Wait
        if ($installer.ExitCode -in @(3010, 1641)) { $restart = $true }
        elseif ($installer.ExitCode -ne 0) { throw "Runtime installer exited with $($installer.ExitCode)." }
    }
    if (!(Test-Requirements (Get-InstalledRuntimes))) { throw 'Runtime verification after installation failed.' }
    if ($restart) { exit 3010 }
    exit 0
}
catch {
    $_ | Out-String | Out-File -LiteralPath $logPath -Append
    exit 1
}
