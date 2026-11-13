# Daily monsters on the current city page

Scope: tasks 10 (friend monsters), 15 (own monsters), and 24 (revenge demon). Implementation/source inspection only; no tests, review, compilation, restart, game/UI operation, or network mutation was performed for this assignment.

## Stock evidence

The `.cs.txt` snapshots here were extracted offline on 2026-11-07 with the existing ILSpy CLI from `C:/Users/yu/AppData/Local/JiangShen/pc/js_Data/Managed`. Fresh SHA256 readings matched the installed pins:

| Assembly | SHA256 |
| --- | --- |
| Assembly-CSharp.dll | c1c8ebac7ddef4173c873a00774975a5ca366f95b19164c4ca775165eb994911 |
| Assembly-CSharp-firstpass.dll | 29d8fa2cb55e800ccdf1cdc6c5b274b924ea7d03c989296dfa569731e02a8d87 |

CapitalFriendUI, BuddyUI, and MainUI come from CSharp; BattleResultUI and MeshScript come from firstpass. Related exact stock extracts already exist in `research/battle-skip/`.

- `../battle-skip/BattleUI.cs.txt:1048-1077`: `battle_exit` restores its recorded `m_prevUI`, broadcasts PrepareLoading/StartLoading/PostInit to that same root, and then invokes its recorded OnClose. This battle/result transition and its asynchronous loading must finish before another target. It does not require an assistant `_capital` navigation.
- `../battle-skip/BuildingMonsterScript.cs.txt:426-484,534-554`: successful settlement binds BonusOpen and a reward close callback to the exact source script. Own BonusClose removes just that monster and enables the page; friend AssistClose enables the page and leaves the defeated target marked dead. Other live monsters on the same Capital or CapitalFriend remain eligible.
- `CapitalFriendUI.cs.txt:23-47,194-207`: same-root loading reconstructs assets from the existing `myfriend` and preserves its killed-target state.
- `BuddyUI.cs.txt:1412-1443`: visiting another friend directly from CapitalFriend is explicitly supported. Stock destroys the old friend city contents, replaces `myfriend`, and runs LevelStart on the current friend root. Visiting from another scene instead uses LoadLevel("CapitalFriend").
- `MainUI.cs.txt:424-442,3963-3969`: the friend list can open on the current scene, through the ordinary sub-UI toggle. BuddyUI's successful visit hides the list without clearing `m_subui`; clear that stale hidden registration before opening it again. A visible existing list is already usable and must not be toggled closed.
- `BattleResultUI.cs.txt:366-376,962-999`: ordinary OK hides the reward and calls its stored OnClose; this callback is necessary settlement/cleanup, not redundant navigation.
- `../battle-skip/DemonScript.cs.txt:447-458`: own demon BonusClose removes the single `character.m_demon`. No second demon is manufactured or replayed. A later absent target or active stock cooldown stops this bounded operation.

## Resulting behavior and contracts

DailyMonsterModel now retains one operation/UI ownership across the remaining daily increments. Each target freezes the source script, owner, root, target, and activity baseline before preparation; its live battle must carry a new stable report and the matching parent/close delegate. A reward can close for the round only through that source's BonusClose/AssistClose.

The next target is selected only after the exact city root is active, input and loading are ready, stock network handlers are idle, reward cleanup has run, the target's stock cleared/dead state agrees, and the daily counter has advanced for that round. Friend targets continue on the same friend page; only exhausting that friend opens the stock friend list to select another. Completion/exhaustion still performs final home cleanup. Missing/changed report ownership or inconsistent settlement stops with UNKNOWN and leaves ownership held, preventing an uncertain mutation from being repeated.

JSON adds `completed_targets` and `round_active`; the command/action schema is unchanged. The Python executor renews its bounded timeout only after `completed_targets` advances. Native timeout likewise renews only after a confirmed target. Task24's existing OwnedBattleReportSource anchors remain in place and are cleared only with the settled round. DailyScriptAdapter must retain a busy monster model when a competing task is observed (controller-owned integration).

Unverified: compilation, Python execution, real repeated battles, server task-counter timing, successful friend-to-friend switching, final cleanup in the game, and actual premium balances. Those claims require the separately authorized controller run; source agreement is not runtime acceptance.
