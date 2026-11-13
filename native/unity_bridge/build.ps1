param(
    [string]$ManagedDir = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed',
    [string]$Zig = '',
    [switch]$RunTests,
    [switch]$EnableDailyReward = $true,
    [switch]$EnableDailyTimed = $true,
    [switch]$EnableDailyStone = $true,
    [switch]$EnableDailyMingshi = $true,
    [switch]$EnableDailyArena = $true,
    [switch]$EnableDailySoul = $true,
    [switch]$EnableDailyRefine = $true,
    [switch]$EnableDailyWood = $true,
    [string]$PackageDir = ''
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$project = Split-Path -Parent (Split-Path -Parent $here)
$out = Join-Path $here 'build'
$defaultPackage = Join-Path $project 'jiangshen_assistant/runtime/win32'
$package = if ($PackageDir) { $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PackageDir) } else { $defaultPackage }
$isDefaultPackage = [IO.Path]::GetFullPath($package).TrimEnd('\','/') -eq [IO.Path]::GetFullPath($defaultPackage).TrimEnd('\','/')
if (-not $Zig) { $Zig = Join-Path $project 'research/unity-object-layer/bridge/tools/zig-windows-x86_64-0.13.0/zig.exe' }
if (-not (Test-Path -LiteralPath $Zig)) { throw 'Existing project-local Zig toolchain required; this script installs nothing.' }
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$legacyReferences = [ordered]@{
    'mscorlib.dll'='3f34c90ab282e8f3546946263087fb5002cf96ef9f412dd4011dab3f0f7cd8a4'
    'UnityEngine.dll'='d479befc03afb0f2071ffcf108847f06a0d560075eb8a76980f0ae929115ed73'
}
foreach ($file in $legacyReferences.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $ManagedDir $file) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $legacyReferences[$file]) {
        throw "Exact legacy compiler reference hash drift: $file"
    }
}
New-Item -ItemType Directory -Path $out,$package -Force | Out-Null
$sourceFiles = @('managed/TitleStartModel.cs','managed/Protocol.cs','managed/Lifecycle.cs','managed/ReceiptStore.cs','managed/ProcessRecovery.cs','managed/Model.cs','managed/CampaignObserver.cs','managed/CampaignModel.cs','managed/ChallengeNavigation.cs','managed/GeneralTutorialModel.cs','managed/SharedDevelopmentContext.cs','managed/DevelopmentContextReader.cs','managed/GrowthTaskModel.cs','managed/EquipmentUpgradeModel.cs','managed/SpiritBeastUpgradeModel.cs','managed/SpiritTributeModel.cs','managed/StarActivationModel.cs','managed/PlayerStateRefreshModel.cs','managed/DevelopmentModel.cs','managed/DailyRefreshGate.cs','managed/DailyRefreshProof.cs','managed/EliteChallengeQuota.cs','managed/DailyPlayerSnapshot.cs','managed/DailyActivityModel.cs','managed/GuildWire.cs','managed/DailyGuildModel.cs','managed/DailyRewardModel.cs','managed/DailyTimedModel.cs','managed/DailyStoneModel.cs','managed/DailyMingshiModel.cs','managed/DailyArenaChestModel.cs','managed/DailySoulModel.cs','managed/Probe.cs','managed/BattleLifecycle.cs','managed/BattleLaunch.cs','managed/BattleSettlement.cs',
    'managed/NativeBattleStartStore.cs','managed/DailyRefineModel.cs','managed/DailyWoodModel.cs','managed/BattleReplyPreflight.cs','managed/DailyArenaBattleModel.cs','managed/DailyPointRaceModel.cs','managed/DailyPointRaceChestModel.cs','managed/DailyFlowerModel.cs',
    'managed/DailyTrialModel.cs','managed/DailyTrainingMonsterModel.cs','managed/DailyGraveModel.cs','managed/DailyMonsterModel.cs','managed/DailyCityGatherModel.cs','managed/DailyCityShopModel.cs','managed/DailyOnlineGiftModel.cs','managed/DailyDivinityBattleModel.cs','managed/DailyEventRewardsModel.cs','managed/DailySignSalaryModel.cs','managed/DailyPopupModel.cs','managed/DailyTechBookCopyModel.cs','managed/DailyScriptAdapter.cs',
    'native/bridge.c','native/bridge.h','native/bridge.def','native/host.c','build.ps1')
