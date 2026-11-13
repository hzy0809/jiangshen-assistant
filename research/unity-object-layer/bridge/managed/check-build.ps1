$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Collections.Generic;
public static class JSProbeBuildMetadata {
    public static string[] Read(string path) {
        using(var f=File.OpenRead(path)) using(var pe=new PEReader(f)) {
            var r=pe.GetMetadataReader(); var a=r.GetAssemblyDefinition(); var result=new List<string>();
            result.Add("assembly="+r.GetString(a.Name));
            result.Add("version="+a.Version);
            result.Add("metadata="+r.MetadataVersion);
            result.Add("corflags="+pe.PEHeaders.CorHeader.Flags);
            result.Add("machine="+pe.PEHeaders.CoffHeader.Machine);
            foreach(var h in r.AssemblyReferences) {
                var ar=r.GetAssemblyReference(h);
                result.Add("reference="+r.GetString(ar.Name)+", Version="+ar.Version+", Token="+BitConverter.ToString(r.GetBlobBytes(ar.PublicKeyOrToken)).Replace("-", "").ToLowerInvariant());
            }
            return result.ToArray();
        }
    }
}
'@
$bridge = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $bridge 'JSUnityProbe.dll'
$metadata = [JSProbeBuildMetadata]::Read($payload)
$references = @($metadata | Where-Object { $_.StartsWith('reference=') })
$expectedRefs = @('reference=mscorlib, Version=2.0.5.0, Token=7cec85d7bea7798e', 'reference=UnityEngine, Version=0.0.0.0, Token=')
$checks = [ordered]@{
    name = @($metadata | Where-Object { $_ -match '^assembly=JSUnityProbe_[0-9a-f]{16}$' }).Count -eq 1
    legacy_metadata = $metadata -contains 'metadata=v2.0.50727'
    x86 = ($metadata -contains 'machine=I386') -and @($metadata | Where-Object { $_ -like 'corflags=*Requires32Bit*' }).Count -eq 1
    exact_references = $references.Count -eq 2 -and @($expectedRefs | Where-Object { $references -notcontains $_ }).Count -eq 0
}
$failed = @($checks.Values | Where-Object { -not $_ }).Count
$record = [ordered]@{
    utc = [DateTime]::UtcNow.ToString('o')
    mode = 'controller-assigned final exact-reference compile; metadata-only readback'
    compile_exit_code = 0
    sha256 = (Get-FileHash -LiteralPath $payload -Algorithm SHA256).Hash
    metadata = $metadata
    checks = $checks
    failed = $failed
    source_hashes = @(foreach ($file in @('Protocol.cs','Probe.cs')) {
        @{ file = $file; sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $file) -Algorithm SHA256).Hash }
    })
    runtime_ui = 'UNVERIFIED; no game load, enumeration, action or resource operation by managed author'
}
$out = Join-Path $bridge 'managed-build-check.json'
$record | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $out -Encoding utf8
$record | ConvertTo-Json -Depth 7
if ($failed -gt 0) { throw 'Legacy payload metadata check failed' }
