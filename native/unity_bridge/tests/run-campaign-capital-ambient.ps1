param([string]$OutputDirectory = '', [string]$ManagedDirectory = '')
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $ManagedDirectory) { $ManagedDirectory = Join-Path $bridge 'managed' }
$ManagedDirectory = [IO.Path]::GetFullPath($ManagedDirectory)
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('campaign-capital-ambient-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $output) { throw "Output directory already exists: $output" }
New-Item -ItemType Directory -Path $output | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$protobuf = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed\protobuf-net.dll'
$expectedProtobuf = 'e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b'
$managed = @('Protocol', 'Lifecycle', 'CampaignObserver', 'CampaignModel', 'ChallengeNavigation', 'GeneralTutorialModel', 'SharedDevelopmentContext')
$sources = @($managed | ForEach-Object { Join-Path $ManagedDirectory "$_.cs" }) + @(
    (Join-Path $PSScriptRoot 'CampaignCapitalAmbientStubs.cs'),
    (Join-Path $PSScriptRoot 'GeneralTutorialTests.cs'),
    (Join-Path $PSScriptRoot 'CampaignCapitalAmbientTests.cs')
)
$manifest = @(@($sources + $PSCommandPath) | ForEach-Object {
    [ordered]@{ path = [IO.Path]::GetFullPath($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
if ((Get-FileHash -LiteralPath $protobuf -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expectedProtobuf) { throw 'Pinned fixture protobuf hash changed' }
Copy-Item -LiteralPath $protobuf -Destination (Join-Path $output 'protobuf-net.dll')
$exe = Join-Path $output 'CampaignCapitalAmbientTests.exe'
$compile = @(& $csc /nologo /noconfig /target:exe /main:CampaignCapitalAmbientTests /platform:x86 /warn:4 /warnaserror+ /nowarn:0649 `
    /reference:System.Xml.dll "/reference:$protobuf" "/out:$exe" @sources 2>&1)
$compileExit = $LASTEXITCODE
[IO.File]::WriteAllLines((Join-Path $output 'compile.log'), [string[]]$compile)
if ($compileExit -ne 0) { $compile | Write-Output; throw "Compilation failed ($compileExit); artifacts: $output" }
$result = @(& $exe 2>&1)
$testExit = $LASTEXITCODE
[IO.File]::WriteAllLines((Join-Path $output 'results.jsonl'), [string[]]$result)
$result | Write-Output
$stable = $true
foreach ($entry in $manifest) { if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $stable = $false } }
[ordered]@{ status = $(if ($testExit -eq 0 -and $stable) { 'PASS' } else { 'FAIL' }); compile_exit = $compileExit; test_exit = $testExit; source_hashes_stable = $stable; game_access = $false; network_access = $false; limitations = 'Detached Unity/client; invokes production selection and admission with injected character-id snapshot. BattleLaunch is a fail-loud stub and is not invoked. No pinned pbdata metadata identity check, live scene, GUI invocation, actual launch, or business acceptance.' } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "Artifacts: $output"
if ($testExit -ne 0 -or -not $stable) { throw 'Tests failed or sources changed during execution' }
