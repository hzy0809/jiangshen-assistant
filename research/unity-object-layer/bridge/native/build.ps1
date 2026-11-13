param(
    [Parameter(Mandatory=$true)][string]$CompilerBin,
    [string]$Payload,
    [switch]$SyntheticOnly,
    [switch]$RunSynthetic
)
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$out = Join-Path $here 'build'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$cc = Join-Path $CompilerBin 'i686-w64-mingw32-gcc.exe'
if (-not (Test-Path -LiteralPath $cc)) { $cc = Join-Path $CompilerBin 'i686-w64-mingw32-clang.exe' }
if (-not (Test-Path -LiteralPath $cc)) { $cc = Join-Path $CompilerBin 'zig.exe' }
if (-not (Test-Path -LiteralPath $cc)) { throw 'Need existing i686-w64-mingw32 gcc/clang or zig with headers and import libraries. This script installs nothing.' }
$common = @()
if ((Split-Path $cc -Leaf) -eq 'zig.exe') {
    $common = @('cc','-target','x86-windows-gnu')
    $env:ZIG_GLOBAL_CACHE_DIR = Join-Path $out 'zig-global-cache'
    $env:ZIG_LOCAL_CACHE_DIR = Join-Path $out 'zig-local-cache'
}
$common += @('-std=c11','-O2','-Wall','-Wextra','-Werror','-I', $here, '-I', $out)
& $cc @common '-DJSB_SYNTHETIC' '-shared' (Join-Path $here 'bridge.c') (Join-Path $here 'bridge.def') '-luser32' '-ladvapi32' '-o' (Join-Path $out 'bridge_selftest.dll')
if ($LASTEXITCODE -ne 0) { throw 'Synthetic DLL compilation failed' }
& $cc @common '-municode' (Join-Path $here 'host.c') '-luser32' '-o' (Join-Path $out 'bridge_selftest.exe')
if ($LASTEXITCODE -ne 0) { throw 'Synthetic host compilation failed' }
if ($RunSynthetic) {
    Push-Location $out
    try {
        $transcript = & (Join-Path $out 'bridge_selftest.exe') '--self-test'
        $testExitCode = $LASTEXITCODE
        $transcript | Set-Content -LiteralPath (Join-Path $out 'synthetic-results.jsonl') -Encoding utf8
        $transcript | Write-Output
        if ($testExitCode -ne 0) { throw "Synthetic run failed: exit $testExitCode" }
    } finally { Pop-Location }
}
if (-not $SyntheticOnly) {
    if (-not $Payload -or -not (Test-Path -LiteralPath $Payload -PathType Leaf)) { throw 'Production build requires existing managed payload path (-Payload)' }
    if ((Split-Path $Payload -Leaf) -ne 'JSUnityProbe.dll') { throw 'Payload basename must be JSUnityProbe.dll' }
    $target = Join-Path $out 'JSUnityProbe.dll'
    if ([IO.Path]::GetFullPath($Payload) -ne [IO.Path]::GetFullPath($target)) { Copy-Item -LiteralPath $Payload -Destination $target }
    $payloadHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
    ('#define JSB_PAYLOAD_SHA256 "' + $payloadHash + '"') | Set-Content -LiteralPath (Join-Path $out 'payload_seal.h') -Encoding ascii
    & $cc @common '-shared' (Join-Path $here 'bridge.c') (Join-Path $here 'bridge.def') '-luser32' '-ladvapi32' '-o' (Join-Path $out 'bridge.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Production DLL compilation failed' }
    & $cc @common '-municode' (Join-Path $here 'host.c') '-luser32' '-o' (Join-Path $out 'bridge_host.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Production host compilation failed' }
    [ordered]@{ protocol=1; payload_sha256=$payloadHash; compiler=$cc; live_run=$false;
        sources=@('bridge.c','bridge.h','bridge.def','host.c','build.ps1') | ForEach-Object {
            [ordered]@{file=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $here $_) -Algorithm SHA256).Hash.ToLowerInvariant()}
        };
        artifacts=@('bridge.dll','bridge_host.exe','JSUnityProbe.dll') | ForEach-Object {
            [ordered]@{file=$_;sha256=(Get-FileHash -LiteralPath (Join-Path $out $_) -Algorithm SHA256).Hash.ToLowerInvariant()}
        }
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $out 'build-manifest.json') -Encoding utf8
}
