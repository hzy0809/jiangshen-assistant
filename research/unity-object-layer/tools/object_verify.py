"""Targeted evidence consistency checks; reads DLL bytes and saved decompilation only."""
from pathlib import Path
import hashlib
import json

root = Path(__file__).resolve().parents[1]
hashes = json.loads((root / "object-map-input-hashes.json").read_text(encoding="utf-8-sig"))
checks = []
for item in hashes:
    checks.append({"check": "input hash " + Path(item["Path"]).name,
                   "result": "PASS" if hashlib.sha256(Path(item["Path"]).read_bytes()).hexdigest().upper() == item["Hash"] else "FAIL"})
expectations = {
    "object-map-Global.cs.txt": ["public static Character character_ = new Character();"],
    "object-map-character-base.cs.txt": ["public short level;", "public short stamina;", "public int gold;", "public long money64;", "public void Set(db_character d)"],
    "object-map-Database.Character.cs.txt": ["public class Character : _character", "public new long money", "return base.money + money64;"],
    "object-map-LoginUI.cs.txt": ["Global.character_ = new Character();", "Global.character_.Set(sCPlayerInfo_0x.playerInfo);"],
    "object-map-LoadScreen.cs.txt": ["public RootMessenger GetCurUI()", "public bool IsBattling()"],
    "object-map-_ButtonMain.cs.txt": ["public delegate_handle handler;", "public delegate_check checker;", "public Rect rcScreen", "handler();"],
    "object-map-FormationUI.cs.txt": ["private void OnClickClose()", "OnClickSetCurrentForm();", "cs_formupdate(curform: true);", "RootUI.m_client.Send(298, cSFormationUpdate_0x12a, sc_formupdate, curform);"],
    "object-map-MainUI.cs.txt": ["public void luoshuZhankai()", "public void luoshuShouqi()", "component.cs_formation_set_switch((uint)(i + 1));"],
    "object-map-BattleUI.cs.txt": ["public bool BattleFighting => bBattle_fighting;", "public BattleReport.BattleType BattleType => interpreter.packet.type;", "private BattleReportInterpreter2 interpreter;"],
    "object-map-Character.il.txt": ["extends [pbdata]Database._character", "IL_0001: ldfld int32 [pbdata]Database._character::money", "IL_0008: ldfld int64 [pbdata]Database._character::money64", "IL_000d: add"],
    "object-map-typedef-tokens.txt": ["character_ declaredOrdinal0=41 FieldDef=0x04000957 Attributes=Public, Static Signature=06-12-81-C4", "level declaredOrdinal0=14 FieldDef=0x04000204 Attributes=Public Signature=06-06", "stamina declaredOrdinal0=18 FieldDef=0x04000208 Attributes=Public Signature=06-06", "gold declaredOrdinal0=20 FieldDef=0x0400020A Attributes=Public Signature=06-08"],
    "object-map-field-route-candidates.txt": ["SCAN Assembly-CSharp.dll: TypeDefs=790, Fields=9036, directOrGenericMainUIFields=0", "SCAN Assembly-CSharp-firstpass.dll: TypeDefs=697, Fields=6122, directOrGenericMainUIFields=0", "SCAN pbdata.dll: TypeDefs=2404, Fields=11805, directOrGenericMainUIFields=0", "SCAN dbdata.dll: TypeDefs=1195, Fields=8673, directOrGenericMainUIFields=0", ".BaseAsset::lds : .LoadScreen", ".LoadScreen::currmg : .RootMessenger"],
}
for filename, markers in expectations.items():
    content = (root / filename).read_text(encoding="utf-8-sig")
    missing = [marker for marker in markers if marker not in content]
    checks.append({"check": filename, "result": "FAIL" if missing else "PASS", "missing": missing})
result = {"scope": "offline artifact and source identity consistency only; no runtime or game DLL execution", "checks": checks}
(root / "object-map-validation.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"PASS": sum(c["result"] == "PASS" for c in checks), "FAIL": sum(c["result"] == "FAIL" for c in checks)}))
raise SystemExit(any(c["result"] == "FAIL" for c in checks))
