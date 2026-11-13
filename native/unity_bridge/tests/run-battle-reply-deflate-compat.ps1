param([string]$OutputDirectory, [string]$PreflightSourceFile = '')
$ErrorActionPreference = 'Stop'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('battle-deflate-compat-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $PreflightSourceFile) { $PreflightSourceFile = Join-Path $bridge 'managed/BattleReplyPreflight.cs' }
$sources = @([IO.Path]::GetFullPath($PreflightSourceFile), (Join-Path $PSScriptRoot 'BattleReplyDeflateCompatibilityTests.cs'))
$generator = Join-Path $PSScriptRoot 'BattleReplyDeflateCorpus.py'
$installed = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed'
$pins = [ordered]@{'pbdata.dll'='905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab';'protobuf-net.dll'='e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b';'Ionic.Zlib.dll'='799f6b9630886132564f961031f2908263e55b104e712009eef0d4bfceb80a30'}
$references = @(); $dependencies = @()
foreach ($entry in $pins.GetEnumerator()) {
    $path = Join-Path $installed $entry.Key
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.Value) { throw "Pinned dependency differs: $($entry.Key)" }
    Copy-Item -LiteralPath $path -Destination (Join-Path $output $entry.Key) -Force
    $references += "/reference:$path"; $dependencies += $path
}
$manifest = @(@($sources + $generator + $dependencies + $PSCommandPath) | ForEach-Object {[ordered]@{path=[IO.Path]::GetFullPath($_);sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()}})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
& python $generator --output $output | Set-Content -LiteralPath (Join-Path $output 'generator.json') -Encoding utf8
if ($LASTEXITCODE -ne 0) { throw 'Independent Python/zlib corpus generation failed' }
$exe = Join-Path $output 'BattleReplyDeflateCompatibilityTests.exe'
$compile = @(& 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe' /nologo /noconfig /target:exe /main:BattleReplyDeflateCompatibilityTests /platform:x86 /warn:4 /warnaserror+ /reference:System.Xml.dll /reference:System.Web.Extensions.dll "/out:$exe" @references @sources 2>&1)
$compileExit = $LASTEXITCODE
$compile | Set-Content -LiteralPath (Join-Path $output 'compile.log') -Encoding utf8
if ($compileExit -ne 0) {$compile | Write-Output; throw 'Differential fixture compilation failed'}
$result = @(& $exe (Join-Path $output 'corpus.tsv') (Join-Path $output 'results.jsonl') (Join-Path $output 'ionic-corpus.tsv') 2>&1)
$testExit = $LASTEXITCODE
$result | Set-Content -LiteralPath (Join-Path $output 'counts.json') -Encoding utf8
$result | Write-Output
& python $generator --validate-ionic (Join-Path $output 'ionic-corpus.tsv') | Set-Content -LiteralPath (Join-Path $output 'ionic-independent-check.json') -Encoding utf8
$independentExit = $LASTEXITCODE
$stable = $true
foreach ($entry in $manifest) {if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) {$stable=$false}}
[ordered]@{status=$(if ($testExit -eq 0 -and $independentExit -eq 0 -and $stable) {'PASS'} else {'FAIL'});compile_exit=$compileExit;test_exit=$testExit;independent_ionic_corpus_exit=$independentExit;source_hashes_stable=$stable;game_access=$false;network_access=$false;corpus_sha256=(Get-FileHash -LiteralPath (Join-Path $output 'corpus.tsv') -Algorithm SHA256).Hash.ToLowerInvariant();ionic_corpus_sha256=(Get-FileHash -LiteralPath (Join-Path $output 'ionic-corpus.tsv') -Algorithm SHA256).Hash.ToLowerInvariant();counts=(Join-Path $output 'counts.json');results=(Join-Path $output 'results.jsonl');manifest=(Join-Path $output 'sources.json')} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
if ($testExit -ne 0) {throw 'Differential fixture found compatibility/safety failures'}
if ($independentExit -ne 0) {throw 'Independent zlib found invalid Ionic producer corpus'}
if (-not $stable) {throw 'Sources changed during run; prepare a fresh run'}
