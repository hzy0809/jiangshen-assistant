param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('spirit-tribute-ambient-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output directory already exists: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$sources = @((Join-Path $bridge 'managed/SharedDevelopmentContext.cs'), (Join-Path $bridge 'managed/SpiritTributeModel.cs'), (Join-Path $PSScriptRoot 'SpiritTributeAmbientStubs.cs'), (Join-Path $PSScriptRoot 'SpiritTributeAmbientTests.cs'))
$manifest = @(@($sources + $PSCommandPath) | ForEach-Object { [ordered]@{ path = [IO.Path]::GetFullPath($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
$exe = Join-Path $output 'SpiritTributeAmbientTests.exe'
$compile = @(& $csc /nologo /noconfig /target:exe /main:SpiritTributeAmbientTests /warn:4 /warnaserror+ /nowarn:0649 "/out:$exe" @sources 2>&1)
$compileExit = $LASTEXITCODE
[IO.File]::WriteAllLines((Join-Path $output 'compile.log'), [string[]]$compile)
if ($compileExit -ne 0) { $compile | Write-Output; throw "Compilation failed ($compileExit); artifacts: $output" }
$result = @(& $exe 2>&1)
$testExit = $LASTEXITCODE
[IO.File]::WriteAllLines((Join-Path $output 'results.txt'), [string[]]$result)
$result | Write-Output
$stable = $true
foreach ($entry in $manifest) { if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $stable = $false } }
[ordered]@{ status = $(if ($testExit -eq 0 -and $stable) { 'PASS' } else { 'FAIL' }); compile_exit = $compileExit; test_exit = $testExit; source_hashes_stable = $stable; game_access = $false; network_access = $false; limitations = 'Detached fake Unity and game objects; no live scene, rendering, network, device, or business acceptance.' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "Artifacts: $output"
if ($testExit -ne 0 -or -not $stable) { throw 'Tests failed or sources changed during execution' }
