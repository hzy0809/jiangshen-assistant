param([string]$ManagedDir = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed')
$ErrorActionPreference = 'Stop'
# Compile only this independent metadata reader; no game assembly is loaded/executed.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Collections.Generic;
using System.Collections.Immutable;
public sealed class JSProbeSchemaProvider : ISignatureTypeProvider<string, object> {
    public static string Def(MetadataReader r, TypeDefinitionHandle h) {
        var t = r.GetTypeDefinition(h);
        var parent = t.GetDeclaringType();
        if (!parent.IsNil) return Def(r, parent) + "+" + r.GetString(t.Name);
        string ns = r.GetString(t.Namespace);
        return (ns.Length == 0 ? "" : ns + ".") + r.GetString(t.Name);
    }
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte kind) { return Def(r,h); }
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte kind) {
        var t=r.GetTypeReference(h); string ns=r.GetString(t.Namespace);
        return (ns.Length==0 ? "" : ns+".")+r.GetString(t.Name);
    }
    public string GetTypeFromSpecification(MetadataReader r, object c, TypeSpecificationHandle h, byte kind) { return r.GetTypeSpecification(h).DecodeSignature(this,c); }
    public string GetPrimitiveType(PrimitiveTypeCode c) { return "System." + c.ToString(); }
    public string GetArrayType(string e, ArrayShape s) { return e+"[,]"; }
    public string GetSZArrayType(string e) { return e+"[]"; }
    public string GetByReferenceType(string e) { return e+"&"; }
    public string GetPointerType(string e) { return e+"*"; }
    public string GetPinnedType(string e) { return e; }
    public string GetModifiedType(string m,string e,bool req) { return e; }
    public string GetGenericInstantiation(string t,ImmutableArray<string> a) { return t+"<"+String.Join(",",a)+">"; }
    public string GetGenericMethodParameter(object c,int i) { return "!!"+i; }
    public string GetGenericTypeParameter(object c,int i) { return "!"+i; }
    public string GetFunctionPointerType(MethodSignature<string> s) { return "fnptr"; }
}
public static class JSProbeSchema {
    sealed class Row { public string Name, Base, Assembly; public Dictionary<string,string> Fields=new Dictionary<string,string>(); public Dictionary<string,int> FieldTokens=new Dictionary<string,int>(); public Dictionary<string,int> Constants=new Dictionary<string,int>(); public List<string> Methods=new List<string>(); public Dictionary<string,MethodAttributes> MethodFlags=new Dictionary<string,MethodAttributes>(); public Dictionary<string,byte[]> MethodBodies=new Dictionary<string,byte[]>(); }
    static readonly Dictionary<string,Row> Types=new Dictionary<string,Row>();
    static string Resolve(MetadataReader r, EntityHandle h, JSProbeSchemaProvider p) {
        if(h.IsNil) return "";
        if(h.Kind==HandleKind.TypeDefinition) return JSProbeSchemaProvider.Def(r,(TypeDefinitionHandle)h);
        if(h.Kind==HandleKind.TypeReference) return p.GetTypeFromReference(r,(TypeReferenceHandle)h,0);
        return "unneeded_typespec";
    }
    static void Read(string path) {
        using(var f=File.OpenRead(path)) using(var pe=new PEReader(f)) {
            var r=pe.GetMetadataReader(); var p=new JSProbeSchemaProvider();
            foreach(var handle in r.TypeDefinitions) {
                var t=r.GetTypeDefinition(handle); var row=new Row { Name=JSProbeSchemaProvider.Def(r,handle), Base=Resolve(r,t.BaseType,p), Assembly=Path.GetFileNameWithoutExtension(path) };
                foreach(var fh in t.GetFields()) {
                    var field=r.GetFieldDefinition(fh); string name=r.GetString(field.Name);
                    string scope=(field.Attributes & FieldAttributes.Static)!=0 ? "static" : "instance";
                    row.Fields[name]=scope+" "+field.DecodeSignature(p,(object)null);
                    row.FieldTokens[name]=MetadataTokens.GetToken(fh);
                    var ch=field.GetDefaultValue();
                    if(!ch.IsNil) { var c=r.GetConstant(ch); if(c.TypeCode==ConstantTypeCode.Int32) row.Constants[name]=r.GetBlobReader(c.Value).ReadInt32(); }
                }
                foreach(var mh in t.GetMethods()) {
                    var method=r.GetMethodDefinition(mh); var sig=method.DecodeSignature(p,(object)null);
                    string scope=(method.Attributes & MethodAttributes.Static)!=0 ? "static" : "instance";
                    string access=(method.Attributes & MethodAttributes.MemberAccessMask)==MethodAttributes.Public ? "public" : "nonpublic";
                    string signature=access+" "+scope+" "+sig.ReturnType+" "+r.GetString(method.Name)+"("+String.Join(",",sig.ParameterTypes)+")";
                    row.Methods.Add(signature);
                    // Only the exact two candidate methods need IL/flags. Avoid
                    // collisions among unrelated generic/display signatures.
                    if(row.Name=="__BaseUI" && (r.GetString(method.Name)=="Enable" || r.GetString(method.Name)=="Disable") && sig.ParameterTypes.Length==0) {
                        row.MethodFlags[signature]=method.Attributes;
                        if(method.RelativeVirtualAddress!=0) row.MethodBodies[signature]=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
                    }
                }
                if(row.Name.StartsWith("<")) continue;
                if(Types.ContainsKey(row.Name)) throw new Exception("Duplicate type "+row.Name);
                Types.Add(row.Name,row);
            }
        }
    }
    static string CheckField(string spec) {
        var s=spec.Split('|'); Row row;
        if(!Types.TryGetValue(s[0],out row)) return "FAIL "+spec+" missing_type";
        string root=s[0];
        while(row!=null) {
            string actual;
            if(row.Fields.TryGetValue(s[1],out actual)) return (actual==s[2] ? "PASS " : "FAIL ")+spec+" declared="+row.Assembly+":"+row.Name+" actual="+actual;
            if(!Types.TryGetValue(row.Base,out row)) break;
        }
        return "FAIL "+spec+" missing_field";
    }
    public static string[] Check(string dir) {
        Types.Clear(); foreach(string a in new[]{"Assembly-CSharp","Assembly-CSharp-firstpass","pbdata","UnityEngine"}) Read(Path.Combine(dir,a+".dll"));
        var result=new List<string>();
        string[] fields=new[]{
            "MainUI|m_subui|static __BaseUI",
            "Global|character_|static Database.Character", "Global|extern_enabled|static System.Boolean",
            "BaseAsset|lds|static LoadScreen", "BaseAsset|rmg|instance RootMessenger", "BaseAsset|m_bInit|instance System.Boolean",
            "__BaseUI|enable|instance System.Boolean", "__BaseUI|first|instance System.Boolean", "__BaseUI|UICount_Visible|static System.Int32",
            "BuildingBuildUI|m_bBuildVisible|static System.Boolean", "LoadScreen|currmg|instance RootMessenger", "LoadScreen|isBattling|instance System.Boolean",
            "RootMessenger|LevelID|instance System.String", "RootMessenger|bLoading|instance System.Boolean", "RootMessenger|ExpectingCount|instance System.Int32",
            "Database.Character|level|instance System.Int16", "Database.Character|stamina|instance System.Int16", "Database.Character|gold|instance System.Int32", "Database.Character|bind_gold|instance System.Int32",
            "Database.Character|experience|instance System.Int32", "Database.Character|practice|instance System.Int32", "Database.Character|spirit_food|instance System.Int32", "Database.Character|mana|instance System.Int32", "Database.Character|stone|instance System.Int32",
            "Database.Character|reputation|instance System.UInt32", "Database.Character|money|instance System.Int32", "Database.Character|money64|instance System.Int64", "Database.Character|cur_formation_set|instance System.Int32", "Database.Character|formation_set|instance System.Int32",
            "PopupUI|popup|static System.Boolean", "TooltipUI|TooltipFlag|instance System.String", "TooltipUI|tttitle|instance System.String",
            "BuildingBehaviourScript|m_bEdit|instance System.Boolean", "BuildingBehaviourScript|m_bEditVisible|static System.Boolean",
            "FlowerMessUI|fc|instance System.Boolean", "FlowerMessUI|rm|instance System.Boolean", "CapitalUI|curArea|static Database.BuildingArea", "Database.BuildingArea|unlocked|instance System.Boolean",
            "MessengerUI|active_sessions|instance System.Collections.Generic.Dictionary`2<System.Int32,MessengerUI+ChatSession>", "MessengerUI|pending_sessions|instance System.Collections.Generic.List`1<MessengerUI+PendingChatSession>"
        };
        foreach(string f in fields) result.Add(CheckField(f));
        foreach(string spec in new[]{"__BaseUI|public instance System.Void Enable()","__BaseUI|public instance System.Void Disable()","UnityEngine.Object|public static UnityEngine.Object[] FindObjectsOfType(System.Type)"}) {
            var s=spec.Split('|'); result.Add((Types[s[0]].Methods.Contains(s[1]) ? "PASS " : "FAIL ")+"METHOD "+spec);
        }
        foreach(string owner in new[]{"MainUI","BaseUI"}) {
            bool absent=true;
            foreach(string m in Types[owner].Methods) if(m.EndsWith(" Enable()") || m.EndsWith(" Disable()")) absent=false;
            result.Add((absent ? "PASS " : "FAIL ")+"NO_DECLARED_INTERACTION_OVERRIDE "+owner);
        }
        foreach(string method in new[]{"Enable","Disable"}) {
            string signature="public instance System.Void "+method+"()";
            MethodAttributes flags; bool virtualMethod=Types["__BaseUI"].MethodFlags.TryGetValue(signature,out flags) && (flags & MethodAttributes.Virtual)!=0 && (flags & MethodAttributes.NewSlot)!=0;
            result.Add((virtualMethod ? "PASS " : "FAIL ")+"EXACT_VIRTUAL_DECLARATION __BaseUI."+method);
            byte[] body; bool bodyOk=Types["__BaseUI"].MethodBodies.TryGetValue(signature,out body);
            int enableToken=Types["__BaseUI"].FieldTokens["enable"];
            bodyOk=bodyOk && body.Length==8 && body[0]==0x02 && body[1]==(method=="Enable" ? 0x17 : 0x16) && body[2]==0x7d && BitConverter.ToInt32(body,3)==enableToken && body[7]==0x2a;
            result.Add((bodyOk ? "PASS " : "FAIL ")+"EXACT_FIELD_ONLY_IL __BaseUI."+method+" -> enable="+(method=="Enable" ? "true" : "false"));
        }
        foreach(string spec in new[]{"MainUI|Assembly-CSharp","BuildingBuildUI|Assembly-CSharp","Global|Assembly-CSharp-firstpass","Database.Character|Assembly-CSharp-firstpass","__BaseUI|Assembly-CSharp-firstpass","BaseAsset|Assembly-CSharp-firstpass",
            "RootUI|Assembly-CSharp", "PopupUI|Assembly-CSharp-firstpass", "TooltipUI|Assembly-CSharp-firstpass", "MessengerUI|Assembly-CSharp", "FlowerMessUI|Assembly-CSharp", "BuildingBehaviourScript|Assembly-CSharp", "CapitalUI|Assembly-CSharp", "Database.BuildingArea|Assembly-CSharp-firstpass",
            "BuildingMonsterScript|Assembly-CSharp", "SecretShopNPC|Assembly-CSharp", "CheckLauncherVersion|Assembly-CSharp", "AnnounceBehaviour|Assembly-CSharp", "MeditationUI|Assembly-CSharp", "ChatBehaviour|Assembly-CSharp"}) {
            var s=spec.Split('|'); result.Add((Types[s[0]].Assembly==s[1] ? "PASS " : "FAIL ")+"ASSEMBLY "+spec);
        }
        Row main=Types["MainUI"]; bool hierarchy=false;
        while(main!=null) { if(main.Name=="BaseAsset") { hierarchy=true; break; } if(!Types.TryGetValue(main.Base,out main)) break; }
        result.Add((hierarchy ? "PASS " : "FAIL ")+"HIERARCHY MainUI -> BaseAsset");
        return result.ToArray();
    }
}
'@
$checks = [JSProbeSchema]::Check($ManagedDir)
$failures = @($checks | Where-Object { $_.StartsWith('FAIL ') })
$record = [ordered]@{
    utc = [DateTime]::UtcNow.ToString('o')
    mode = 'PEReader metadata only; game assemblies not loaded or executed'
    passed = @($checks | Where-Object { $_.StartsWith('PASS ') }).Count
    failed = $failures.Count
    checks = $checks
    input_hashes = @(foreach ($file in @('Assembly-CSharp.dll','Assembly-CSharp-firstpass.dll','pbdata.dll','UnityEngine.dll')) {
        @{ file = $file; sha256 = (Get-FileHash -LiteralPath (Join-Path $ManagedDir $file) -Algorithm SHA256).Hash }
    })
    source_hashes = @(foreach ($file in @('Protocol.cs','Probe.cs')) {
        @{ file = $file; sha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $file) -Algorithm SHA256).Hash }
    })
}
$out = Join-Path (Split-Path -Parent $PSScriptRoot) 'managed-schema-check.json'
$record | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $out -Encoding utf8
"$($record.passed) PASS / $($record.failed) FAIL; $out"
if ($failures.Count -gt 0) { $failures; throw 'Reflection schema mismatch' }
