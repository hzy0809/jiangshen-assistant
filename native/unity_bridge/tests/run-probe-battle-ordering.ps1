param([string]$OutputDirectory = '')
$ErrorActionPreference = 'Stop'
$bridge = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) ('probe-battle-ordering-' + [Guid]::NewGuid().ToString('N')) }
$output = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force | Out-Null
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$probe = Join-Path $bridge 'managed/Probe.cs'
$files = @('managed/Protocol.cs','managed/Lifecycle.cs','managed/ReceiptStore.cs') | ForEach-Object { Join-Path $bridge $_ }
$files += @((Join-Path $PSScriptRoot 'ProbeBattleOrderingStubs.cs'), (Join-Path $PSScriptRoot 'ProbeBattleOrderingTests.cs'))
$sourcePaths = @($probe) + $files + @($PSCommandPath)
$manifest = @($sourcePaths | ForEach-Object { [ordered]@{ path = [IO.Path]::GetFullPath($_); sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() } })
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'sources.json') -Encoding utf8
$original = [IO.File]::ReadAllText($probe)
$observeNeedle = 'if (!life.StopRequested) ObserveBattleSettlement();'
$guardNeedle = 'if (guiCommand != null && battleSettlement != null && battleSettlement.BlocksGuiInvocation)'
$pollGuardNeedle = 'if (battleSettlement != null && battleSettlement.BlocksGuiInvocation &&'
if (($original.Split(@($observeNeedle), [StringSplitOptions]::None).Count -ne 2) -or ($original.Split(@($guardNeedle), [StringSplitOptions]::None).Count -ne 2) -or ($original.Split(@($pollGuardNeedle), [StringSplitOptions]::None).Count -ne 2)) { throw 'Probe mutation anchor changed or is ambiguous; update the negative control explicitly' }
$variants = @(
    @{ name='fixed'; source=$probe; scenarios=@('update','gui','unknown','presentation','poll-start','poll-ack','poll-start-unknown','poll-ack-unknown','poll-confirmed','poll-observe','poll-disable','unknown-update'); expected=0; failure='' },
    @{ name='without-update-observation'; source=(Join-Path $output 'ProbeBattleNoUpdateObservation.cs'); scenarios=@('update'); expected=1; failure='pending_blocks_daily_capture' },
    @{ name='without-gui-gate'; source=(Join-Path $output 'ProbeBattleNoGuiGate.cs'); scenarios=@('gui'); expected=1; failure='pending_command_not_invoked' },
    @{ name='without-poll-gate'; source=(Join-Path $output 'ProbeBattleNoPollGate.cs'); scenarios=@('poll-start','poll-ack','poll-start-unknown','poll-ack-unknown'); expected=1; failure='pending_direct_command_not_invoked' }
)
[IO.File]::WriteAllText($variants[1].source, $original.Replace($observeNeedle, 'if (false) ObserveBattleSettlement();'))
[IO.File]::WriteAllText($variants[2].source, $original.Replace($guardNeedle, 'if (false && guiCommand != null && battleSettlement != null && battleSettlement.BlocksGuiInvocation)'))
[IO.File]::WriteAllText($variants[3].source, $original.Replace($pollGuardNeedle, 'if (false && battleSettlement != null && battleSettlement.BlocksGuiInvocation &&'))
$results = @()
foreach ($variant in $variants) {
    $exe = Join-Path $output ($variant.name + '.exe')
    $compile = @(& $csc /nologo /noconfig /target:exe /main:ProbeBattleOrderingTests /platform:x86 /warn:4 /nowarn:0649 /define:JSB_LIFECYCLE_TEST "/out:$exe" $variant.source $files 2>&1)
    $compileExit = $LASTEXITCODE
    $compile | Set-Content -LiteralPath (Join-Path $output ($variant.name + '-compile.log')) -Encoding utf8
    if ($compileExit -ne 0) { $compile | Write-Output; throw "Compile failed for $($variant.name): $compileExit" }
    foreach ($scenario in $variant.scenarios) {
        $result = @(& $exe $output $scenario 2>&1)
        $exit = $LASTEXITCODE
        $result | Set-Content -LiteralPath (Join-Path $output ($variant.name + '-' + $scenario + '.log')) -Encoding utf8
        $match = ($exit -eq $variant.expected) -and ($variant.failure -eq '' -or ($result -join "`n").Contains($variant.failure))
        $results += [ordered]@{ variant=$variant.name; scenario=$scenario; exit=$exit; expected_exit=$variant.expected; expected_failure=$variant.failure; matched=$match; output=($result -join "`n") }
        Write-Output "$($variant.name) $scenario exit=$exit expected=$($variant.expected): $($result[0])"
    }
}
$stable = $true
foreach ($entry in $manifest) { if ((Get-FileHash -LiteralPath $entry.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256) { $stable = $false } }
$ok = $stable -and @($results | Where-Object { -not $_.matched }).Count -eq 0
[ordered]@{ status=$(if ($ok) { 'PASS' } else { 'FAIL' }); source_hashes_stable=$stable; production_probe=$true; production_settlement=$false; production_daily_adapter=$false; game_access=$false; network_access=$false; results=$results } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'summary.json') -Encoding utf8
Write-Output "Probe battle ordering artifacts: $output"
if (-not $ok) { throw 'Probe battle ordering check failed; see summary.json' }
