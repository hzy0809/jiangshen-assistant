param([string]$OutputDirectory = '', [string]$ModelSource = '', [string]$Scenario = 'suite')
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('DemonPreflight-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not $ModelSource) { $ModelSource = Join-Path $bridge 'managed/DailyMonsterModel.cs' }
$dependencies = [ordered]@{
    'pbdata.dll' = '905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab'
    'protobuf-net.dll' = 'e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b'
    'Ionic.Zlib.dll' = '799f6b9630886132564f961031f2908263e55b104e712009eef0d4bfceb80a30'
}
$installed = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed'
$references = @()
foreach ($name in $dependencies.Keys) {
    $path = Join-Path $installed $name
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $dependencies[$name]) { throw "Pinned client dependency drift: $name" }
    Copy-Item -LiteralPath $path -Destination (Join-Path $output $name) -Force
    $references += "/reference:$path"
}
$sources = @($ModelSource) + @('Protocol','SharedDevelopmentContext','DailyRefreshGate','DailyRefreshProof','PlayerStateRefreshModel','DailyPlayerSnapshot','BattleReplyPreflight' | ForEach-Object { Join-Path $bridge "managed/$_.cs" }) + @(
    (Join-Path $PSScriptRoot 'DemonPreflightStubs.cs'), (Join-Path $PSScriptRoot 'DemonPreflightTests.cs'))
$evidence = @('research/battle-skip/DemonScript.cs.txt','research/battle-skip/BattleReportInterpreter2.cs.txt',
    'research/battle-skip/BattleUI.cs.txt','research/battle-skip/game.net.tcp_base.cs.txt',
    'research/battle-lifecycle/SCDemonBattle_0x492.cs.txt','data/campaign-research/game.net.data.StoC.BattleReport.cs.txt') |
    ForEach-Object { Join-Path (Split-Path -Parent (Split-Path -Parent $bridge)) $_ }
$manifest = @($sources + $evidence + @($PSCommandPath) | ForEach-Object { [ordered]@{
    path = [IO.Path]::GetFullPath($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
Copy-Item -LiteralPath $ModelSource -Destination (Join-Path $output 'DemonPreflight-source.cs') -Force
$exe = Join-Path $output 'DemonPreflightTests.exe'
$compile = @(& C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /noconfig /target:exe /platform:x86 /main:DemonPreflightTests /warn:4 /warnaserror+ /nowarn:0649 /reference:System.Xml.dll @references "/out:$exe" @sources 2>&1)
$compileExit = $LASTEXITCODE
$compile | Set-Content -LiteralPath (Join-Path $output 'compile.log') -Encoding utf8
if ($compileExit -ne 0) { $compile | Write-Output; throw "Demon fixture compilation failed: $compileExit" }
$scenarios = @($Scenario)
if ($Scenario -eq 'suite') {
    $scenarios = @('fixed','win','loss','draw','loss-default-win','wrong-loss','missing-status','missing-left-army',
        'missing-right-army','missing-army-required','invalid-status','malformed-report','truncated-deflate','trailing-deflate','empty-inflate',
        'truncated-protobuf','inflate-limit','missing-result','missing-report','missing-cooldown','missing-bonuses',
        'duplicate-result','duplicate-win','invalid-win-bool','invalid-result-uint32','bad-cooldown-id','duplicate-cooldown-id','invalid-cooldown-bool',
        'unknown-result','unsafe-rejection-one','rejection-bad-report','rejection-inconsistent-report','empty-packet','packet-limit','timeout','timeout-cleanup','stop-cleanup',
        'duplicate','rejection','post-substitution','post-proof-mutation','nonzero-cursor','input-limit-before-copy','boundary-during-decode') + @(
        'role-object','role-id','target-object','source-target','target-mesh','clear-time','source-root','friend-mode',
        'handler','handler-callback','handler-table','socket','transport-stream','disconnected','clock-cycle',
        'gold','bound-gold','gold-consume','launch-missing' | ForEach-Object { 'boundary-' + $_ })
}
$results = @(); $testExit = 0; $checks = 0
foreach ($case in $scenarios) {
    $result = @(& $exe $output $case 2>&1)
    $caseExit = $LASTEXITCODE
    $result | Set-Content -LiteralPath (Join-Path $output ("$case.log")) -Encoding utf8
    if ($caseExit -ne 0) { $testExit = 1 }
    else { $checks += [int](($result[-1] | ConvertFrom-Json).checks) }
    $results += [ordered]@{ scenario=$case; exit=$caseExit; output=($result -join "`n") }
    Write-Output "$case exit=$caseExit"
}
$results | ForEach-Object { $_ | ConvertTo-Json -Compress } | Set-Content -LiteralPath (Join-Path $output 'results.jsonl') -Encoding utf8
$stable = $true
foreach ($entry in $manifest) { if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $stable = $false } }
[ordered]@{
    status = $(if ($testExit -eq 0 -and $stable) { 'PASS' } else { 'FAIL' }); scenario=$Scenario; compile_exit=$compileExit; test_exit=$testExit;
    source_hashes_stable=$stable; model_sha256=$manifest[0].sha256; scenario_count=$scenarios.Count; checks=$checks; dependencies=$dependencies; production_full_model=$true; production_helpers_reader_transport_clock=$true;
    actual_protobuf_and_deflate=$true; game_access=$false; network_access=$false;
    limitations='Detached Unity, stock presentation callback, Model scene inventory, BattleLaunch and BattleSettlement substitutes. Pinned protobuf and deflate code are real; no live game, rendering, IPC guard, physical interaction, deployment, or business acceptance.'
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "Demon preflight artifacts: $output"
if ($testExit -ne 0 -or -not $stable) { throw 'Demon preflight check failed; see evidence artifacts' }
