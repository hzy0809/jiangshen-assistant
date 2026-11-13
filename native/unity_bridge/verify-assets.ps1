param([string]$PackageDir = '', [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$project = Split-Path -Parent (Split-Path -Parent $here)
$package = if ($PackageDir) { $PackageDir } else { Join-Path $project 'jiangshen_assistant/runtime/win32' }
if (-not $OutputPath) { $OutputPath = Join-Path $here 'build/managed-metadata.json' }
$manifest = Get-Content -LiteralPath (Join-Path $package 'manifest.json') -Raw | ConvertFrom-Json
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Collections.Generic;
public static class AssistantBuildMetadata {
  public static string[] Read(string path) {
    using(var f=File.OpenRead(path)) using(var pe=new PEReader(f)) {
      var r=pe.GetMetadataReader(); var a=r.GetAssemblyDefinition(); var result=new List<string>();
      result.Add("assembly="+r.GetString(a.Name)); result.Add("metadata="+r.MetadataVersion);
      result.Add("corflags="+pe.PEHeaders.CorHeader.Flags); result.Add("machine="+pe.PEHeaders.CoffHeader.Machine);
      foreach(var handle in r.AssemblyReferences) { var ar=r.GetAssemblyReference(handle);
        result.Add("reference="+r.GetString(ar.Name)+", Version="+ar.Version+", Token="+BitConverter.ToString(r.GetBlobBytes(ar.PublicKeyOrToken)).Replace("-", "").ToLowerInvariant()); }
      return result.ToArray();
    }
  }
}
'@
$metadata = [AssistantBuildMetadata]::Read((Join-Path $package 'JSAssistantBridge.dll'))
$references = @($metadata | Where-Object { $_.StartsWith('reference=') })
$expected = @('reference=mscorlib, Version=2.0.5.0, Token=7cec85d7bea7798e','reference=UnityEngine, Version=0.0.0.0, Token=')
$checks = [ordered]@{
    internal_identity = $metadata -contains ('assembly=' + $manifest.managed_assembly_name)
    legacy_metadata = $metadata -contains 'metadata=v2.0.50727'
    x86 = ($metadata -contains 'machine=I386') -and @($metadata | Where-Object { $_ -like 'corflags=*Requires32Bit*' }).Count -eq 1
    exact_references = $references.Count -eq 2 -and @($expected | Where-Object { $references -notcontains $_ }).Count -eq 0
}
$record = [ordered]@{ status='PASS'; scope='metadata-only read, no payload load'; checks=$checks; metadata=$metadata;
    payload_sha256=(Get-FileHash -LiteralPath (Join-Path $package 'JSAssistantBridge.dll') -Algorithm SHA256).Hash.ToLowerInvariant() }
if (@($checks.Values | Where-Object {-not $_}).Count -ne 0) { $record.status='FAIL' }
$record | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $OutputPath -Encoding utf8
$record | ConvertTo-Json -Depth 6
if ($record.status -ne 'PASS') { throw 'Packaged managed metadata gate failed' }
