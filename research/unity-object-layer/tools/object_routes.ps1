# Pure offline field-signature decoding with PEReader; never loads target DLLs.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
public sealed class ObjectMapSignature : ISignatureTypeProvider<string, object> {
  public string GetArrayType(string e, ArrayShape s) => e + "[array]";
  public string GetByReferenceType(string e) => e + "&";
  public string GetFunctionPointerType(MethodSignature<string> s) => "fnptr";
  public string GetGenericInstantiation(string t, ImmutableArray<string> a) => t + "<" + string.Join(",", a) + ">";
  public string GetGenericMethodParameter(object c, int i) => "!!" + i;
  public string GetGenericTypeParameter(object c, int i) => "!" + i;
  public string GetModifiedType(string m, string t, bool r) => t;
  public string GetPinnedType(string e) => e;
  public string GetPointerType(string e) => e + "*";
  public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
  public string GetSZArrayType(string e) => e + "[]";
  public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) { var t = r.GetTypeDefinition(h); return r.GetString(t.Namespace) + "." + r.GetString(t.Name); }
  public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) { var t = r.GetTypeReference(h); return r.GetString(t.Namespace) + "." + r.GetString(t.Name); }
  public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte k) => r.GetTypeSpecification(h).DecodeSignature(this, c);
}
public static class ObjectMapRoutes {
  public static string[] Read(string path) {
    var result = new List<string>();
    var provider = new ObjectMapSignature();
    using (var stream = File.OpenRead(path))
    using (var pe = new PEReader(stream)) {
      var reader = pe.GetMetadataReader();
      int typesScanned = 0, fieldsScanned = 0, mainUiFields = 0;
      foreach (var th in reader.TypeDefinitions) {
        typesScanned++;
        var type = reader.GetTypeDefinition(th);
        var typeName = reader.GetString(type.Namespace) + "." + reader.GetString(type.Name);
        foreach (var mh in type.GetMethods()) {
          var method = reader.GetMethodDefinition(mh);
          string methodName = reader.GetString(method.Name);
          if (methodName == "GlobalCreate" || methodName == "GlobalCreateProto")
            result.Add(string.Format("METHOD {0} {1}::{2} MethodDef=0x{3:X8} {4}", Path.GetFileName(path), typeName, methodName, MetadataTokens.GetToken(mh), method.Attributes));
        }
        int ordinal = 0;
        foreach (var fh in type.GetFields()) {
          var field = reader.GetFieldDefinition(fh);
          var fieldType = field.DecodeSignature(provider, (object)null);
          fieldsScanned++;
          if (fieldType.Contains(".MainUI")) mainUiFields++;
          if (fieldType.Contains("MainUI") || fieldType.Contains("BaseUI") || fieldType.Contains("RootMessenger") || fieldType.Contains("LoadScreen") || fieldType == ".RootUI")
            result.Add(string.Format("{0} TypeDef=0x{1:X8} {2}::{3} : {4}; {5}; declaredOrdinal0={6}; FieldDef=0x{7:X8}", Path.GetFileName(path), MetadataTokens.GetToken(th), typeName, reader.GetString(field.Name), fieldType, field.Attributes, ordinal, MetadataTokens.GetToken(fh)));
          ordinal++;
        }
      }
      result.Add(string.Format("SCAN {0}: TypeDefs={1}, Fields={2}, directOrGenericMainUIFields={3}", Path.GetFileName(path), typesScanned, fieldsScanned, mainUiFields));
    }
    return result.ToArray();
  }
}
'@
$managed = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed'
$result = foreach ($name in @('Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','pbdata.dll','dbdata.dll')) {
  [ObjectMapRoutes]::Read((Join-Path $managed $name))
}
$result | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'object-map-field-route-candidates.txt') -Encoding utf8
$result | Where-Object { $_ -match 'MainUI|BaseAsset::lds|BaseUI::rootUI|LoadScreen::|RootUI::|SCAN' }
