param([string]$ManagedDir = 'C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed', [string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not $OutputPath) { $OutputPath = Join-Path $here 'build/campaign-metadata.json' }
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata.Ecma335;
public static class BridgeCampaignMetadata {
  static string Name(MetadataReader r, EntityHandle h) {
    if(h.IsNil) return "";
    if(h.Kind==HandleKind.TypeReference) { var t=r.GetTypeReference((TypeReferenceHandle)h); return r.GetString(t.Namespace)+"."+r.GetString(t.Name); }
    if(h.Kind!=HandleKind.TypeDefinition) return "";
    var d=r.GetTypeDefinition((TypeDefinitionHandle)h); var parent=d.GetDeclaringType();
    return parent.IsNil ? (String.IsNullOrEmpty(r.GetString(d.Namespace))?"":r.GetString(d.Namespace)+".")+r.GetString(d.Name) : Name(r,parent)+"+"+r.GetString(d.Name);
  }
  static string Sig(MetadataReader r, BlobHandle h) { return BitConverter.ToString(r.GetBlobBytes(h)); }
  public static string[] Read(string path) {
    var lines=new List<string>();
    using(var f=File.OpenRead(path)) using(var pe=new PEReader(f)) {
      var r=pe.GetMetadataReader();
      foreach(var h in r.TypeDefinitions) {
        var t=r.GetTypeDefinition(h); var n=Name(r,h); lines.Add(n+"|T|"+Name(r,t.BaseType));
        foreach(var fh in t.GetFields()) { var x=r.GetFieldDefinition(fh); lines.Add(n+"|F|"+r.GetString(x.Name)+"|"+MetadataTokens.GetToken(fh).ToString("x8")+"|"+Sig(r,x.Signature)); }
        foreach(var mh in t.GetMethods()) { var x=r.GetMethodDefinition(mh); lines.Add(n+"|M|"+r.GetString(x.Name)+"|"+MetadataTokens.GetToken(mh).ToString("x8")+"|"+Sig(r,x.Signature)); }
        foreach(var ph in t.GetProperties()) { var x=r.GetPropertyDefinition(ph); lines.Add(n+"|P|"+r.GetString(x.Name)+"|"+MetadataTokens.GetToken(ph).ToString("x8")+"|"+Sig(r,x.Signature)); }
      }
    }
    return lines.ToArray();
  }
  public static string MethodIl(string path,int token) {
    using(var f=File.OpenRead(path)) using(var pe=new PEReader(f)) {
      var r=pe.GetMetadataReader(); var method=r.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(token&0x00ffffff));
      return BitConverter.ToString(pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes());
    }
  }
  public static string MethodTarget(string path,int token) {
    using(var f=File.OpenRead(path)) using(var pe=new PEReader(f)) {
      var r=pe.GetMetadataReader(); EntityHandle h=MetadataTokens.EntityHandle(token);
      if(h.Kind==HandleKind.MethodSpecification) h=r.GetMethodSpecification((MethodSpecificationHandle)h).Method;
      if(h.Kind==HandleKind.MemberReference) { var m=r.GetMemberReference((MemberReferenceHandle)h); return Name(r,m.Parent)+"::"+r.GetString(m.Name); }
      if(h.Kind==HandleKind.MethodDefinition) { var m=r.GetMethodDefinition((MethodDefinitionHandle)h); return Name(r,m.GetDeclaringType())+"::"+r.GetString(m.Name); }
      return "UNSUPPORTED";
    }
  }
  public static string PureTwoFieldRead(string path,int token) {
    using(var f=File.OpenRead(path)) using(var pe=new PEReader(f)) {
      var r=pe.GetMetadataReader(); var method=r.GetMethodDefinition(MetadataTokens.MethodDefinitionHandle(token&0x00ffffff));
      var il=pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes();
      if(il.Length!=12 || il[0]!=0x02 || il[1]!=0x7b || il[6]!=0x7b || il[11]!=0x2a) return "NOT_PURE_FIELD_CHAIN";
      string[] fields=new string[2];
      for(int i=0;i<2;i++) {
        var handle=MetadataTokens.EntityHandle(BitConverter.ToInt32(il,i==0?2:7));
        if(handle.Kind==HandleKind.FieldDefinition) { var d=r.GetFieldDefinition((FieldDefinitionHandle)handle); fields[i]=Name(r,d.GetDeclaringType())+"::"+r.GetString(d.Name); }
        else if(handle.Kind==HandleKind.MemberReference) { var d=r.GetMemberReference((MemberReferenceHandle)handle); fields[i]=Name(r,d.Parent)+"::"+r.GetString(d.Name); }
        else return "NOT_FIELD_REFERENCE";
      }
      return String.Join(" > ",fields);
    }
  }
}
'@
$hashes = [ordered]@{
    'Assembly-CSharp.dll'='c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911'
    'Assembly-CSharp-firstpass.dll'='29d8fa2cb55e800ccdf1cdc6c5b274b924ea7d03c989296dfa569731e02a8d87'
    'pbdata.dll'='905cf19394545e2c4b5d49d994a7d981e13ae38a2a629f7d02329f27c6d2d5ab'
    'dbdata.dll'='d32bb2b6c58b7cafcd4be5cbcdabbdb6a76c7003c0e2a160def80835be412a2d'
}
$types = @{}
foreach ($file in $hashes.Keys) {
    $path = Join-Path $ManagedDir $file
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hashes[$file]) { throw "Campaign metadata input hash drift: $file" }
    foreach ($line in [BridgeCampaignMetadata]::Read($path)) {
        $parts = $line.Split('|'); $name = $parts[0]
        if ($parts[1] -eq 'T') { $types[$name] = @{parent=$parts[2]; members=@{}} }
        else { $types[$name].members[$parts[1] + ':' + $parts[2]] = $parts }
    }
}
function Find-Member([string]$type, [string]$member) {
    for ($depth=0; $depth -lt 20 -and $type -and $types.ContainsKey($type); $depth++) {
        if ($types[$type].members.ContainsKey($member)) { return ,$types[$type].members[$member] }
        $type = $types[$type].parent
        if ($type.StartsWith('.')) { $type = $type.Substring(1) }
    }
    return $null
}
$requirements = [ordered]@{
    'Global'=@('F:character_','F:extern_enabled','M:setGuideCheck')
    'MainUI'=@('M:_campaign_check','M:_campaign','M:SetSubUI')
    'RootUI'=@('F:m_client','M:cs_campaign_enter')
    'game.net.tcp_base'=@('F:messages','F:m_handlers','M:Parse','M:Run')
    'game.net.tcp_base+HandleMessage'=@('F:op','F:stream','M:.ctor')
    'JSProtoBuf'=@('F:wrapper','M:get_Serializer')
    'JSProtoBuf+PBSerializerWrapper'=@('M:Deserialize')
    'RootMessenger'=@('M:ToDisable','F:LevelID')
    'BaseAsset'=@('F:lds','F:rmg','F:loading','F:collecting')
    'LoadScreen'=@('F:currmg')
    '__BaseUI'=@('F:visible_time','M:_rectRB')
    'CampaignUI'=@('F:m_campaignID','F:m_troopID','F:m_campaign','F:m_troops','F:m_bDebug','F:m_bPostInit','F:m_bBorning','F:m_bBornWant','F:m_bScenarioVisible','F:m_bSignActiving','M:wantCombat','M:BonusOpen')
    'CampaignTroopUI'=@('F:m_parent','F:m_troop','M:BattleFormation','M:OnAttack')
    'CampaignTroopScript'=@('F:ParentUI','F:Troop','M:MyMouseDown','M:Web_OnGUI','M:IsEnable')
    'CampaignSignScript'=@('F:ParentUI','F:Sign')
    'CampaignChestScript'=@('F:ParentUI','F:Chest')
    'FormationUI'=@('F:Campaignid','F:Troopid','F:OnAttack','F:DefaultState','F:substate','F:viewonly','F:isVisible','F:isLoading','F:onfight','F:waitstate','F:newhint_skill','M:OnClickSetCurrentForm')
    'BattleUI'=@('F:m_parentUI','F:m_prevUI','F:idcampaign','F:idtroop','F:bNeedWaitForBattleEnd','F:bUseBattleEnd','F:m_pktBattleEnd','F:interpreter','F:m_BattleLength','F:top_ui','F:spirit_top_ui','F:wood_top_ui','P:ReportID','P:BattleFighting','P:IsSurrended','P:ViewType','P:IsCrossServer','M:battle_exit')
    'BattleReportInterpreter2'=@('P:packet')
    'BattleUI+PktBattleEnd'=@('F:pkt')
    'BattleNormalResultUI'=@('F:battle_ui','F:m_battle_view_ui','F:m_reportid','M:Hide')
    'BattleResultUI'=@('F:mine_ui','F:m_bCampaign','F:m_troopID','F:m_prevUI','F:OnClose','M:Hide')
    'CampaignChapterClearUI'=@('F:m_bAnecdote','F:m_bNextChapter','F:rmg','M:Hide')
    'ScenarioUI'=@('F:m_scenario','F:m_prevUI','F:m_iSection','F:m_bSectionEnd','F:m_bHasNewUI','F:m_bMoving','F:m_bNext','F:m_bWantNewUI','F:m_bTrigger','F:m_bFirst','F:OnClose','F:newUIList','F:skipStyle','M:scenarioSkip','M:IsEnable','M:Update','M:Web_OnGUI')
    'ScenarioUI+newUIBtn'=@('F:name','F:width','F:height','F:tex','F:curPos','F:targetPos','F:bMovingEnd')
    'ScenarioFunctionUI'=@('M:MyLoading','M:Show','M:Web_OnGUI')
    'Scenario'=@('F:id','F:param0','F:sections','F:m_sections')
    'ScenarioSection'=@('F:triggers')
    'Database.Campaign'=@('F:idcampaign_type','P:unlocked','P:finished')
    'Database.CampaignTroop'=@('F:data_','F:idcampaign','F:idtroop','F:visible','P:canattack','P:killed','P:clicked','P:finish_count','F:finish_max','F:chanllege_level','F:click_mode','F:show_arrow','F:mesh','F:limit_count','F:isNewbie','F:Newbie_Skill','F:Newbie_formation','F:m_scenarioClickF','F:m_scenarioVisible','F:m_scenarioAttackF','F:m_scenarioVictoryF','F:m_scenarioFailure','F:m_scenarioSystem')
    'Database.Character'=@('F:id','F:level','F:idcampaign','F:gm')
    'game.net.data.StoC.SCBattleEnd_0x408'=@('P:result','P:type','P:iswin','P:idcampaign','P:idtroop','P:idbattle_report')
}
$checks = [ordered]@{}
foreach ($type in $requirements.Keys) { foreach ($member in $requirements[$type]) { $checks["${type}::$member"] = $null -ne (Find-Member $type $member) } }
$identity = Find-Member 'Database.Character' 'F:id'
$checks['character_id_exact_u64_token'] = $null -ne $identity -and $identity[0] -eq 'Database._character' -and $identity[3] -eq '040001f6' -and $identity[4] -eq '06-0B'
$packetGetterIl = [BridgeCampaignMetadata]::MethodIl((Join-Path $ManagedDir 'Assembly-CSharp.dll'), 0x060023d7)
$checks['packet_getter_pure_field_return'] = $packetGetterIl -eq '02-7B-34-21-00-04-2A'
$fixedFields = [ordered]@{
    'Database.CampaignTroop:F:data_'='06-12-84-E5'
    'Database.Character:F:idcampaign'='06-07'
    'Database.CampaignTroop:F:click_mode'='06-05'
    'Database.CampaignTroop:F:limit_count'='06-06'
    'Database.CampaignTroop:F:finish_max'='06-05'
    'Database.CampaignTroop:F:isNewbie'='06-05'
    'Database.CampaignTroop:F:Newbie_Skill'='06-07'
    'Database.CampaignTroop:F:Newbie_formation'='06-09'
}
foreach ($field in @('m_scenarioClickF','m_scenarioVisible','m_scenarioAttackF','m_scenarioVictoryF','m_scenarioFailure','m_scenarioSystem')) { $fixedFields["Database.CampaignTroop:F:$field"]='06-09' }
foreach ($key in $fixedFields.Keys) {
    $parts=$key.Split(':',2); $member=Find-Member $parts[0] $parts[1]
    $checks["diagnostic_signature:$key"]=$null -ne $member -and $member[4] -eq $fixedFields[$key]
}
foreach ($field in @('result','type','idcampaign','idtroop')) {
    $member=Find-Member 'game.net.data.StoC.SCBattleEnd_0x408' "P:$field"
    $checks["settlement_packet_uint32:$field"]=$null -ne $member -and $member[4] -eq '28-00-09'
}
$getterEvidence = [ordered]@{}
foreach ($type in @('Database.Campaign','Database.CampaignTroop')) {
    $names = if ($type -eq 'Database.Campaign') { @('unlocked','finished') } else { @('canattack','killed','clicked','finish_count') }
    foreach ($name in $names) {
        $getter = Find-Member $type "M:get_$name"
        $token = [Convert]::ToInt32($getter[3],16)
        $chain = [BridgeCampaignMetadata]::PureTwoFieldRead((Join-Path $ManagedDir 'Assembly-CSharp-firstpass.dll'),$token)
        $expected = "${type}::data_ > Database._character_campaign" + $(if ($type -eq 'Database.CampaignTroop') {'_troop'} else {''}) + "::$name"
        $checks["pure_getter:${type}::$name"]=$chain -eq $expected
        $getterEvidence["${type}::$name"]=[ordered]@{token=$getter[3];signature=$getter[4];raw_il=[BridgeCampaignMetadata]::MethodIl((Join-Path $ManagedDir 'Assembly-CSharp-firstpass.dll'),$token);field_chain=$chain}
    }
}
$signatures = [ordered]@{
    'MainUI:M:_campaign_check'='20-01-02-0E'
    'MainUI:M:_campaign'='20-00-01'
    'CampaignUI:M:wantCombat'='20-01-01-05'
    'CampaignTroopUI:M:BattleFormation'='20-02-01-02-05'
    'CampaignTroopUI:M:OnAttack'='20-00-02'
    'FormationUI:M:OnClickSetCurrentForm'='20-00-01'
    'ScenarioUI:M:scenarioSkip'='20-01-01-11-65'
    '__BaseUI:M:_rectRB'='00-04-11-61-0C-0C-0C-0C'
}
foreach ($key in $signatures.Keys) {
    $parts=$key.Split(':',2); $member=Find-Member $parts[0] $parts[1]
    $checks["signature:$key"]=$null -ne $member -and $member[4] -eq $signatures[$key]
}
$captureEvidence = [ordered]@{}
$captureSignatures = [ordered]@{
    'RootUI:F:m_client'=@('04001aac','06-12-82-C5')
    'game.net.tcp_base:F:messages'=@('040016a4','06-15-12-15-01-12-88-F0')
    'game.net.tcp_base:F:m_handlers'=@('0400169d','06-15-12-19-02-07-12-88-E8')
    'game.net.tcp_base+HandleMessage:F:op'=@('040016b3','06-07')
    'game.net.tcp_base+HandleMessage:F:stream'=@('040016b4','06-12-81-A9')
    'JSProtoBuf:F:wrapper'=@('04000c28','06-12-85-D0')
    'JSProtoBuf+PBSerializerWrapper:M:Deserialize'=@('060008d1','30-01-01-1E-00-12-81-41')
    'Database.Character:F:gm'=@('040001fe','06-02')
}
foreach ($key in $captureSignatures.Keys) {
    $parts=$key.Split(':',2); $member=Find-Member $parts[0] $parts[1]; $expected=$captureSignatures[$key]
    $checks["capture_exact:$key"]=$null -ne $member -and $member[3] -eq $expected[0] -and $member[4] -eq $expected[1]
    $captureEvidence[$key]=$member
}
$firstpassPath=Join-Path $ManagedDir 'Assembly-CSharp-firstpass.dll'
foreach ($entry in @(@('game.net.tcp_base','Parse'),@('game.net.tcp_base','Run'),@('game.net.tcp_base+HandleMessage','.ctor'),@('JSProtoBuf+PBSerializerWrapper','Deserialize'))) {
    $member=Find-Member $entry[0] ('M:'+$entry[1]); $raw=[BridgeCampaignMetadata]::MethodIl($firstpassPath,[Convert]::ToInt32($member[3],16))
    $captureEvidence[($entry[0]+'::'+$entry[1])]=[ordered]@{token=$member[3];signature=$member[4];raw_il=$raw}
    if ($entry[1] -in @('Parse','Run')) { $checks[('same_client_monitor:'+$entry[1])]=$raw.Contains('02-28-34-1C-00-0A') -and $raw.Contains('02-28-36-1C-00-0A') }
    if ($entry[1] -eq 'Deserialize') { $checks['copy_parser_pure_forward']=$raw -eq '03-28-D1-00-00-2B-2A' }
}
$checks['monitor_enter_exact_target']=[BridgeCampaignMetadata]::MethodTarget($firstpassPath,0x0a001c34) -eq 'System.Threading.Monitor::Enter'
$checks['monitor_exit_exact_target']=[BridgeCampaignMetadata]::MethodTarget($firstpassPath,0x0a001c36) -eq 'System.Threading.Monitor::Exit'
$checks['copy_parser_exact_target']=[BridgeCampaignMetadata]::MethodTarget($firstpassPath,0x2b0000d1) -eq 'ProtoBuf.Serializer::Deserialize'
$protobufHash=(Get-FileHash -LiteralPath (Join-Path $ManagedDir 'protobuf-net.dll') -Algorithm SHA256).Hash.ToLowerInvariant()
$checks['pure_protobuf_dependency_hash']=$protobufHash -eq 'e3f060f578bc4b6d0c4715ef731a1fba4dd04389902d12e1a8a73b788b9d838b'
$introEvidence=[ordered]@{}
$introFields=[ordered]@{
    'ScenarioUI:F:m_bMoving'=@('04001ae2','06-02')
    'ScenarioUI:F:m_bWantNewUI'=@('04001ae0','06-02')
    'ScenarioUI:F:m_bTrigger'=@('04001adc','06-02')
    'ScenarioUI:F:m_bFirst'=@('04001ad5','06-02')
    'ScenarioUI:F:OnClose'=@('04001504','06-12-89-78')
    'ScenarioUI+newUIBtn:F:name'=@('04001ae9','06-0E')
    'ScenarioUI+newUIBtn:F:width'=@('04001aea','06-0C')
    'ScenarioUI+newUIBtn:F:height'=@('04001aeb','06-0C')
    'ScenarioUI+newUIBtn:F:tex'=@('04001aec','06-12-69')
    'ScenarioUI+newUIBtn:F:curPos'=@('04001aed','06-11-81-09')
    'ScenarioUI+newUIBtn:F:targetPos'=@('04001aee','06-11-81-09')
    'ScenarioUI+newUIBtn:F:bMovingEnd'=@('04001aef','06-02')
}
foreach ($key in $introFields.Keys) {
    $parts=$key.Split(':',2); $member=Find-Member $parts[0] $parts[1]; $expected=$introFields[$key]
    $checks["intro_exact:$key"]=$null -ne $member -and $member[3] -eq $expected[0] -and $member[4] -eq $expected[1]
    $introEvidence[$key]=$member
}
$introMethods=@(
    @('Update',0x06001f22,'a3c38279811ac640fb4a039d8b345668624e7e555fcc94fc377a705ddc32e217'),
    @('Close()',0x06001f25,'97d1ae2dcf24920217fcbed6a346f3d5c95bc5375b502dbfc50b533ce16c433e'),
    @('Close(Rect)',0x06001f26,'d980e079e92dedf94a364ead1c49f1b04910e75bf56e0104f0e9231514723610'),
    @('Web_OnGUI',0x06001f27,'7e79a1ebb2f9dad7f9116f718be9f99a152a44f4e8a0b68f75ab53287d493231'),
    @('FunctionLoad',0x06001f29,'940c99d1aa6b9342258dd965b0cf1185353e50a0db0be6138f8002491b4ec383'),
    @('cs_scenario_end',0x06001f2b,'2920599b38f5970f4589565c6fab376793edef765ee72f4f2e1d3d82c312ce17'),
    @('sc_scenario_end',0x06001f2c,'4b908d70524085a9dc24b2a5e4f7a82c23e554e540fa859e8562d776ca822d21'),
    @('scenarioSkip',0x06001f2d,'477931fa25a964a4479a3cfa4a48e9673dd93d7f698625c37d9d2350281dc85b')
)
foreach ($entry in $introMethods) {
    $raw=[BridgeCampaignMetadata]::MethodIl((Join-Path $ManagedDir 'Assembly-CSharp.dll'),$entry[1])
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $digest=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($raw))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $checks[('intro_il:ScenarioUI::'+$entry[0])]=$digest -eq $entry[2]
    $introEvidence[('ScenarioUI::'+$entry[0])]=[ordered]@{token=('{0:x8}' -f $entry[1]);raw_il=$raw;il_hex_ascii_sha256=$digest}
}
$generalEvidence=[ordered]@{}
$generalFields=[ordered]@{
    'SpecialGetUI:F:showtype'=@('040010b1','06-0E')
    'SpecialGetUI:F:tempid'=@('040010b8','06-09')
    'SpecialGetUI:F:showFormClose'=@('040010ba','06-02')
    'Database.Character:F:form_generals_'=@('0400047b','06-15-12-19-02-07-12-83-48')
    'Database.Character:F:formations_'=@('04000486','06-15-12-19-02-08-12-82-FC')
    'Database.Character:F:cur_form'=@('04000487','06-08')
    'Database.FormGeneral:F:idgeneral_template'=@('04000504','06-07')
    'Database.Formation:F:id'=@('040006b1','06-08')
    'Database.Formation:F:spotnum'=@('040006b2','06-08')
    'Database.Formation:F:spotgen'=@('040006b4','06-15-12-15-01-08')
    'FormationUI:F:CharFormation'=@('04000e2b','06-12-85-4C')
    'FormationUI:F:RightPanel'=@('04000e22','06-0E')
    'FormationUI:F:OnClose'=@('04001504','06-12-89-78')
    'FormationUI:F:showCloseTip'=@('04000e47','06-02')
    'FormationUI:F:m_lastparam'=@('04000e49','06-12-80-89')
    'FormationUI+FormationInfo:F:formid'=@('04000e93','06-08')
    'FormationUI+FormationInfo:F:dbspot'=@('04000e8b','06-15-12-31-01-08')
    'FormationUI+FormationInfo:F:formgenpos'=@('04000e8c','06-14-11-85-48-02-00-00')
    'FormationUI+formgeninfo:F:genid'=@('04000e85','06-0A')
    'Global:F:m_client'=@('04000937','06-12-88-D8')
    'MainUI:F:m_subui'=@('0400143c','06-12-05')
    'DelegateFunc:F:MainUI_formation_ui'=@('04000659','06-12-89-AC')
    'BaseUI:F:rootUI'=@('04000353','06-12-88-98')
    '__BaseUI:F:enable'=@('04001673','06-02')
    'RootBase+enumGeneralAutoEquipMode:F:Attack'=@('04000f7e','06-11-86-AC')
}
foreach ($key in $generalFields.Keys) {
    $parts=$key.Split(':',2); $member=Find-Member $parts[0] $parts[1]; $expected=$generalFields[$key]
    $checks["general_exact:$key"]=$null -ne $member -and $member[3] -eq $expected[0] -and $member[4] -eq $expected[1]
    $generalEvidence[$key]=$member
}
# The Assembly hashes pin the full source of every method. These fixed raw IL
# digests separately freeze the GUI branch, ordinary save callback and delegate
# target used by the teaching adapter; this is file-backed metadata only.
$generalMethods=@(
    @('SpecialGetUI','Web_OnGUI','Assembly-CSharp-firstpass.dll',0x06000c23,'20-00-01','71ac24caf204a7eb8f77ffa070827e87cd3a9df6408c68a5122088f4fa5fe789'),
    @('SpecialGetUI','ShowSPGet_General','Assembly-CSharp-firstpass.dll',0x06000c16,'20-01-01-09','f51aaea3ed23a17bb89d9e61f192d3877f5935a57b27c9b9fcb2d2f7f124adf2'),
    @('SpecialGetUI','Hide','Assembly-CSharp-firstpass.dll',0x06000c24,'20-00-01','899d3d5acbdb236c2c0b84d82945b501e0e648055d8bd5484287a7c3c33a34be'),
    @('Database.Character','AddOneGenToCurForm','Assembly-CSharp-firstpass.dll',0x0600034d,'20-01-02-09','47b70861fc87da3e88f98f1cea7d1c2f82f433bcbc9560fcf51b3833aa97d82f'),
    @('RootBase','cs_general_auto_equip','Assembly-CSharp-firstpass.dll',0x06000b45,'00-02-01-08-11-86-AC','5876a662ce2fe7e33a944e581f0e474f7eb5e1e9a6ed90d46240e5b01c984b32'),
    @('DelegateFuncUI','MainUI_formation_ui','Assembly-CSharp.dll',0x06000950,'00-01-01-02','e9026880d5e2958788c150e0d736dd4602bbfd25819d201057441bef68163174'),
    @('FormationUI','OnClickClose','Assembly-CSharp.dll',0x06001468,'20-00-01','20ae4d993de20f534cfac4d67c04dc31b9cf345c4d15c1858394f515556910f5'),
    @('FormationUI','OnClickSetCurrentForm','Assembly-CSharp.dll',0x0600144d,'20-00-01','9dd9a1b245003ef825fa586d3b88b664a7e958aaa056931d48be98b3931a3fb2'),
    @('FormationUI','sc_formupdate','Assembly-CSharp.dll',0x0600148a,'20-02-01-12-80-ED-1C','d0e0ea8af9739b536a66eb344234baef874683722dbc167f1d7da9d51b94e991'),
    @('FormationUI','Web_OnGUI','Assembly-CSharp.dll',0x06001448,'20-00-01','21bd9e2f1f38f43cea4046952cb87307b38173222e09415dba08e4639602f127'),
    @('BaseUI','GetRootUI','Assembly-CSharp.dll',0x06000309,'00-00-12-88-98','9f99bc82cecf2017b1c355f4f2eccf4b94f2e630458123c5fc7278c5d8f8daa3'),
    @('MainUI','handle_sub_ui','Assembly-CSharp.dll',0x06001842,'00-02-02-12-05-0E','ecdc6d55ee349125783c6b21e7d0ba3166cf300ca515f86ef8fe0839ec3c0d3e')
)
foreach ($entry in $generalMethods) {
    $member=Find-Member $entry[0] ('M:'+$entry[1]); $token=('{0:x8}' -f $entry[3])
    $checks[('general_method:'+$entry[0]+'::'+$entry[1])]=$null -ne $member -and $member[3] -eq $token -and $member[4] -eq $entry[4]
    $raw=[BridgeCampaignMetadata]::MethodIl((Join-Path $ManagedDir $entry[2]),$entry[3])
    $sha=[Security.Cryptography.SHA256]::Create()
    try { $digest=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::ASCII.GetBytes($raw))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    $checks[('general_il:'+$entry[0]+'::'+$entry[1])]=$digest -eq $entry[5]
    $generalEvidence[($entry[0]+'::'+$entry[1])]=[ordered]@{token=$token;signature=$entry[4];raw_il=$raw;il_hex_ascii_sha256=$digest}
}
$failed = @($checks.Keys | Where-Object { -not $checks[$_] })
$selectionMethods = [ordered]@{}
foreach ($entry in @(
    @('CampaignUI','wantCombat','Assembly-CSharp.dll'), @('CampaignUI','wantTroopUI','Assembly-CSharp.dll'), @('CampaignUI','createATroop','Assembly-CSharp.dll'),
    @('CampaignTroopScript','MyMouseDown','Assembly-CSharp.dll'), @('CampaignTroopScript','Web_OnGUI','Assembly-CSharp.dll'),
    @('Database.CampaignTroop','make','Assembly-CSharp-firstpass.dll'), @('CampaignChapterClearUI','Web_OnGUI','Assembly-CSharp.dll')
)) {
    $member=Find-Member $entry[0] ('M:'+$entry[1]); if ($null -eq $member) { throw "Missing named selection evidence: $entry" }
    $selectionMethods[($entry[0]+'::'+$entry[1])]=[ordered]@{declaring_type=$member[0];token=$member[3];signature=$member[4];raw_il=[BridgeCampaignMetadata]::MethodIl((Join-Path $ManagedDir $entry[2]),[Convert]::ToInt32($member[3],16))}
}
$result = [ordered]@{status=$(if ($failed.Count) {'FAIL'} else {'PASS'}); scope='file-backed PEReader metadata only; no game assembly load or process access'; input_hashes=$hashes; protobuf_dependency_sha256=$protobufHash; checks=$checks; failures=$failed; diagnostic_getters=$getterEvidence; selection_method_evidence=$selectionMethods; settlement_capture_evidence=$captureEvidence; scenario_intro_evidence=$introEvidence; general_tutorial_evidence=$generalEvidence}
$result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputPath -Encoding utf8
[ordered]@{status=$result.status; checks=$checks.Count; failures=$failed; game_access=$false} | ConvertTo-Json -Depth 5
if ($failed.Count) { throw 'Campaign fixed metadata gate failed' }
