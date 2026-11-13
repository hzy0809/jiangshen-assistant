param([string]$Zig = '', [switch]$DailyStoneOnly, [switch]$DailyMingshiOnly, [switch]$DailyArenaOnly, [switch]$DailyActivityOnly, [switch]$DailyGuildOnly, [switch]$DailySoulOnly, [switch]$DailyRefineOnly, [switch]$DailyWoodOnly)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$project = Split-Path -Parent (Split-Path -Parent $here)
$out = if ($DailyWoodOnly) { Join-Path ([IO.Path]::GetTempPath()) ('daily-wood-validation-' + [Guid]::NewGuid().ToString('N')) }
    elseif ($DailyRefineOnly) { Join-Path ([IO.Path]::GetTempPath()) ('daily-refine-validation-' + [Guid]::NewGuid().ToString('N')) }
    else { Join-Path $here 'build' }
$managedOut = Join-Path $out 'managed-tests'
New-Item -ItemType Directory -Path $managedOut -Force | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
function Invoke-DailyActivityTests {
    $dailyExe = Join-Path $managedOut 'DailyActivityTests.exe'
    & $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /reference:System.Web.Extensions.dll "/out:$dailyExe" `
        (Join-Path $here 'managed/Protocol.cs') (Join-Path $here 'managed/DailyRefreshGate.cs') (Join-Path $here 'managed/DailyActivityModel.cs') (Join-Path $here 'tests/DailyActivityTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Daily observation fixture compilation failed' }
    $dailyResult = & $dailyExe
    $dailyExit = $LASTEXITCODE
    $dailyResult | Set-Content -LiteralPath (Join-Path $out 'managed-daily.json') -Encoding utf8
    $dailyResult | Write-Output
    if ($dailyExit -ne 0) { throw "Daily observation fixture failed: $dailyExit" }
}
if ($DailyActivityOnly) { Invoke-DailyActivityTests; return }
function Invoke-DailyGuildTests {
    $guildExe = Join-Path $managedOut 'DailyGuildTests.exe'
    & $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 "/out:$guildExe" `
        (Join-Path $here 'managed/Protocol.cs') (Join-Path $here 'managed/SharedDevelopmentContext.cs') `
        (Join-Path $here 'managed/PlayerStateRefreshModel.cs') (Join-Path $here 'managed/DailyPlayerSnapshot.cs') (Join-Path $here 'managed/DailyRefreshProof.cs') `
        (Join-Path $here 'managed/DailyRefreshGate.cs') (Join-Path $here 'managed/GuildWire.cs') (Join-Path $here 'managed/DailyGuildModel.cs') (Join-Path $here 'tests/DailyGuildTests.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Daily guild fixture compilation failed' }
    $guildResult = & $guildExe
    $guildExit = $LASTEXITCODE
    $guildResult | Set-Content -LiteralPath (Join-Path $out 'managed-daily-guild.json') -Encoding utf8
    $guildResult | Write-Output
    if ($guildExit -ne 0) { throw "Daily guild fixture failed: $guildExit" }
}
if ($DailyGuildOnly) { Invoke-DailyGuildTests; return }
$stoneReceipts = [Collections.Generic.List[string]]::new()
function Invoke-DailyStoneTests {
    $stoneSources = @('Protocol','SharedDevelopmentContext','DailyRefreshGate','DailyRefreshProof','PlayerStateRefreshModel','DailyPlayerSnapshot','GuildWire','DailyStoneModel') |
        ForEach-Object { Join-Path $here "managed/$_.cs" }
    $stoneFixture = Join-Path $here 'tests/DailyStoneTests.cs'
    $stoneExe = Join-Path $managedOut 'DailyStoneTests.exe'
    & $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 /define:JSB_DAILY_STONE_TEST "/out:$stoneExe" @stoneSources $stoneFixture
    if ($LASTEXITCODE -ne 0) { throw 'Daily stone production model and callback adapter fixture compilation failed' }
    $stoneResult = & $stoneExe
    $stoneExit = $LASTEXITCODE
    $stoneResult | Set-Content -LiteralPath (Join-Path $out 'managed-daily-stone.json') -Encoding utf8
    $stoneResult | Write-Output
    if ($stoneExit -ne 0) { throw "Daily stone model and callback adapter fixture failed: $stoneExit" }
    $stoneReceipts.Add('managed-daily-stone.json')
    $stoneProbeSources = $stoneSources + @($stoneFixture, (Join-Path $here 'managed/DevelopmentContextReader.cs'),
        (Join-Path $here 'managed/Lifecycle.cs'), (Join-Path $here 'managed/ReceiptStore.cs'),
        (Join-Path $here 'managed/Probe.cs'), (Join-Path $here 'tests/LifecycleTests.cs'))
    foreach ($enabled in @($false,$true)) {
        $stoneProbeExe = Join-Path $managedOut "DailyStoneProbeTests-$enabled.exe"
        $stoneDefines = 'JSB_DAILY_STONE_TEST,JSB_STONE_PROBE_TEST,JSB_LIFECYCLE_TEST'
        if ($enabled) { $stoneDefines += ',JSB_DAILY_STONE_ENABLED' }
        & $csc /nologo /noconfig /target:exe /main:LifecycleTests /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 "/define:$stoneDefines" "/out:$stoneProbeExe" @stoneProbeSources
        if ($LASTEXITCODE -ne 0) { throw "Daily stone Probe fixture compilation failed (enabled=$enabled)" }
        $stoneEndings = if ($enabled) {
            @('pre-open-stop','open-commit-overlay','open-stop','stale-open','open-dismissed','final-stop','final-expiry',
                'dig-open-expiry','dig-commit-stream','dig-commit-disconnect','dig-commit-stop','dig-commit-heartbeat',
                'dig-commit-replace','refusal','settle',
                'stop','destroy','late-settlement','cleanup-stop','cleanup-destroy','cleanup-expiry',
                'cleanup-frame-failure','stale-rows','close-noop','close-throw','close-race','close-final-expiry','close-commit-foreign')
        } else { @('disabled') }
        foreach ($ending in $stoneEndings) {
            $stoneProbeResult = & $stoneProbeExe (Join-Path $managedOut 'stone-run') $ending
            $stoneProbeExit = $LASTEXITCODE
            $stoneReceipt = "managed-daily-stone-$ending.json"
            $stoneProbeResult | Set-Content -LiteralPath (Join-Path $out $stoneReceipt) -Encoding utf8
            $stoneProbeResult | Write-Output
            if ($stoneProbeExit -ne 0) { throw "Daily stone Probe fixture failed ($ending): $stoneProbeExit" }
            $stoneReceipts.Add($stoneReceipt)
        }
    }
}
if ($DailyStoneOnly) { Invoke-DailyStoneTests; return }
$mingshiReceipts = [Collections.Generic.List[string]]::new()
function Invoke-DailyMingshiTests {
    $sources = @('Protocol','SharedDevelopmentContext','DailyRefreshGate','DailyRefreshProof','PlayerStateRefreshModel',
        'DailyPlayerSnapshot','GuildWire','DailyTimedModel','DailyMingshiModel') | ForEach-Object { Join-Path $here "managed/$_.cs" }
    $fixture = Join-Path $here 'tests/DailyMingshiTests.cs'
    $exe = Join-Path $managedOut 'DailyMingshiTests.exe'
    & $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 /define:JSB_DAILY_MINGSHI_TEST "/out:$exe" @sources $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Daily Mingshi model and callback fixture compilation failed' }
    $receipt = 'managed-daily-mingshi.json'
    $result = & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Daily Mingshi model and callback fixture failed' }
    $result | Set-Content -LiteralPath (Join-Path $out $receipt) -Encoding utf8
    $result | Write-Output
    $mingshiReceipts.Add($receipt)
    $probeSources = $sources + @(
        'managed/DevelopmentContextReader.cs','managed/Lifecycle.cs','managed/ReceiptStore.cs','managed/Probe.cs',
        'tests/LifecycleTests.cs','tests/DailyMingshiTests.cs','tests/DailyMingshiProbeTests.cs' |
        ForEach-Object { Join-Path $here $_ }
    )
    foreach ($enabled in @($false,$true)) {
        $exe = Join-Path $managedOut "DailyMingshiProbeTests-$enabled.exe"
        $defines = 'JSB_DAILY_MINGSHI_TEST,JSB_MINGSHI_PROBE_TEST,JSB_LIFECYCLE_TEST'
        if ($enabled) { $defines += ',JSB_DAILY_MINGSHI_ENABLED' }
        & $csc /nologo /noconfig /target:exe /main:DailyMingshiProbeTests /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 "/define:$defines" "/out:$exe" @probeSources
        if ($LASTEXITCODE -ne 0) { throw "Daily Mingshi Probe fixture compilation failed (enabled=$enabled)" }
        $scenarios = if ($enabled) {
            @('pre-open-stop','pre-open-destroy','stop-open','destroy-open','stop-pending','destroy-pending','stop-settled','destroy-settled',
                'settle-capital','settle-campaign','partial-cache','destroy-closing','commit-open-stop','commit-open-wrong-chrome',
                'replace-355-opening','replace-355-open','commit-harvest-replaced-355',
                'entry-campaign-duplicate','entry-campaign-wrong-root','return-campaign-duplicate','return-campaign-wrong-root',
                'commit-harvest-same-operation','commit-harvest-same-action','commit-harvest-same-role','commit-harvest-same-revision',
                'commit-harvest-same-task','commit-harvest-same-nonce','commit-harvest-same-name','commit-harvest-higher-heartbeat',
                'commit-harvest-stop','commit-harvest-lease','commit-harvest-deadline','commit-harvest-stream','commit-harvest-connect-lost',
                'commit-harvest-gui','commit-harvest-role','commit-harvest-panel','commit-harvest-overlay','commit-harvest-quote','commit-close-stop')
        } else { @('disabled') }
        foreach ($scenario in $scenarios) {
            $receipt = "managed-daily-mingshi-$scenario.json"
            $result = & $exe $scenario
            if ($LASTEXITCODE -ne 0) { throw "Daily Mingshi Probe fixture failed ($scenario)" }
            $result | Set-Content -LiteralPath (Join-Path $out $receipt) -Encoding utf8
            $result | Write-Output
            $mingshiReceipts.Add($receipt)
        }
    }
}
if ($DailyMingshiOnly) { Invoke-DailyMingshiTests; return }
$arenaReceipts = [Collections.Generic.List[string]]::new()
function Invoke-DailyArenaTests {
    $sources = @('Protocol','SharedDevelopmentContext','DailyRefreshGate','DailyRefreshProof','PlayerStateRefreshModel',
        'DailyPlayerSnapshot','GuildWire','DailyArenaChestModel') | ForEach-Object { Join-Path $here "managed/$_.cs" }
    $fixture = Join-Path $here 'tests/DailyArenaChestTests.cs'
    $exe = Join-Path $managedOut 'DailyArenaChestTests.exe'
    & $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 /reference:System.Web.Extensions.dll "/out:$exe" @sources $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Daily arena model and callback fixture compilation failed' }
    $receipt = 'managed-daily-arena.json'
    $result = & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Daily arena model and callback fixture failed' }
    $result | Set-Content -LiteralPath (Join-Path $out $receipt) -Encoding utf8
    $result | Write-Output
    $arenaReceipts.Add($receipt)
    $probeSources = $sources + @($fixture) + @(
        'managed/DevelopmentContextReader.cs','managed/Lifecycle.cs','managed/ReceiptStore.cs','managed/Probe.cs',
        'tests/LifecycleTests.cs','tests/DailyArenaProbeTests.cs' | ForEach-Object { Join-Path $here $_ }
    )
    foreach ($enabled in @($false,$true)) {
        $exe = Join-Path $managedOut "DailyArenaProbeTests-$enabled.exe"
        $defines = 'JSB_ARENA_PROBE_TEST,JSB_LIFECYCLE_TEST'
        if ($enabled) { $defines += ',JSB_DAILY_ARENA_ENABLED' }
        & $csc /nologo /noconfig /target:exe /main:DailyArenaProbeTests /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 /reference:System.Web.Extensions.dll "/define:$defines" "/out:$exe" @probeSources
        if ($LASTEXITCODE -ne 0) { throw "Daily arena Probe fixture compilation failed (enabled=$enabled)" }
        $scenarios = if ($enabled) {
            @('paid-only','unproven-clock','wrong-category','reply-first','push-first','cache-mismatch','pending-callback','loader-stop','loader-destroy',
                'partial-ui-stop','partial-ui-destroy','chest-disable-loss','business-before-arena','stop-sample','higher-sequence','same-action','same-operation',
                'same-role','same-revision','same-name','wrong-nonce','expired-lease','expired-gui','competing-navigation',
                'disconnect','swap-stream','read-closed','write-closed','gui-after-read','stop-after-read')
        } else { @('disabled') }
        foreach ($scenario in $scenarios) {
            $receipt = "managed-daily-arena-$scenario.json"
            $result = & $exe $scenario
            if ($LASTEXITCODE -ne 0) { throw "Daily arena Probe fixture failed ($scenario)" }
            $result | Set-Content -LiteralPath (Join-Path $out $receipt) -Encoding utf8
            $result | Write-Output
            $arenaReceipts.Add($receipt)
        }
    }
}
if ($DailyArenaOnly) { Invoke-DailyArenaTests; return }
$soulReceipts = [Collections.Generic.List[string]]::new()
function Invoke-DailySoulTests {
    $sources = @('Protocol','SharedDevelopmentContext','DailyRefreshGate','DailyRefreshProof','PlayerStateRefreshModel',
        'DailyPlayerSnapshot','GuildWire','DailySoulModel') | ForEach-Object { Join-Path $here "managed/$_.cs" }
    $fixture = Join-Path $here 'tests/DailySoulTests.cs'
    $exe = Join-Path $managedOut 'DailySoulTests.exe'
    & $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 /define:JSB_DAILY_SOUL_TEST "/out:$exe" @sources $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Daily Soul model and callback fixture compilation failed' }
    $receipt = 'managed-daily-soul.json'
    $result = & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Daily Soul model and callback fixture failed' }
    $result | Set-Content -LiteralPath (Join-Path $out $receipt) -Encoding utf8
    $result | Write-Output
    $soulReceipts.Add($receipt)
    $probeSources = $sources + @($fixture) + @(
        'managed/DevelopmentContextReader.cs','managed/Lifecycle.cs','managed/ReceiptStore.cs','managed/Probe.cs',
        'tests/LifecycleTests.cs','tests/DailySoulProbeTests.cs' | ForEach-Object { Join-Path $here $_ }
    )
    foreach ($enabled in @($false,$true)) {
        $exe = Join-Path $managedOut "DailySoulProbeTests-$enabled.exe"
        $defines = 'JSB_DAILY_SOUL_TEST,JSB_SOUL_PROBE_TEST,JSB_LIFECYCLE_TEST'
        if ($enabled) { $defines += ',JSB_DAILY_SOUL_ENABLED' }
        & $csc /nologo /noconfig /target:exe /main:DailySoulProbeTests /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 /reference:System.Web.Extensions.dll "/define:$defines" "/out:$exe" @probeSources
        if ($LASTEXITCODE -ne 0) { throw "Daily Soul Probe fixture compilation failed (enabled=$enabled)" }
        $scenarios = if ($enabled) {
            @('readiness','root-unavailable','no-game-state','invalid-projection','progression','one-free','commit-stop','commit-lease','commit-deadline','commit-name','commit-sequence','commit-same-sequence-stop',
                'commit-nonce','commit-action','commit-operation','commit-role-field','commit-task','commit-revision','commit-role','commit-reset',
                'commit-cycle','commit-socket','commit-stream','commit-last-deadline','commit-last-lease','commit-last-stream',
                'pre-gui','noop','late-start','late-harvest','start-timeout','harvest-timeout','cleanup-timeout','cleanup-final-deadline',
                'refused-harvest','refused-close','queued-stable','queued-quota','queued-role','queued-cycle','destroy-open','destroy-pending',
                'original-throws','binding-355','binding-life','binding-arg','binding-target','binding-harvest-arg','binding-harvest-life')
        } else { @('disabled','disabled-no-game-state') }
        foreach ($scenario in $scenarios) {
            $receipt = "managed-daily-soul-$scenario.json"
            $result = & $exe $scenario
            if ($LASTEXITCODE -ne 0) { throw "Daily Soul Probe fixture failed ($scenario)" }
            $result | Set-Content -LiteralPath (Join-Path $out $receipt) -Encoding utf8
            $result | Write-Output
            $soulReceipts.Add($receipt)
        }
    }
}
if ($DailySoulOnly) { Invoke-DailySoulTests; return }
$refineReceipts = [Collections.Generic.List[string]]::new()
function Invoke-DailyRefineTests {
    $sources = @('Protocol','SharedDevelopmentContext','DailyRefreshGate','DailyRefreshProof','PlayerStateRefreshModel',
        'DailyPlayerSnapshot','GuildWire','DailyRefineModel') | ForEach-Object { Join-Path $here "managed/$_.cs" }
    $fixture = Join-Path $here 'tests/DailyRefineTests.cs'
    $exe = Join-Path $managedOut 'DailyRefineTests.exe'
    & $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 /define:JSB_DAILY_REFINE_TEST "/out:$exe" @sources $fixture
    if ($LASTEXITCODE -ne 0) { throw 'Daily refine model fixture compilation failed' }
    $receipt = 'managed-daily-refine.json'
    $result = & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Daily refine model fixture failed' }
    $result | Set-Content -LiteralPath (Join-Path $out $receipt) -Encoding utf8
    $result | Write-Output
    $refineReceipts.Add($receipt)
    $probeSources = $sources + @($fixture) + @(
        'managed/DevelopmentContextReader.cs','managed/Lifecycle.cs','managed/ReceiptStore.cs','managed/Probe.cs',
        'tests/LifecycleTests.cs','tests/DailyRefineProbeTests.cs' | ForEach-Object { Join-Path $here $_ }
    )
    foreach ($enabled in @($false,$true)) {
        $exe = Join-Path $managedOut "DailyRefineProbeTests-$enabled.exe"
        $defines = 'JSB_DAILY_REFINE_TEST,JSB_REFINE_PROBE_TEST,JSB_LIFECYCLE_TEST'
        if ($enabled) { $defines += ',JSB_DAILY_REFINE_ENABLED' }
        & $csc /nologo /noconfig /target:exe /main:DailyRefineProbeTests /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 /reference:System.Web.Extensions.dll "/define:$defines" "/out:$exe" @probeSources
        if ($LASTEXITCODE -ne 0) { throw "Daily refine Probe fixture compilation failed (enabled=$enabled)" }
        $scenarios = if ($enabled) {
            @('settle','open-commit-command','commit-stop','commit-command','commit-lease','open-stop','stop','destroy','timeout','close-unverified')
        } else { @('disabled') }
        foreach ($scenario in $scenarios) {
            $receipt = "managed-daily-refine-$scenario.json"
            $result = & $exe $scenario
            if ($LASTEXITCODE -ne 0) { throw "Daily refine Probe fixture failed ($scenario)" }
            $result | Set-Content -LiteralPath (Join-Path $out $receipt) -Encoding utf8
            $result | Write-Output
            $refineReceipts.Add($receipt)
        }
    }
}
if ($DailyRefineOnly) { Invoke-DailyRefineTests; Write-Output "Daily refine receipts: $out"; return }
$woodReceipts = [Collections.Generic.List[string]]::new()
function Invoke-DailyWoodTests {
    $sources = @('Protocol','SharedDevelopmentContext','DailyRefreshGate','DailyRefreshProof','PlayerStateRefreshModel',
        'DailyPlayerSnapshot','GuildWire','DailyTimedModel','DailyWoodModel','NativeBattleStartStore','DevelopmentContextReader','Lifecycle','ReceiptStore','Probe') |
        ForEach-Object { Join-Path $here "managed/$_.cs" }
    $fixtures = @('LifecycleTests','DailyWoodTests','DailyWoodDurableTests','DailyWoodProbeTests') | ForEach-Object { Join-Path $here "tests/$_.cs" }
    foreach ($enabled in @($false,$true)) {
        $exe = Join-Path $managedOut "DailyWoodProbeTests-$enabled.exe"
        $defines = 'JSB_WOOD_PROBE_TEST,JSB_DAILY_WOOD_TEST,JSB_NATIVE_BATTLE_STORE_TEST'
        if ($enabled) { $defines += ',JSB_DAILY_WOOD_ENABLED' }
        & $csc /nologo /noconfig /target:exe /main:DailyWoodProbeTests /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 /reference:System.Web.Extensions.dll "/define:$defines" "/out:$exe" @sources @fixtures
        if ($LASTEXITCODE -ne 0) { throw "Daily wood Probe fixture compilation failed (enabled=$enabled)" }
        $scenarios = if ($enabled) {
            @('model','success','commit-command','commit-stop','commit-lease','commit-connection','commit-stream','commit-gui',
                'commit-role','commit-quota-epoch','missing-handler','stop','destroy')
        } else { @('disabled') }
        foreach ($scenario in $scenarios) {
            $receipt = if ($scenario -eq 'model') { 'managed-daily-wood-model.txt' } else { "managed-daily-wood-$scenario.json" }
            $result = & $exe $scenario
            if ($LASTEXITCODE -ne 0) { throw "Daily wood fixture failed ($scenario)" }
            $result | Set-Content -LiteralPath (Join-Path $out $receipt) -Encoding utf8
            $result | Write-Output
            $woodReceipts.Add($receipt)
        }
    }
}
if ($DailyWoodOnly) { Invoke-DailyWoodTests; Write-Output "Daily wood receipts: $out"; return }
if (-not $Zig) { $Zig = Join-Path $project 'research/unity-object-layer/bridge/tools/zig-windows-x86_64-0.13.0/zig.exe' }
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $out 'zig-global-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $out 'zig-local-cache'
$cc = @('cc','-target','x86-windows-gnu','-std=c11','-O2','-Wall','-Wextra','-Werror','-I',(Join-Path $here 'native'),'-I',$out)
& $Zig @cc '-shared' '-DJSB_SYNTHETIC' (Join-Path $here 'native/bridge.c') (Join-Path $here 'native/bridge.def') '-luser32' '-ladvapi32' '-o' (Join-Path $out 'bridge_selftest.dll')
if ($LASTEXITCODE -ne 0) { throw 'Native synthetic DLL compilation failed' }
& $Zig @cc '-municode' (Join-Path $here 'native/host.c') '-luser32' '-o' (Join-Path $out 'bridge_selftest.exe')
if ($LASTEXITCODE -ne 0) { throw 'Native synthetic host compilation failed' }
Push-Location $out
try {
    $nativeResult = & .\bridge_selftest.exe --self-test
    $nativeExit = $LASTEXITCODE
    $nativeResult | Set-Content -LiteralPath (Join-Path $out 'native-synthetic.jsonl') -Encoding utf8
    $nativeResult | Write-Output
    if ($nativeExit -ne 0) { throw "Native synthetic failed: $nativeExit" }
} finally { Pop-Location }
& $Zig @cc (Join-Path $here 'tests/native_paths.c') '-luser32' '-ladvapi32' '-o' (Join-Path $out 'native_paths.exe')
if ($LASTEXITCODE -ne 0) { throw 'Native cache-path test compilation failed' }
$pathResult = & (Join-Path $out 'native_paths.exe')
if ($LASTEXITCODE -ne 0) { throw 'Native cache-path tests failed' }
$pathResult | Set-Content -LiteralPath (Join-Path $out 'native-paths.json') -Encoding utf8
$pathResult | Write-Output
Invoke-DailyActivityTests
Invoke-DailyGuildTests
$rewardExe = Join-Path $managedOut 'DailyRewardTests.exe'
& $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 "/out:$rewardExe" `
    (Join-Path $here 'managed/Protocol.cs') (Join-Path $here 'managed/SharedDevelopmentContext.cs') (Join-Path $here 'managed/DevelopmentContextReader.cs') `
    (Join-Path $here 'managed/DailyRefreshGate.cs') (Join-Path $here 'managed/DailyRefreshProof.cs') (Join-Path $here 'managed/DailyPlayerSnapshot.cs') (Join-Path $here 'managed/PlayerStateRefreshModel.cs') `
    (Join-Path $here 'managed/DailyRewardModel.cs') (Join-Path $here 'tests/DailyRewardTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Daily reward fixture compilation failed' }
$rewardResult = & $rewardExe
$rewardExit = $LASTEXITCODE
$rewardResult | Set-Content -LiteralPath (Join-Path $out 'managed-daily-reward.json') -Encoding utf8
$rewardResult | Write-Output
if ($rewardExit -ne 0) { throw "Daily reward fixture failed: $rewardExit" }
$timedExe = Join-Path $managedOut 'DailyTimedTests.exe'
& $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 /reference:System.Web.Extensions.dll "/out:$timedExe" `
    (Join-Path $here 'managed/Protocol.cs') (Join-Path $here 'managed/SharedDevelopmentContext.cs') `
    (Join-Path $here 'managed/DailyRefreshGate.cs') (Join-Path $here 'managed/DailyTimedModel.cs') (Join-Path $here 'tests/DailyTimedTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Daily timed production model and callback adapter fixture compilation failed' }
$timedResult = & $timedExe
$timedExit = $LASTEXITCODE
$timedResult | Set-Content -LiteralPath (Join-Path $out 'managed-daily-timed.json') -Encoding utf8
$timedResult | Write-Output
if ($timedExit -ne 0) { throw "Daily timed model and callback adapter fixture failed: $timedExit" }
Invoke-DailyStoneTests
Invoke-DailyMingshiTests
Invoke-DailyArenaTests
Invoke-DailySoulTests
Invoke-DailyRefineTests
Invoke-DailyWoodTests
$testExe = Join-Path $managedOut 'LifecycleTests.exe'
& $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /define:JSB_LIFECYCLE_TEST "/out:$testExe" `
    (Join-Path $here 'managed/Protocol.cs') (Join-Path $here 'managed/Lifecycle.cs') (Join-Path $here 'managed/ReceiptStore.cs') `
    (Join-Path $here 'managed/Probe.cs') (Join-Path $here 'tests/LifecycleTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Managed independent lifecycle harness compilation failed' }
$runDirectory = Join-Path $managedOut ('run-' + [Guid]::NewGuid().ToString('N'))
$managedResult = & $testExe $runDirectory
$managedExit = $LASTEXITCODE
$managedResult | Set-Content -LiteralPath (Join-Path $out 'managed-lifecycle.json') -Encoding utf8
$managedResult | Write-Output
if ($managedExit -ne 0) { throw "Managed lifecycle harness failed: $managedExit" }
$rewardLifecycleExe = Join-Path $managedOut 'DailyRewardLifecycleTests.exe'
& $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ '/define:JSB_LIFECYCLE_TEST,JSB_DAILY_REWARD_ENABLED' "/out:$rewardLifecycleExe" `
    (Join-Path $here 'managed/Protocol.cs') (Join-Path $here 'managed/Lifecycle.cs') (Join-Path $here 'managed/ReceiptStore.cs') `
    (Join-Path $here 'managed/Probe.cs') (Join-Path $here 'tests/LifecycleTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Daily reward enabled lifecycle harness compilation failed' }
foreach ($ending in @('stop','destroy')) {
    $rewardLifecycleResult = & $rewardLifecycleExe $runDirectory $ending
    $rewardLifecycleExit = $LASTEXITCODE
    $rewardLifecycleResult | Set-Content -LiteralPath (Join-Path $out "managed-daily-reward-$ending.json") -Encoding utf8
    $rewardLifecycleResult | Write-Output
    if ($rewardLifecycleExit -ne 0) { throw "Daily reward enabled lifecycle harness failed ($ending): $rewardLifecycleExit" }
}
$timedLifecycleExe = Join-Path $managedOut 'DailyTimedLifecycleTests.exe'
& $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ '/define:JSB_LIFECYCLE_TEST,JSB_DAILY_TIMED_ENABLED' "/out:$timedLifecycleExe" `
    (Join-Path $here 'managed/Protocol.cs') (Join-Path $here 'managed/Lifecycle.cs') (Join-Path $here 'managed/ReceiptStore.cs') `
    (Join-Path $here 'managed/Probe.cs') (Join-Path $here 'tests/LifecycleTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Daily timed enabled lifecycle harness compilation failed' }
foreach ($ending in @('stop','destroy')) {
    $timedLifecycleResult = & $timedLifecycleExe $runDirectory "timed-$ending"
    $timedLifecycleExit = $LASTEXITCODE
    $timedLifecycleResult | Set-Content -LiteralPath (Join-Path $out "managed-daily-timed-$ending.json") -Encoding utf8
    $timedLifecycleResult | Write-Output
    if ($timedLifecycleExit -ne 0) { throw "Daily timed enabled lifecycle harness failed ($ending): $timedLifecycleExit" }
}
$campaignExe = Join-Path $managedOut 'CampaignTests.exe'
$protobufLibrary = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed\protobuf-net.dll'
if ((Get-FileHash -LiteralPath $protobufLibrary -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b') { throw 'Pure protobuf fixture dependency hash drift' }
Copy-Item -LiteralPath $protobufLibrary -Destination (Join-Path $managedOut 'protobuf-net.dll') -Force
& $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ "/out:$campaignExe" `
    "/reference:$protobufLibrary" /reference:System.Xml.dll `
    (Join-Path $here 'managed/Protocol.cs') (Join-Path $here 'managed/Lifecycle.cs') (Join-Path $here 'managed/CampaignObserver.cs') `
    (Join-Path $here 'managed/CampaignModel.cs') (Join-Path $here 'managed/ChallengeNavigation.cs') `
    (Join-Path $here 'managed/GeneralTutorialModel.cs') (Join-Path $here 'tests/CampaignTests.cs') (Join-Path $here 'tests/GeneralTutorialTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Production campaign fixture compilation failed' }
$campaignResult = & $campaignExe
$campaignExit = $LASTEXITCODE
$campaignResult | Set-Content -LiteralPath (Join-Path $out 'managed-campaign.json') -Encoding utf8
$campaignResult | Write-Output
if ($campaignExit -ne 0) { throw "Production campaign fixtures failed: $campaignExit" }
$receiptExe = Join-Path $managedOut 'ReceiptSharingTests.exe'
& $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ "/out:$receiptExe" `
    (Join-Path $here 'managed/ReceiptStore.cs') (Join-Path $here 'tests/ReceiptSharingTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Production receipt sharing test compilation failed' }
