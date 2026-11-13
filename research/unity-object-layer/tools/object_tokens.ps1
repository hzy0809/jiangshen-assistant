# Offline ECMA-335 metadata reader. Does not load or execute target assemblies.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Collections.Generic;
public static class ObjectMapTokens {
  public static string[] Read(string path) {
    var result = new List<string>();
    using (var stream = File.OpenRead(path))
    using (var pe = new PEReader(stream)) {
      var reader = pe.GetMetadataReader();
      foreach (var handle in reader.TypeDefinitions) {
        var type = reader.GetTypeDefinition(handle);
        var name = reader.GetString(type.Name);
        var ns = reader.GetString(type.Namespace);
        if (name == "Global" || name == "MainUI" || name == "Character" || name == "_character" || name == "_ButtonMain" || name == "_ButtonStyle") {
          result.Add(string.Format("{0} {1}.{2} TypeDef=0x{3:X8}", Path.GetFileName(path), ns, name, MetadataTokens.GetToken(handle)));
          int ordinal = 0;
          foreach (var fieldHandle in type.GetFields()) {
            var field = reader.GetFieldDefinition(fieldHandle);
            string fieldName = reader.GetString(field.Name);
            if ((name == "Global" && fieldName == "character_") || (name == "_character" && (fieldName == "level" || fieldName == "stamina" || fieldName == "gold")) || (name == "MainUI" && (fieldName == "btnTop" || fieldName == "btnBottom")))
              result.Add(string.Format("  FIELD {0} declaredOrdinal0={1} FieldDef=0x{2:X8} Attributes={3} Signature={4}", fieldName, ordinal, MetadataTokens.GetToken(fieldHandle), field.Attributes, BitConverter.ToString(reader.GetBlobBytes(field.Signature))));
            ordinal++;
          }
        }
      }
    }
    return result.ToArray();
  }
}
'@
$managed = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed'
$result = foreach ($name in @('Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','pbdata.dll')) {
  [ObjectMapTokens]::Read((Join-Path $managed $name))
}
$result | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'object-map-typedef-tokens.txt') -Encoding utf8
$result
