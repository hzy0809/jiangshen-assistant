param([string]$OutputDirectory = '', [string]$Scenario = 'all', [string]$SettlementSourceFile = '')
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('battle-lifecycle-' + [Guid]::NewGuid().ToString('N'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$protobuf = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed\protobuf-net.dll'
$expectedProtobuf = 'e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b'
$pbdata = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed\pbdata.dll'
$expectedPbdata = '905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab'
$managed = @('Protocol', 'SharedDevelopmentContext', 'DailyRefreshGate', 'DailyRefreshProof',
    'PlayerStateRefreshModel', 'DailyPlayerSnapshot', 'GuildWire', 'BattleLifecycle', 'BattleSettlement', 'BattleReplyPreflight', 'NativeBattleStartStore')
$sources = @($managed | ForEach-Object {
    if ($_ -eq 'BattleSettlement' -and $SettlementSourceFile) { [IO.Path]::GetFullPath($SettlementSourceFile) }
    else { Join-Path $bridge "managed/$_.cs" }
}) + @(
    (Join-Path $PSScriptRoot 'BattleLifecycleTestStubs.cs'),
    (Join-Path $PSScriptRoot 'BattleLifecycleTests.cs'),
    (Join-Path $PSScriptRoot 'BattleSettlementDurableTests.cs'),
    (Join-Path $PSScriptRoot 'BattleSettlementPreflightTests.cs')
)
$manifest = @(@($sources + $pbdata + $protobuf + $PSCommandPath) | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetFullPath($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
if ((Get-FileHash -LiteralPath $protobuf -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedProtobuf) {
    throw 'Offline protobuf dependency hash differs from the pinned existing Campaign fixture dependency'
}
Copy-Item -LiteralPath $protobuf -Destination (Join-Path $output 'protobuf-net.dll') -Force
if ((Get-FileHash -LiteralPath $pbdata -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedPbdata) { throw 'Pinned real pbdata differs from settlement schema' }
Copy-Item -LiteralPath $pbdata -Destination (Join-Path $output 'pbdata.dll') -Force
$exe = Join-Path $output 'BattleLifecycleTests.exe'
$compile = @(& $csc /nologo /noconfig /target:exe /define:JSB_NATIVE_BATTLE_STORE_TEST /main:BattleLifecycleTests /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 `
    /reference:System.Xml.dll "/reference:$protobuf" "/reference:$pbdata" "/out:$exe" @sources 2>&1)
$compileExit = $LASTEXITCODE
$compile | Set-Content -LiteralPath (Join-Path $output 'compile.log') -Encoding utf8
if ($compileExit -ne 0) {
    $compile | Write-Output
    Write-Output "Battle lifecycle artifacts: $output"
    throw "Production common battle lifecycle fixture compilation failed: $compileExit"
}
$result = @(& $exe $Scenario 2>&1)
$testExit = $LASTEXITCODE
$result | Set-Content -LiteralPath (Join-Path $output 'results.jsonl') -Encoding utf8
$result | Write-Output
$stable = $true
foreach ($entry in $manifest) {
    if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $stable = $false }
}
[ordered]@{
    status = $(if ($testExit -eq 0 -and $stable) { 'PASS' } else { 'FAIL' })
    compile_exit = $compileExit
    test_exit = $testExit
    source_hashes_stable = $stable
    scenario = $Scenario
    game_access = $false
    network_access = $false
    protobuf_sha256 = $expectedProtobuf
    pbdata_sha256 = $expectedPbdata
    limitations = 'Production settlement/lifecycle/transport and real pinned pbdata/protobuf; detached fake Unity/client/stock callback and launch guard. Constructed reports, no real IPC guard, game, renderer, server, deployment or business acceptance. Embedded objects do not preserve original wire presence.'
    source_manifest = (Join-Path $output 'sources.json')
    results = (Join-Path $output 'results.jsonl')
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "Battle lifecycle artifacts: $output"
if ($testExit -ne 0) { throw "Production common battle lifecycle tests failed: $testExit" }
if (-not $stable) { throw 'Production sources changed during the test run; prepare a fresh run' }
