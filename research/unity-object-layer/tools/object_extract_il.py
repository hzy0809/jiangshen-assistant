"""Reduce ILSpy's assembly-wide -il output to the owned evidence slice."""
from pathlib import Path

path = Path(__file__).resolve().parents[1] / "object-map-Character.il.txt"
lines = path.read_text(encoding="utf-8-sig").splitlines()
start = next(i for i, line in enumerate(lines) if line.startswith(".class public auto ansi beforefieldinit Database.Character") and lines[i + 1].strip() == "extends [pbdata]Database._character")
end = next(i for i in range(start, len(lines)) if lines[i].strip() == "} // end of class Database.Character")
money_end = next(i for i in range(start, end) if "end of method Character::set_money" in lines[i])
money_start = next(i for i in range(money_end - 1, start, -1) if ".method" in lines[i] and any("get_money ()" in x for x in lines[i:i + 5]))
selected = ["// Offline ILSpy -il selected excerpt; original assembly-wide output line references follow."]
for begin, finish in [(start, start + 3), (money_start, money_end + 1)]:
    selected.append(f"// Original output lines {begin + 1}-{finish}")
    selected.extend(lines[begin:finish])
path.write_text("\n".join(selected) + "\n", encoding="utf-8")
print(f"Saved {len(selected)} IL evidence lines to {path}")
