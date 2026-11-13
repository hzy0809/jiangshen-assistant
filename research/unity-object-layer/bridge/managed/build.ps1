param([string]$ManagedDir = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed')
$ErrorActionPreference = 'Stop'
$csc = 'C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe'
$bridge = Split-Path -Parent $PSScriptRoot
$sourceIdentity = ((Get-FileHash -LiteralPath "$PSScriptRoot\Protocol.cs" -Algorithm SHA256).Hash + (Get-FileHash -LiteralPath "$PSScriptRoot\Probe.cs" -Algorithm SHA256).Hash)
$identityHash = [System.Security.Cryptography.SHA256]::Create()
try { $suffix = [BitConverter]::ToString($identityHash.ComputeHash([Text.Encoding]::ASCII.GetBytes($sourceIdentity))).Replace('-','').Substring(0,16).ToLowerInvariant() }
finally { $identityHash.Dispose() }
$buildDir = Join-Path $PSScriptRoot 'build'
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
$unique = Join-Path $buildDir "JSUnityProbe_$suffix.dll"
$args = @('/nologo', '/noconfig', '/target:library', '/platform:x86', '/optimize+', '/nostdlib+', '/warn:4',
    "/out:$unique", "/reference:$ManagedDir\mscorlib.dll", "/reference:$ManagedDir\UnityEngine.dll",
    "$PSScriptRoot\Protocol.cs", "$PSScriptRoot\Probe.cs")
& $csc @args
if ($LASTEXITCODE -ne 0) { throw "Legacy-reference payload compile failed: $LASTEXITCODE" }
Copy-Item -LiteralPath $unique -Destination "$bridge\JSUnityProbe.dll" -Force
"Internal assembly identity: JSUnityProbe_$suffix"
Get-FileHash -LiteralPath "$bridge\JSUnityProbe.dll" -Algorithm SHA256
