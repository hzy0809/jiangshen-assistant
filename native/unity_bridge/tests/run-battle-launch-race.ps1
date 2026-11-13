param([string]$OutputDirectory = '', [string]$GuardSource = '', [switch]$DurableOnly)
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
$project = Split-Path -Parent (Split-Path -Parent $bridge)
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('battle-launch-race-' + [Guid]::NewGuid().ToString('N'))
}
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sources = @('managed/Protocol.cs','managed/BattleLifecycle.cs','managed/BattleLaunch.cs',
    'tests/BattleLaunchRaceFixture.cs','managed/NativeBattleStartStore.cs') | ForEach-Object { Join-Path $bridge $_ }
if ($GuardSource) { $sources[2] = [IO.Path]::GetFullPath($GuardSource) }
$sharedPreflight = Join-Path $bridge 'managed/BattleReplyPreflight.cs'
if (Test-Path -LiteralPath $sharedPreflight) { $sources += $sharedPreflight }
$installed = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed'
$dependencies = @('pbdata.dll','protobuf-net.dll','Ionic.Zlib.dll') | ForEach-Object { Join-Path $installed $_ }
$expectedDependencies = @('905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab',
    'e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b',
    '799f6b9630886132564f961031f2908263e55b104e712009eef0d4bfceb80a30')
for ($index = 0; $index -lt $dependencies.Count; $index++) {
    if ((Get-FileHash -LiteralPath $dependencies[$index] -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedDependencies[$index]) {
        throw 'Pinned offline protobuf dependency hash mismatch'
    }
    Copy-Item -LiteralPath $dependencies[$index] -Destination $output -Force
}
$sources += @(Join-Path $project 'jiangshen_assistant/battle_launch.py')
$sources += @(Join-Path $project 'tests/test_battle_launch.py')
$sources += @(Join-Path $PSScriptRoot 'native_battle_launch_durable_subprocess.py')
$manifest = @($sources + $dependencies + $PSCommandPath | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
$exe = Join-Path $output 'BattleLaunchRaceFixture.exe'
$compile = @(& C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /noconfig /target:exe /platform:x86 `
    /main:BattleLaunchRaceFixture /define:JSB_NATIVE_BATTLE_STORE_TEST /warn:4 /warnaserror+ /nowarn:0649 /reference:System.dll /reference:System.Xml.dll "/reference:$installed\pbdata.dll" `
    "/reference:$installed\protobuf-net.dll" "/reference:$installed\Ionic.Zlib.dll" "/out:$exe" @($sources | Where-Object { $_ -like '*.cs' }) 2>&1)
[IO.File]::WriteAllLines((Join-Path $output 'compile.log'), [string[]]$compile)
if ($LASTEXITCODE -ne 0) { $compile | Write-Output; throw 'Production battle launch race fixture failed to compile' }
$previousExe = $env:BATTLE_LAUNCH_FIXTURE_EXE
$previousOutput = $env:BATTLE_LAUNCH_TEST_OUTPUT
try {
    $env:BATTLE_LAUNCH_FIXTURE_EXE = $exe
    $env:BATTLE_LAUNCH_TEST_OUTPUT = $output
    Push-Location $project
    try {
        $previousPreference = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            if ($DurableOnly) { $result = @(& python (Join-Path $PSScriptRoot 'native_battle_launch_durable_subprocess.py') --exe $exe --output $output 2>&1) }
            else { $result = @(& python -c "import sys,unittest; result=unittest.TextTestRunner(stream=sys.stdout,verbosity=2).run(unittest.defaultTestLoader.loadTestsFromName('tests.test_battle_launch')); sys.exit(not result.wasSuccessful())" 2>&1) }
            $testExit = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previousPreference }
    }
    finally { Pop-Location }
    [IO.File]::WriteAllLines((Join-Path $output 'results.txt'), [string[]]$result)
    $result | Write-Output
}
finally { $env:BATTLE_LAUNCH_FIXTURE_EXE = $previousExe; $env:BATTLE_LAUNCH_TEST_OUTPUT = $previousOutput }
$stable = $true
foreach ($entry in $manifest) {
    if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $stable = $false }
}
[ordered]@{status=$(if($testExit -eq 0 -and $stable){'PASS'}else{'FAIL'});test_exit=$testExit;
    source_hashes_stable=$stable;game_access=$false;network_access=$false;real_named_ipc=$true;
    limitations='Detached stock callback branches and reflection/transport boundary; no live gameplay, scene loading or business acceptance.'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "Launch race artifacts: $output"
if ($testExit -ne 0 -or -not $stable) { throw 'Battle launch race checks failed or sources changed' }