$receiptDirectory = Join-Path $managedOut ('receipt-sharing-' + [Guid]::NewGuid().ToString('N'))
$receiptResult = & $receiptExe $receiptDirectory
$receiptExit = $LASTEXITCODE
$receiptResult | Set-Content -LiteralPath (Join-Path $out 'receipt-sharing.json') -Encoding utf8
$receiptResult | Write-Output
if ($receiptExit -ne 0) { throw "Production receipt sharing test failed: $receiptExit" }
$growthExe = Join-Path $managedOut 'GrowthCapabilityTests.exe'
& $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ "/out:$growthExe" `
    (Join-Path $here 'managed/Protocol.cs') (Join-Path $here 'managed/GeneralTutorialModel.cs') `
    (Join-Path $here 'managed/SharedDevelopmentContext.cs') (Join-Path $here 'managed/GrowthTaskModel.cs') `
    (Join-Path $here 'managed/EquipmentUpgradeModel.cs') (Join-Path $here 'managed/SpiritBeastUpgradeModel.cs') `
    (Join-Path $here 'tests/GrowthCapabilityTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Production growth capability fixtures compilation failed' }
$growthDirectory = Join-Path $managedOut ('growth-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $growthDirectory -Force | Out-Null
$growthResult = & $growthExe $growthDirectory
$growthExit = $LASTEXITCODE
$growthResult | Set-Content -LiteralPath (Join-Path $out 'managed-growth.json') -Encoding utf8
$growthResult | Write-Output
if ($growthExit -ne 0) { throw "Production growth fixtures failed: $growthExit" }
$refreshExe = Join-Path $managedOut 'PlayerStateRefreshTests.exe'
& $csc /nologo /noconfig /target:exe /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 "/out:$refreshExe" `
    (Join-Path $here 'managed/SharedDevelopmentContext.cs') (Join-Path $here 'managed/PlayerStateRefreshModel.cs') `
    (Join-Path $here 'managed/DailyPlayerSnapshot.cs') (Join-Path $here 'managed/DailyRefreshProof.cs') (Join-Path $here 'managed/DailyRefreshGate.cs') `
    (Join-Path $here 'tests/PlayerStateRefreshTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Production player state refresh fixtures compilation failed' }
$refreshDirectory = Join-Path $managedOut ('refresh-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $refreshDirectory -Force | Out-Null
$refreshResult = & $refreshExe $refreshDirectory
$refreshExit = $LASTEXITCODE
$refreshResult | Set-Content -LiteralPath (Join-Path $out 'managed-refresh.json') -Encoding utf8
$refreshResult | Write-Output
if ($refreshExit -ne 0) { throw "Production player state refresh fixtures failed: $refreshExit" }
$sourceHashes = [ordered]@{}
foreach ($file in @('managed/Protocol.cs','managed/Lifecycle.cs','managed/ReceiptStore.cs','managed/Model.cs','managed/CampaignObserver.cs','managed/CampaignModel.cs','managed/ChallengeNavigation.cs','managed/GeneralTutorialModel.cs','managed/SharedDevelopmentContext.cs','managed/GrowthTaskModel.cs','managed/EquipmentUpgradeModel.cs','managed/SpiritBeastUpgradeModel.cs','managed/PlayerStateRefreshModel.cs','managed/DevelopmentModel.cs','managed/Probe.cs',
    'managed/DailyRefreshGate.cs','managed/DailyRefreshProof.cs','managed/DailyPlayerSnapshot.cs','managed/DailyActivityModel.cs','managed/DailyGuildModel.cs','managed/GuildWire.cs','managed/DevelopmentContextReader.cs','managed/DailyRewardModel.cs','managed/DailyTimedModel.cs','managed/DailyStoneModel.cs','managed/DailyMingshiModel.cs','managed/DailyArenaChestModel.cs','managed/DailySoulModel.cs','managed/DailyRefineModel.cs','managed/DailyWoodModel.cs','native/bridge.c','native/bridge.h','native/host.c','tests/DailyActivityTests.cs','tests/DailyGuildTests.cs','tests/DailyRewardTests.cs','tests/DailyTimedTests.cs','tests/DailyStoneTests.cs','tests/DailyMingshiTests.cs','tests/DailyMingshiProbeTests.cs','tests/DailyArenaChestTests.cs','tests/DailyArenaProbeTests.cs','tests/DailySoulTests.cs','tests/DailySoulProbeTests.cs','tests/DailyRefineTests.cs','tests/DailyRefineProbeTests.cs','tests/DailyWoodTests.cs','tests/DailyWoodProbeTests.cs','tests/LifecycleTests.cs','tests/CampaignTests.cs','tests/GeneralTutorialTests.cs','tests/ReceiptSharingTests.cs','tests/GrowthCapabilityTests.cs','tests/PlayerStateRefreshTests.cs','tests/native_paths.c','test.ps1')) {
    $sourceHashes[$file] = (Get-FileHash -LiteralPath (Join-Path $here $file) -Algorithm SHA256).Hash.ToLowerInvariant()
}
[ordered]@{status='PASS'; source_hashes=$sourceHashes; native='native-synthetic.jsonl'; paths='native-paths.json'; managed='managed-lifecycle.json'; campaign='managed-campaign.json'; growth='managed-growth.json'; refresh='managed-refresh.json'; daily='managed-daily.json'; daily_guild='managed-daily-guild.json'; daily_reward=@('managed-daily-reward.json','managed-daily-reward-stop.json','managed-daily-reward-destroy.json'); daily_timed=@('managed-daily-timed.json','managed-daily-timed-stop.json','managed-daily-timed-destroy.json'); daily_stone=$stoneReceipts; daily_mingshi=$mingshiReceipts; daily_arena=$arenaReceipts; daily_soul=$soulReceipts; daily_refine=$refineReceipts; daily_wood=$woodReceipts; receipt_sharing='receipt-sharing.json'; game_access=$false} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'targeted-validation.json') -Encoding utf8