$sources = [ordered]@{}
foreach ($file in $sourceFiles) { $sources[$file] = (Get-FileHash -LiteralPath (Join-Path $here $file) -Algorithm SHA256).Hash.ToLowerInvariant() }
$sourceIdentity = ($sources.Values -join '')
if ($EnableDailyReward) { $sourceIdentity += 'daily_reward=enabled' }
if ($EnableDailyTimed) { $sourceIdentity += 'daily_timed=enabled' }
if ($EnableDailyStone) { $sourceIdentity += 'daily_stone=enabled' }
if ($EnableDailyMingshi) { $sourceIdentity += 'daily_mingshi=enabled' }
if ($EnableDailyArena) { $sourceIdentity += 'daily_arena=enabled' }
if ($EnableDailySoul) { $sourceIdentity += 'daily_soul=enabled' }
if ($EnableDailyRefine) { $sourceIdentity += 'daily_refine=enabled' }
if ($EnableDailyWood) { $sourceIdentity += 'daily_wood=enabled' }
$sha = [Security.Cryptography.SHA256]::Create()
try { $buildId = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($sourceIdentity))).Replace('-','').ToLowerInvariant() }
finally { $sha.Dispose() }
$assemblyName = 'JSAssistantBridge_' + $buildId.Substring(0,16)
$uniquePayload = Join-Path $out ($assemblyName + '.dll')
$cscArgs = @('/nologo','/noconfig','/target:library','/platform:x86','/optimize+','/nostdlib+','/warn:4','/warnaserror+',
    "/out:$uniquePayload", "/reference:$ManagedDir\mscorlib.dll", "/reference:$ManagedDir\UnityEngine.dll")
