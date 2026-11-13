param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('pointrace-battle-adapter-' + [Guid]::NewGuid().ToString('N'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$protobuf = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed\protobuf-net.dll'
$expectedProtobuf = 'e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b'
$pbdata = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed\pbdata.dll'
$expectedPbdata = '905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab'
$ionic = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed\Ionic.Zlib.dll'
$expectedIonic = '799f6b9630886132564f961031f2908263e55b104e712009eef0d4bfceb80a30'
$managed = @('Protocol', 'SharedDevelopmentContext', 'DailyRefreshGate', 'DailyRefreshProof',
    'PlayerStateRefreshModel', 'DailyPlayerSnapshot', 'GuildWire', 'BattleLifecycle', 'BattleSettlement', 'BattleReplyPreflight', 'NativeBattleStartStore', 'DailyPointRaceModel')
$sources = @($managed | ForEach-Object { Join-Path $bridge "managed/$_.cs" }) + @(
    (Join-Path $PSScriptRoot 'BattleModeAdapterTestStubs.cs'),
    (Join-Path $PSScriptRoot 'BattleModeAdapterLaunchStub.cs'),
    (Join-Path $PSScriptRoot 'PointRaceBattleAdapterTests.cs'),
    (Join-Path $PSScriptRoot 'PointRaceEntryTimeoutTests.cs'),
    (Join-Path $PSScriptRoot 'BattleReplyPreflightWire.cs'),
    (Join-Path $PSScriptRoot 'PointRaceReplyPreflightTests.cs')
)
$research = Join-Path (Split-Path -Parent (Split-Path -Parent $bridge)) 'research/battle-skip'
$evidence = @('PointraceUI.cs.txt', 'BattleUI.cs.txt', 'game.net.tcp_client.cs.txt' | ForEach-Object { Join-Path $research $_ })
$manifest = @(@($sources + $evidence + $PSCommandPath + $pbdata + $protobuf + $ionic) | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetFullPath($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
if ((Get-FileHash -LiteralPath $protobuf -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedProtobuf) {
    throw 'Offline protobuf dependency hash differs from the pinned existing fixture dependency'
}
if ((Get-FileHash -LiteralPath $pbdata -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedPbdata) { throw 'Pinned pbdata protocol schema hash differs' }
if ((Get-FileHash -LiteralPath $ionic -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedIonic) { throw 'Pinned Ionic.Zlib hash differs' }
Copy-Item -LiteralPath $protobuf -Destination (Join-Path $output 'protobuf-net.dll') -Force
Copy-Item -LiteralPath $pbdata -Destination (Join-Path $output 'pbdata.dll') -Force
Copy-Item -LiteralPath $ionic -Destination (Join-Path $output 'Ionic.Zlib.dll') -Force
$exe = Join-Path $output 'PointRaceBattleAdapterTests.exe'
$compile = @(& $csc /nologo /noconfig /target:exe /define:JSB_NATIVE_BATTLE_STORE_TEST /main:PointRaceBattleAdapterTests /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 `
    /reference:System.Xml.dll "/reference:$protobuf" "/reference:$pbdata" "/reference:$ionic" "/out:$exe" @sources 2>&1)
$compileExit = $LASTEXITCODE
[IO.File]::WriteAllLines((Join-Path $output 'compile.log'), [string[]]$compile)
if ($compileExit -ne 0) {
    $compile | Write-Output
    Write-Output "PointRace adapter artifacts: $output"
    throw "Production PointRace adapter fixture compilation failed: $compileExit"
}
$result = @(& $exe 2>&1)
$testExit = $LASTEXITCODE
$result | Set-Content -LiteralPath (Join-Path $output 'results.jsonl') -Encoding utf8
$result | Write-Output
$stable = $true
foreach ($entry in $manifest) {
    if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $stable = $false }
}
[ordered]@{
    status = $(if ($testExit -eq 0 -and $stable) { 'PASS' } else { 'FAIL' })
    compile_exit = $compileExit; test_exit = $testExit; source_hashes_stable = $stable
    game_access = $false; network_access = $false; dependency_sha256 = $expectedProtobuf
    pbdata_sha256 = $expectedPbdata
    ionic_sha256 = $expectedIonic
    source_manifest = (Join-Path $output 'sources.json'); results = (Join-Path $output 'results.jsonl')
    limitations = 'Detached fake Unity/client and instrumented BattleLaunch substitute, with pinned real pbdata/protobuf/Ionic and constructed compressed reports. Entry/return use source-derived fake stock methods; timeout uses injected TickCount baseline. Real named-event/mutex IPC, guard race/cancellation, real success field availability, live scene, paid branch, free-refresh 0x467, rendering, server cost, and business acceptance are not tested.'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "PointRace adapter artifacts: $output"
if ($testExit -ne 0) { throw "Production PointRace adapter tests failed: $testExit" }
if (-not $stable) { throw 'Production sources changed during the test run; prepare a fresh run' }
