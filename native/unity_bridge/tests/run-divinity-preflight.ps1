param([string]$OutputDirectory = '', [string]$ModelSource = '', [switch]$NegativeControl)
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('divinity-preflight-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
if (-not $ModelSource) { $ModelSource = Join-Path $bridge 'managed/DailyDivinityBattleModel.cs' }
$installed = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed'
$pins = [ordered]@{
    'pbdata.dll' = '905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab'
    'protobuf-net.dll' = 'e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b'
    'Ionic.Zlib.dll' = '799f6b9630886132564f961031f2908263e55b104e712009eef0d4bfceb80a30'
}
$references = @()
$deps = @()
foreach ($entry in $pins.GetEnumerator()) {
    $path = Join-Path $installed $entry.Key
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw "Pinned dependency drift: $($entry.Key)" }
    Copy-Item -LiteralPath $path -Destination (Join-Path $output $entry.Key) -Force
    $references += "/reference:$path"
    $deps += $path
}
$sources = @('Protocol','SharedDevelopmentContext','DailyRefreshGate','DailyRefreshProof',
    'PlayerStateRefreshModel','DailyPlayerSnapshot','GuildWire','DailyTimedModel','BattleLifecycle','BattleReplyPreflight' |
    ForEach-Object { Join-Path $bridge "managed/$_.cs" }) + @($ModelSource,
    (Join-Path $PSScriptRoot 'DivinityPreflightBoundaryStubs.cs'),
    (Join-Path $PSScriptRoot 'BattleReplyPreflightWire.cs'),
    (Join-Path $PSScriptRoot 'DivinityPreflightTests.cs'))
$evidence = Join-Path (Split-Path -Parent (Split-Path -Parent $bridge)) 'research/battle-skip/DivinityUI.cs.txt'
$manifest = @($sources + $deps + @($PSCommandPath,$evidence) | ForEach-Object {
    [ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
Copy-Item -LiteralPath $ModelSource -Destination (Join-Path $output 'DailyDivinityBattleModel-source.cs') -Force
$exe = Join-Path $output 'DivinityPreflightTests.exe'
$defines = 'JSB_DAILY_DIVINITY_TEST'
if ($NegativeControl) { $defines += ',DIVINITY_PREFLIGHT_BASELINE' }
$compile = @(& C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /noconfig /target:exe /main:DivinityPreflightTests /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 "/define:$defines" /reference:System.Xml.dll @references "/out:$exe" @sources 2>&1)
$compileExit = $LASTEXITCODE
[IO.File]::WriteAllLines((Join-Path $output 'compile.log'), [string[]]$compile)
if ($compileExit -ne 0) { $compile | Write-Output; throw "Divinity fixture compilation failed: $compileExit" }
$mode = if ($NegativeControl) { 'negative' } else { 'suite' }
$result = @(& $exe $mode 2>&1)
$testExit = $LASTEXITCODE
$result | Set-Content -LiteralPath (Join-Path $output 'results.jsonl') -Encoding utf8
$result | Write-Output
$stable = $true
foreach ($entry in $manifest) { if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $stable = $false } }
[ordered]@{status=$(if ($testExit -eq 0 -and $stable) {'PASS'} else {'FAIL'});compile_exit=$compileExit;test_exit=$testExit;source_hashes_stable=$stable;negative_control=[bool]$NegativeControl;game_access=$false;network_access=$false;
    limitations='Full production Divinity model, context and transport linked; constructed real pinned pbdata/protobuf/Ionic reports. Unity/stock presentation, launch, settlement and frame/admission inventory are detached boundaries. No live gameplay, server fee, loading/rendering or Win32 guard claim.'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "Divinity preflight artifacts: $output"
if ($testExit -ne 0 -or -not $stable) { throw 'Divinity preflight validation failed; see raw evidence' }
