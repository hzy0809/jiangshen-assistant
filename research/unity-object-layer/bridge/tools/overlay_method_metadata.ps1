$ErrorActionPreference = 'Stop'
# Read ECMA-335 metadata and IL bytes as data. Target assemblies are never loaded.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Collections.Generic;
public static class OverlayMethodEvidence {
  public static string[] Read(string path) {
    var result = new List<string>();
    using (var stream = File.OpenRead(path)) using (var pe = new PEReader(stream)) {
      var r = pe.GetMetadataReader();
      foreach (var h in r.TypeDefinitions) {
        var t = r.GetTypeDefinition(h); var name = r.GetString(t.Name);
        if (name != "__BaseUI" && name != "BaseUI" && name != "MainUI") continue;
        int count = 0;
        foreach (var mh in t.GetMethods()) {
          var m = r.GetMethodDefinition(mh); var mn = r.GetString(m.Name);
          if (mn != "Enable" && mn != "Disable") continue;
          count++;
          result.Add(Path.GetFileName(path)+" "+name+"."+mn+" MethodDef=0x"+MetadataTokens.GetToken(mh).ToString("X8")+" Attributes="+m.Attributes+" ImplAttributes="+m.ImplAttributes+" Signature="+BitConverter.ToString(r.GetBlobBytes(m.Signature))+" IL="+BitConverter.ToString(pe.GetMethodBody(m.RelativeVirtualAddress).GetILBytes()));
        }
        result.Add(Path.GetFileName(path)+" "+name+" declared Enable/Disable count="+count);
        if (name == "__BaseUI") foreach (var fh in t.GetFields()) {
          var f = r.GetFieldDefinition(fh);
          if (r.GetString(f.Name)=="enable") result.Add("__BaseUI.enable FieldDef=0x"+MetadataTokens.GetToken(fh).ToString("X8")+" Attributes="+f.Attributes+" Signature="+BitConverter.ToString(r.GetBlobBytes(f.Signature)));
        }
      }
    }
    return result.ToArray();
  }
}
'@
$managedDir = 'C:/Users/yu/AppData/Local/JiangShen/pc/js_Data/Managed'
$result = foreach ($assemblyName in @('Assembly-CSharp-firstpass.dll','Assembly-CSharp.dll')) { [OverlayMethodEvidence]::Read((Join-Path $managedDir $assemblyName)) }
$result | Set-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'overlay-method-metadata.txt') -Encoding utf8
$result
