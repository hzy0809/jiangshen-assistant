param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('wood-model-durable-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$sources = @('Protocol','SharedDevelopmentContext','DailyRefreshGate','DailyRefreshProof','PlayerStateRefreshModel',
    'DailyPlayerSnapshot','GuildWire','DailyTimedModel','DailyWoodModel','NativeBattleStartStore') | ForEach-Object { Join-Path $bridge "managed/$_.cs" }
$sources += @((Join-Path $PSScriptRoot 'DailyWoodModelTestStubs.cs'), (Join-Path $PSScriptRoot 'DailyWoodTests.cs'), (Join-Path $PSScriptRoot 'DailyWoodDurableTests.cs'))
$exe = Join-Path $output 'DailyWoodModelDurableTests.exe'
$compile = @(& C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe /nologo /noconfig /target:exe /platform:x86 /main:DailyWoodDurableRunner /warn:4 /warnaserror+ /nowarn:0649 /define:JSB_DAILY_WOOD_TEST,JSB_NATIVE_BATTLE_STORE_TEST "/out:$exe" @sources 2>&1)
$compile | Set-Content -LiteralPath (Join-Path $output 'compile.log') -Encoding utf8
if ($LASTEXITCODE -ne 0) { $compile | Write-Output; throw 'Wood model durable fixture compile failed' }
$manifest = @($sources + $PSCommandPath | ForEach-Object { [ordered]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()} })
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
try {
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $result = @(& $exe durable 2>&1); $resultExit = $LASTEXITCODE
}
finally { $ErrorActionPreference = $previousPreference }
$result | Set-Content -LiteralPath (Join-Path $output 'results.txt') -Encoding utf8
$result | Write-Output
if ($resultExit -ne 0) { throw 'Wood model durable fixture failed' }
Write-Output "Wood model durable artifacts: $output"
