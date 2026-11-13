param([string]$OutputDirectory = '', [switch]$UseMono, [string]$BaselineFixture = '')
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('native-durable-battle-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$installed = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed'
$mscorlib = Join-Path $installed 'mscorlib.dll'
if ((Get-FileHash -LiteralPath $mscorlib -Algorithm SHA256).Hash.ToLowerInvariant() -ne '3f34c90ab282e8f3546946263087fb5002cf96ef9f412dd4011dab3f0f7cd8a4') { throw 'Legacy mscorlib hash drift' }
$sources = @((Join-Path $bridge 'managed/NativeBattleStartStore.cs'), (Join-Path $PSScriptRoot 'NativeBattleStartStoreTests.cs'))
$exe = Join-Path $output 'NativeBattleStartStoreTests.exe'
$compile = @(& C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /noconfig /target:exe /platform:x86 /nostdlib+ /warn:4 /warnaserror+ /define:JSB_NATIVE_BATTLE_STORE_TEST "/reference:$mscorlib" "/out:$exe" @sources 2>&1)
$compile | Set-Content -LiteralPath (Join-Path $output 'compile.log') -Encoding utf8
if ($LASTEXITCODE -ne 0) { $compile | Write-Output; throw 'Pinned legacy store compilation failed' }
'<configuration><startup useLegacyV2RuntimeActivationPolicy="true"><supportedRuntime version="v4.0" /></startup></configuration>' | Set-Content -LiteralPath ($exe + '.config') -Encoding utf8
$manifest = @($sources + $PSCommandPath + $mscorlib + (Join-Path $PSScriptRoot 'native_battle_start_store_subprocess.py') +
    (Join-Path $PSScriptRoot 'native_battle_store_anchor_regressions.py') | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
$runtimeArgs = @()
if ($UseMono) {
    $mono = Join-Path (Split-Path -Parent $installed) 'Mono/mono.dll'
    if ((Get-FileHash -LiteralPath $mono -Algorithm SHA256).Hash.ToLowerInvariant() -ne '51941db23fd29380039924d1a44964fec18ab2a80e72f14410fe0163d2cae500') { throw 'Pinned Mono runtime hash drift' }
    $project = Split-Path -Parent (Split-Path -Parent $bridge)
    $zig = Join-Path $project 'research/unity-object-layer/bridge/tools/zig-windows-x86_64-0.13.0/zig.exe'
    $hostSource = Join-Path $PSScriptRoot 'native_battle_store_mono_host.c'
    $hostExe = Join-Path $output 'native_battle_store_mono_host.exe'
    & $zig cc -target x86-windows-gnu -std=c11 -O2 -Wall -Wextra -Werror $hostSource -o $hostExe
    if ($LASTEXITCODE -ne 0) { throw 'Offline Mono host compilation failed' }
    $monoLib = Join-Path $output 'mono-lib'
    $corlibDirectory = Join-Path $monoLib 'mono/2.0'
    New-Item -ItemType Directory -Path $corlibDirectory -Force | Out-Null
    Copy-Item -LiteralPath $mscorlib -Destination (Join-Path $corlibDirectory 'mscorlib.dll') -Force
    $runtimeArgs = @('--mono-host', $hostExe, '--mono-dll', $mono, '--managed-dir', $monoLib)
    @($mono, $hostSource, $hostExe | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()} }) |
        ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'mono-sources.json') -Encoding utf8
}
try {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $results = @(& python (Join-Path $PSScriptRoot 'native_battle_start_store_subprocess.py') --exe $exe --output $output @runtimeArgs 2>&1)
    $resultExit = $LASTEXITCODE
}
finally { $ErrorActionPreference = $previousPreference }
$results | Set-Content -LiteralPath (Join-Path $output 'results.log') -Encoding utf8
$results | Write-Output
if ($resultExit -ne 0) { throw 'Native durable subprocess checks failed' }
if (-not $UseMono) {
    $baselineArgs = @(); if ($BaselineFixture) { $baselineArgs = @('--baseline-exe', [IO.Path]::GetFullPath($BaselineFixture)) }
    try {
        $previousPreference = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        $anchorResults = @(& python (Join-Path $PSScriptRoot 'native_battle_store_anchor_regressions.py') --exe $exe --output $output @baselineArgs 2>&1)
        $anchorExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previousPreference }
    $anchorResults | Set-Content -LiteralPath (Join-Path $output 'anchor-results.log') -Encoding utf8
    $anchorResults | Write-Output
    if ($anchorExit -ne 0) { throw 'Native durable anchor regression checks failed' }
}
Write-Output "Native durable store artifacts: $output"
