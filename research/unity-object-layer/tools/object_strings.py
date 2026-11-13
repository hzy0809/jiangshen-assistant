"""Offline ASCII metadata-name candidate scan; never loads the managed assembly."""
import pathlib
import re

managed = pathlib.Path(r"C:\Users\yu\AppData\Local\JiangShen\pc\js_Data\Managed")
output = pathlib.Path(__file__).resolve().parents[1] / "object-map-name-candidates.txt"
pattern = re.compile(r"(player|role|fight|battle|scene|button|onclick|uimanager|window|panel|resource|gold|money|level|singleton)", re.I)
with output.open("w", encoding="utf-8") as out:
    for name in ("Assembly-CSharp.dll", "Assembly-CSharp-firstpass.dll"):
        out.write(f"## {name}\n")
        words = {m.group().decode("ascii") for m in re.finditer(rb"[A-Za-z_][A-Za-z0-9_.$`+<>]{3,120}", (managed / name).read_bytes())}
        out.writelines(word + "\n" for word in sorted(words) if pattern.search(word))
print(output)
