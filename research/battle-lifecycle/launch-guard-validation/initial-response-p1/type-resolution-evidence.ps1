$assembly=[Reflection.Assembly]::ReflectionOnlyLoadFrom('C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed\Assembly-CSharp-firstpass.dll')
@('game.net.data.StoC.SCBuildingMonsterBattle_0x48D','ProtoBuf.Serializer+NonGeneric','Ionic.Zlib.ZlibCodec') | ForEach-Object {
    [ordered]@{name=$_;resolved_in_firstpass=($null -ne $assembly.GetType($_,$false))}
} | ConvertTo-Json
