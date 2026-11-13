# Cached soul gathering parameters

The installed cache `C:/Users/yu/AppData/LocalLow/GMarkSoft/______/assets/db_template2.unity3d`
had SHA256 `472c40e57616e5ec65c2c7217f26e44b3fd69a3b7a4fc5007729e5047b330d33`.
Its UnityWeb payload was decompressed in memory and decoded against the installed
`dbdata.dll` protobuf field declarations. These are cached client parameters,
not proof of current server behavior.

- Ordinary start mode 1: price 10,000 money, one free start, growth multiplier 1.
- Ordinary start mode 2: price 50,000 money, one free start, growth multiplier 1.
- Ordinary growth mode 1: 3,000 money. Other grow modes shown in the table cost gold.
- `general_soul_grow_rate`: growth 0–5 has weights 15, 25, 25, 25, 5, 5.
- `general_soul_level_rate`: initial levels 1–10 have weights 10, 20, 30, 20, 10, 5, 5, 0, 0, 0.
- `general_soul_type_rate`: start types 1/4/6/8 have blue/purple/orange weights 90/10/0;
  types 2/5/7/9 have 0/85/15; type 3 has 0/0/100.

The game client reads server-provided growth steps and final awards. The tables
alone do not establish the complete state transition, overflow/explosion rules,
or the relationship between level and harvest quantity. In the extracted
`data/campaign-research/GeneralSoulGatherUI.cs.txt`, an explosion can be rendered
when an existing level greater than 1 becomes level 1; it cannot simply be
assumed to empty the slot. An eight-gate description table also exists, but the
available client code does not establish that those modifiers are active.

The exchange UI sends 1,000 or 5,000 units depending on the chosen exchange;
those request quantities do not establish the exchange output or a common value
for different soul colors. Until the transition rules, harvest function and
color valuation are known, the current ordinary-growth rule is a heuristic,
not a proven maximum-return policy. Record actual per-color awards and money
spent rather than combining unlike colors into an unsupported scalar reward.
