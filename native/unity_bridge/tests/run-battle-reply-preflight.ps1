param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('battle-reply-preflight-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$installed = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed'
$pins = [ordered]@{
    'pbdata.dll' = '905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab'
    'protobuf-net.dll' = 'e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b'
    'Ionic.Zlib.dll' = '799f6b9630886132564f961031f2908263e55b104e712009eef0d4bfceb80a30'
}
$sources = @((Join-Path $bridge 'managed/BattleReplyPreflight.cs'), (Join-Path $PSScriptRoot 'BattleReplyPreflightWire.cs'), (Join-Path $PSScriptRoot 'BattleReplyPreflightDecoderTests.cs'))
$dependencies = @()
foreach ($entry in $pins.GetEnumerator()) {
    $path = Join-Path $installed $entry.Key
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw "Pinned dependency differs: $($entry.Key)" }
    $dependencies += $path
    Copy-Item -LiteralPath $path -Destination (Join-Path $output $entry.Key) -Force
}
$manifest = @(@($sources + $dependencies + $PSCommandPath) | ForEach-Object { [ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()} })
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
$references = @($dependencies | ForEach-Object { "/reference:$_" })
$exe = Join-Path $output 'BattleReplyPreflightDecoderTests.exe'
$compile = @(& $csc /nologo /noconfig /target:exe /main:BattleReplyPreflightDecoderTests /platform:x86 /warn:4 /warnaserror+ /reference:System.Xml.dll "/out:$exe" @references @sources 2>&1)
$compileExit = $LASTEXITCODE
[IO.File]::WriteAllLines((Join-Path $output 'compile.log'), [string[]]$compile)
if ($compileExit -ne 0) { $compile | Write-Output; throw "Detached preflight fixture compile failed: $compileExit" }
$result = @(& $exe 2>&1)
$testExit = $LASTEXITCODE
$result | Set-Content -LiteralPath (Join-Path $output 'results.jsonl') -Encoding utf8
$result | Write-Output
$stable = $true
foreach ($entry in $manifest) { if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $stable = $false } }
[ordered]@{status=$(if ($testExit -eq 0 -and $stable) {'PASS'} else {'FAIL'}); compile_exit=$compileExit;test_exit=$testExit;source_hashes_stable=$stable;game_access=$false;network_access=$false;scope='Production shared decoder only with real pinned pbdata/protobuf/Ionic; no Unity, Model, stock callback, IPC, server or live game';source_manifest=(Join-Path $output 'sources.json');results=(Join-Path $output 'results.jsonl')} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "Battle reply preflight artifacts: $output"
if ($testExit -ne 0) { throw "Detached preflight fixture failed: $testExit" }
if (-not $stable) { throw 'Sources changed during the test run; prepare a fresh run' }
