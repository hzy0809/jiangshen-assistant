$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$out = Join-Path $PSScriptRoot 'offline-protocol-tests.exe'
& $csc /nologo /target:exe /warn:4 "/out:$out" "$PSScriptRoot\Protocol.cs" "$PSScriptRoot\ProtocolTests.cs"
if ($LASTEXITCODE -ne 0) { throw "Offline protocol compile failed: $LASTEXITCODE" }
$result = & $out
$testExit = $LASTEXITCODE
$result
$record = [ordered]@{
    utc = [DateTime]::UtcNow.ToString('o')
    mode = 'desktop-framework protocol/resource tests only; no Unity/game assembly load'
    exit_code = $testExit
    output = $result
    source_hashes = @(foreach ($file in @('Protocol.cs','ProtocolTests.cs')) {
        @{ file = $file; sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $file) -Algorithm SHA256).Hash }
    })
}
$record | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'managed-protocol-check.json') -Encoding utf8
if ($testExit -ne 0) { throw "Offline protocol tests failed: $testExit" }
