$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dependency-license-assets.ps1')
function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw "$Message : expected $Expected; got $Actual" }
}
$assets = @{
    libraries = @{}
    targets = @{ 'net10.0' = @{}; 'net10.0/win-x64' = @{} }
}
function Add-Package([string]$Name, [hashtable]$Value, [string]$Target = 'net10.0') {
    $assets.libraries[$Name] = @{ type = 'package' }
    $assets.targets[$Target][$Name] = $Value
}
Add-Package 'Microsoft.NET.ILLink.Tasks/10.0.11' @{ build = @{ 'build/Microsoft.NET.ILLink.Tasks.props' = @{} } }
Add-Package 'CompileOnly/1.0.0' @{ compile = @{ 'ref/net10.0/Test.dll' = @{} } }
Add-Package 'Placeholder/1.0.0' @{ runtime = @{ 'lib/net10.0/_._' = @{} } }
Add-Package 'Newtonsoft.Json/13.0.3' @{ runtime = @{ 'lib/net6.0/Newtonsoft.Json.dll' = @{} } }
Assert-Equal ((Get-ReleaseLicensePackages $assets) -join ',') 'Newtonsoft.Json/13.0.3' 'Build-only SDK packages must not require release inventory entries'

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$testRoot = Join-Path $tempBase ('launchergo-license-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot | Out-Null
try {
    $fixture = Join-Path $testRoot 'project.assets.json'
    $assets | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $fixture -Encoding utf8NoBOM
    & (Join-Path $PSScriptRoot 'verify-dependency-licenses.ps1') -AssetsFile $fixture
    foreach ($kind in @('runtime','native','runtimeTargets','resource','contentFiles')) {
        $name = "Unlisted-$kind/1.0.0"
        Add-Package $name @{ $kind = @{ 'runtimes/win-x64/lib/test.bin' = @{} } } 'net10.0/win-x64'
        $assets | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $fixture -Encoding utf8NoBOM
        $failed = $false
        try { & (Join-Path $PSScriptRoot 'verify-dependency-licenses.ps1') -AssetsFile $fixture }
        catch { if ($_.Exception.Message -notlike "*License inventory needs updating: $name*") { throw }; $failed = $true }
        Assert-Equal $failed $true "Unlisted $kind assets must still block releases"
        [void]$assets.libraries.Remove($name)
        [void]$assets.targets['net10.0/win-x64'].Remove($name)
    }
    # The tool name itself is not an exemption if it ever supplies runtime code.
    $assets.targets['net10.0']['Microsoft.NET.ILLink.Tasks/10.0.11'].runtime = @{ 'lib/net10.0/ILLink.dll' = @{} }
    Assert-Equal (@(Get-ReleaseLicensePackages $assets) -contains 'Microsoft.NET.ILLink.Tasks/10.0.11') $true 'Runtime assets must not be skipped by package name'
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if (!$resolved.StartsWith([IO.Path]::TrimEndingDirectorySeparator($tempBase) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'launchergo-license-test-*') { throw 'Unsafe test cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

$mods = @(
    @{ Id = 'launchergoauth'; Folder = 'launchergoauth'; Source = 'LauncherGo.Services/ServerAuthService.cs'; Symbol = 'AuthModVersion' },
    @{ Id = 'launchergoredirect'; Folder = 'launchergoredirect'; Source = 'LauncherGo.Services/GatewayRedirectModService.cs'; Symbol = 'ModVersion' },
    @{ Id = 'launchergoserverbridge'; Folder = 'launchergoserverbridge'; Source = 'LauncherGo.Services/ServerBridgeService.cs'; Symbol = 'ModVersion' },
    @{ Id = 'servermap'; Folder = 'ServerMapMod'; Source = 'LauncherGo.Services/EmbeddedMods/ServerMapMod/src/Web/ServerMapWebServer.cs'; Symbol = 'serverMapVersion' }
)
foreach ($mod in $mods) {
    $info = Get-Content -LiteralPath (Join-Path $repository "LauncherGo.Services/EmbeddedMods/$($mod.Folder)/modinfo.json") -Raw | ConvertFrom-Json
    Assert-Equal $info.modid $mod.Id 'Mod identity'
    if ($info.version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid release version: $($mod.Id) $($info.version)" }
    $source = Get-Content -LiteralPath (Join-Path $repository $mod.Source) -Raw
    $match = [regex]::Match($source, $mod.Symbol + '\s*=\s*"([^"]+)"')
    Assert-Equal $match.Groups[1].Value $info.version "Deployment/API version for $($mod.Id)"
    if ($mod.Id -eq 'launchergoserverbridge') {
        $bridge = Get-Content -LiteralPath (Join-Path $repository 'LauncherGo.Services/EmbeddedMods/LauncherGoServerBridgeMod/LauncherGoServerBridgeModSystem.cs') -Raw
        Assert-Equal ([regex]::Match($bridge, 'BridgeVersion\s*=\s*"([^"]+)"').Groups[1].Value) $info.version 'Bridge heartbeat version'
    }
}
Write-Output 'PASS release metadata: build-only SDK tools, runtime/native/content inventory enforcement, and all four embedded mod versions.'
