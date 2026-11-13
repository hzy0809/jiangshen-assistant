param([string]$OutputDirectory = '', [string]$ArenaSource = '', [string]$PointSource = '', [string]$DemonSource = '', [string]$Scenario = 'suite', [switch]$ModesOnly, [switch]$LegacyStopApi)
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('normal-stop-reply-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$installed = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed'
$pins = [ordered]@{
    'pbdata.dll' = '905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab'
    'protobuf-net.dll' = 'e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b'
    'Ionic.Zlib.dll' = '799f6b9630886132564f961031f2908263e55b104e712009eef0d4bfceb80a30'
}
$dependencies = @(); $references = @()
foreach ($name in $pins.Keys) {
    $path = Join-Path $installed $name
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pins[$name]) { throw "Pinned dependency drift: $name" }
    $dependencies += $path; $references += "/reference:$path"
    Copy-Item -LiteralPath $path -Destination (Join-Path $output $name) -Force
}
$common = @('Protocol','SharedDevelopmentContext','DailyRefreshGate','DailyRefreshProof','PlayerStateRefreshModel','DailyPlayerSnapshot','BattleReplyPreflight','BattleLifecycle','BattleLaunch') |
    ForEach-Object { Join-Path $bridge "managed/$_.cs" }
if (-not $ArenaSource) { $ArenaSource = Join-Path $bridge 'managed/DailyArenaBattleModel.cs' }
if (-not $PointSource) { $PointSource = Join-Path $bridge 'managed/DailyPointRaceModel.cs' }
if (-not $DemonSource) { $DemonSource = Join-Path $bridge 'managed/DailyMonsterModel.cs' }
$modeSources = @($common) + @($ArenaSource, $PointSource, (Join-Path $bridge 'managed/BattleSettlement.cs'), (Join-Path $bridge 'managed/GuildWire.cs'),
    (Join-Path $PSScriptRoot 'BattleModeAdapterTestStubs.cs'), (Join-Path $PSScriptRoot 'BattleReplyPreflightWire.cs'), (Join-Path $PSScriptRoot 'NormalStopModeReplyTests.cs'))
$demonSources = @($common) + @($DemonSource, (Join-Path $PSScriptRoot 'DemonPreflightStubs.cs'), (Join-Path $PSScriptRoot 'NormalStopDemonReplyTests.cs'))
$inputs = @($modeSources)
if (-not $ModesOnly) { $inputs += $demonSources }
$project = Split-Path -Parent (Split-Path -Parent $bridge)
$evidence = @('native/unity_bridge/managed/Lifecycle.cs','native/unity_bridge/managed/Probe.cs','native/unity_bridge/managed/DailyScriptAdapter.cs',
    'research/battle-skip/ArenaUI.cs.txt','research/battle-skip/PointraceUI.cs.txt','research/battle-skip/DemonScript.cs.txt','research/battle-skip/BattleUI.cs.txt','research/battle-skip/game.net.tcp_client.cs.txt') |
    ForEach-Object { Join-Path $project $_ }
$manifest = @($inputs + $dependencies + $evidence + @($PSCommandPath) | Select-Object -Unique | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()} })
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$modeExe = Join-Path $output 'NormalStopModeReplyTests.exe'
$modeDefines = @(); if ($LegacyStopApi) { $modeDefines += '/define:LEGACY_STOP_API' }
$compile = @(& $csc /nologo /noconfig /target:exe /platform:x86 /main:NormalStopModeReplyTests /warn:4 /warnaserror+ /nowarn:0649 /reference:System.Xml.dll @modeDefines @references "/out:$modeExe" @modeSources 2>&1)
$compile | Set-Content -LiteralPath (Join-Path $output 'mode-compile.log') -Encoding utf8
if ($LASTEXITCODE -ne 0) { $compile | Write-Output; throw 'Real-launch mode fixture compile failed' }
if (-not $ModesOnly) {
    $demonExe = Join-Path $output 'NormalStopDemonReplyTests.exe'
    $compile = @(& $csc /nologo /noconfig /target:exe /platform:x86 /define:REAL_BATTLE_LAUNCH /main:NormalStopDemonReplyTests /warn:4 /warnaserror+ /nowarn:0649 /reference:System.Xml.dll @references "/out:$demonExe" @demonSources 2>&1)
    $compile | Set-Content -LiteralPath (Join-Path $output 'demon-compile.log') -Encoding utf8
    if ($LASTEXITCODE -ne 0) { $compile | Write-Output; throw 'Real-launch demon fixture compile failed' }
}
$scenarios = @($Scenario)
if ($Scenario -eq 'suite') {
    $scenarios = @('after-win','after-loss','during-win','during-loss','after-reply','abnormal-stop','bad-report','wrong-target','premium','timeout',
        'role-object','role-id','target-object','target-id','target-index','socket','transport-stream','client','loader','root','source-root','handler','handler-callback','handler-table','handler-arg','arg',
        'cycle','unknown-before-stop','new-epoch-blocked','retired-wrapper')
}
$failed = 0; $checks = 0; $cases = 0
foreach ($mode in @('arena','point')) {
    foreach ($case in $scenarios) {
        $result = @(& $modeExe $mode $case 2>&1); $caseExit = $LASTEXITCODE; $cases++
        $result | Set-Content -LiteralPath (Join-Path $output "$mode-$case.log") -Encoding utf8
        if ($caseExit -ne 0) { $failed++; Write-Output "$mode $case FAIL" } else { $checks += [int](($result[-1] | ConvertFrom-Json).checks) }
    }
}
if (-not $ModesOnly) {
    foreach ($case in $scenarios | Where-Object { $_ -ne 'target-index' -and $_ -ne 'retired-wrapper' }) {
        $result = @(& $demonExe $case 2>&1); $caseExit = $LASTEXITCODE; $cases++
        $result | Set-Content -LiteralPath (Join-Path $output "demon-$case.log") -Encoding utf8
        if ($caseExit -ne 0) { $failed++; Write-Output "demon $case FAIL" } else { $checks += [int](($result[-1] | ConvertFrom-Json).checks) }
    }
}
$stable = $true
foreach ($entry in $manifest) { if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $stable = $false } }
[ordered]@{status=$(if ($failed -eq 0 -and $stable) {'PASS'}else{'FAIL'});scenarios=$cases;failed=$failed;checks=$checks;source_hashes_stable=$stable;
    real_battle_launch=$true;real_named_ipc=$true;pinned_dependencies=$pins;game_access=$false;network_access=$false;
    limits='Production models/guard/helpers and named event/mutex are real; stock callbacks, Unity scenes, dispatch transport and manual presentation are detached fixtures. No live game, physical UI, deployment, server cost or business acceptance.'} |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "Normal stop reply artifacts: $output; cases=$cases; failed=$failed; checks=$checks; stable=$stable"
if ($failed -ne 0 -or -not $stable) { throw 'Normal stop reply suite failed; see per-case evidence' }
