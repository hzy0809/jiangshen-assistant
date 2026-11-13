$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('daily-script-ownership-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    $exe = Join-Path $temporary 'DailyScriptOwnershipTests.exe'
    & $csc /nologo /warn:4 /warnaserror+ "/out:$exe" (Join-Path $PSScriptRoot 'DailyScriptOwnershipTests.cs') (Join-Path $PSScriptRoot '../managed/DailyScriptAdapter.cs')
    if ($LASTEXITCODE -ne 0) { throw 'Ownership test compilation failed' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Ownership tests failed' }
} finally {
    Write-Output "Ownership test artifacts: $temporary"
}