$defines = @()
if ($EnableDailyReward) { $defines += 'JSB_DAILY_REWARD_ENABLED' }
if ($EnableDailyTimed) { $defines += 'JSB_DAILY_TIMED_ENABLED' }
if ($EnableDailyStone) { $defines += 'JSB_DAILY_STONE_ENABLED' }
if ($EnableDailyMingshi) { $defines += 'JSB_DAILY_MINGSHI_ENABLED' }
if ($EnableDailyArena) { $defines += 'JSB_DAILY_ARENA_ENABLED' }
if ($EnableDailySoul) { $defines += 'JSB_DAILY_SOUL_ENABLED' }
if ($EnableDailyRefine) { $defines += 'JSB_DAILY_REFINE_ENABLED' }
if ($EnableDailyWood) { $defines += 'JSB_DAILY_WOOD_ENABLED' }
if ($defines.Count) { $cscArgs += '/define:' + ($defines -join ',') }
$cscArgs += $sourceFiles | Where-Object { $_ -like 'managed/*.cs' } | ForEach-Object { Join-Path $here $_ }
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw 'Exact-legacy-reference managed payload compilation failed' }
$payloadHash = (Get-FileHash -LiteralPath $uniquePayload -Algorithm SHA256).Hash.ToLowerInvariant()
@(('#define JSB_PAYLOAD_SHA256 "' + $payloadHash + '"'),('#define JSB_ASSEMBLY_NAME "' + $assemblyName + '"')) |
    Set-Content -LiteralPath (Join-Path $out 'payload_seal.h') -Encoding ascii
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $out 'zig-global-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $out 'zig-local-cache'
$cc = @('cc','-target','x86-windows-gnu','-std=c11','-O2','-Wall','-Wextra','-Werror','-I',(Join-Path $here 'native'),'-I',$out)
& $Zig @cc '-shared' (Join-Path $here 'native/bridge.c') (Join-Path $here 'native/bridge.def') '-luser32' '-ladvapi32' '-o' (Join-Path $out 'bridge.dll')
if ($LASTEXITCODE -ne 0) { throw 'Native DLL compilation failed' }
& $Zig @cc '-municode' (Join-Path $here 'native/host.c') '-luser32' '-o' (Join-Path $out 'bridge_host.exe')
if ($LASTEXITCODE -ne 0) { throw 'Native host compilation failed' }
Copy-Item -LiteralPath $uniquePayload -Destination (Join-Path $package 'JSAssistantBridge.dll') -Force
foreach ($file in @('bridge.dll','bridge_host.exe')) { Copy-Item -LiteralPath (Join-Path $out $file) -Destination (Join-Path $package $file) -Force }
$files = [ordered]@{}
foreach ($file in @('bridge_host.exe','bridge.dll','JSAssistantBridge.dll')) { $files[$file] = (Get-FileHash -LiteralPath (Join-Path $package $file) -Algorithm SHA256).Hash.ToLowerInvariant() }
$manifest = [ordered]@{
    same_process_recovery_interface='soul-connection-reconciliation-v1';
    connection_interface='domain-independent-connection-v1';
    soul_board_adoption_interface='scoped-soul-board-adoption-v1';
    soul_activity_interface='optional-activity-evidence-v1';
    title_start_interface='scoped-title-start-v1';
    protocol=3; native_protocol=2; bridge_id='jiangshen-assistant-win32-v1'; build_id=$buildId;
    campaign_interface='scoped-campaign-native-flow-v2';
    challenge_interface='scoped-campaign-native-flow-v2'; challenge_mode='NORMAL_CHALLENGE';
    native_battle_start_interface='native-durable-battle-start-v1';
    native_battle_start_root=(Join-Path $project 'data/native-battle-starts-v1');
    daily_interface='activity-ui-observation-v1';
    daily_point_chest_interface='scoped-point-chest-v1';
    daily_popup_interface='scoped-daily-popup-v1';
    daily_guild_interface='scoped-guild-daily-v1';
    daily_refresh_interface='scoped-daily-refresh-v1';
    daily_reward_interface='scoped-daily-reward-v1'; daily_reward_enabled=[bool]$EnableDailyReward;
    daily_timed_interface='scoped-daily-timed-v1'; daily_timed_enabled=[bool]$EnableDailyTimed;
    daily_stone_interface='scoped-daily-stone-v1'; daily_stone_enabled=[bool]$EnableDailyStone;
    daily_mingshi_interface='scoped-daily-mingshi-v1'; daily_mingshi_enabled=[bool]$EnableDailyMingshi;
    daily_arena_interface='scoped-daily-arena-v1'; daily_arena_enabled=[bool]$EnableDailyArena;
    daily_soul_interface='scoped-daily-soul-v1'; daily_soul_enabled=[bool]$EnableDailySoul;
    daily_refine_interface='scoped-daily-refine-v1'; daily_refine_enabled=[bool]$EnableDailyRefine;
    daily_wood_interface='scoped-daily-wood-v1'; daily_wood_enabled=[bool]$EnableDailyWood;
    managed_assembly_name=$assemblyName; files=$files; sources=$sources;
    session_root=(Join-Path $project 'data/object_sessions'); heartbeat_seconds=5; lease_seconds=20; recovery_seconds=10;
    cleanup='normal_process_exit_for_assemblies'; live_validation='controller_owned';
    supported_game=[ordered]@{
        js_exe_sha256='5cc05ce27aa4d2fc0474603b4867fb00e3247fa437e459ef36a7075eab4bbc70';
        mono_sha256='51941db23fd29380039924d1a44964fec18ab2a80e72f14410fe0163d2cae500';
        managed=[ordered]@{
            'mscorlib.dll'=$legacyReferences['mscorlib.dll'];
            'UnityEngine.dll'=$legacyReferences['UnityEngine.dll'];
            'Assembly-CSharp.dll'='c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911';
            'Assembly-CSharp-firstpass.dll'='29d8fa2cb55e800ccdf1cdc6c5b274b924ea7d03c989296dfa569731e02a8d87';
            'pbdata.dll'='905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab'
            'dbdata.dll'='d32bb2b6c58b7cafcd4be5cbcdabbdb6a76c7003c0e2a160def80835be412a2d'
        }
    }
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $package 'manifest.json') -Encoding utf8
if ($RunTests) { & (Join-Path $here 'test.ps1') -Zig $Zig; if ($LASTEXITCODE -ne 0) { throw 'Targeted helper checks failed' } }
$manifest | ConvertTo-Json -Depth 4
