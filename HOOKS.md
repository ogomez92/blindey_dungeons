# Verified hook targets

> **Status: fallback path.** This file describes the original script-mod route, which can
> only see battles. The mod now works through an in-process bridge that reads every screen
> (`CLAUDE.md` §2-3). With the bridge installed, nothing here is used; it remains for a game
> folder where the bridge cannot be installed.

Dicey Dungeons has no method patching, so this is the equivalent of a
`PATCH_TARGETS.md`: every place the game will run mod code, whether this mod uses
it, and what it gets there.

Status values: **verified** = observed firing in a logged run; **registered** =
wired up through the same mechanism as the verified ones but not yet observed,
because reaching it needs a played turn (see §6); **available** = documented by the
game or its source but not used by this mod; **rejected** = tried and found
unusable, with the reason.

---

## 1. Global combat hooks — `Rules.addextrascript(source, when)`

Registered once per run from `flexible_generator.hx`. The first argument is
**inline hscript source**, not a path (see CLAUDE.md §5). The list of valid `when`
values comes from `Rules.hx` and from the error the game throws on a bad value.

| `when` | Fires | Status | Used for |
|---|---|---|---|
| `startcombat` | Once, as a battle begins | **verified** | Battle intro: enemy name, both health totals, enemy card list, starting statuses. Resets the diff baseline. |
| `beforestartturn` | Before each turn's start-of-turn effects | **verified** | Baseline only, so poison/burn ticks are attributed to the turn rather than to the previous action. |
| `onstartturn` | After start-of-turn effects resolve | **verified** | "Your turn" / "<enemy> turn", dice roll, newly ready countdown cards, status changes. |
| `endturn` | End of each turn | **registered** | Changes since the last snapshot. |
| `playerequipmentuse` | Any time the player uses equipment | **registered** | Damage, healing and status changes caused by the action. |
| `enemyequipmentuse` | Any time the enemy uses equipment | **registered** | Same, for the enemy's action. |
| `aftercombat` | Battle resolved | **registered** | Victory / defeat and remaining health. |

In every one of these, the script receives `self` and `target` as
`elements.Fighter`. **They swap** depending on whose turn it is — for the turn
hooks `self` is the fighter whose turn it is. The companion resolves this with the
`isplayer` flag rather than trusting position.

`turn` is also in scope in these scripts (observed in the base game's own
`addextrascript` usages) but is **not** currently read by this mod.

---

## 2. Run-scope hook — `flexible_generator.hx`

| Target | Status | Notes |
|---|---|---|
| `data/text/scripts/diceydungeons/flexible_generator.hx` | **verified** | Called by every generator at run start, after `Rules.reset()`. This mod replaces the file with *its prelude + the game's own copy*, so generation is untouched. It is where the seven hooks above get registered, and where `a11y/run.hx` is invoked. |

Script functions confirmed callable from **generator** scope:

| Call | Status | Result |
|---|---|---|
| `getplayername()` | **verified** | Character name for the run. |
| `getcurrentfloor()` | **verified** | Current floor number. |
| `getfinalboss()` | **rejected** | Returns null here — `"getfinalboss" is a null object`. Not used. |

Confirmed callable from **combat** scope:

| Call | Status | Result |
|---|---|---|
| `getcurrentfloor()` | **verified** | Emitted on every snapshot as `A11Y1\|ctx\|floor=N`; drives the "Floor N" announcement. |

---

## 3. Data-file script columns (available, not used)

These are the per-entity script slots Polymod exposes. Using any of them means
editing or appending to a CSV, which conflicts with content mods; the global hooks
in §1 cover the same ground without that cost. Listed so a future session does not
have to rediscover them.

| File | Script columns |
|---|---|
| `equipment.csv` | On Execute, Before Combat, After Combat, Before Start Turn, On Start Turn, On any equipment use, On any countdown reduce, End Turn, Before execute, On Snap, On Fury, On Dodge |
| `fighters.csv` | Before Combat, After Combat, Before Start Turn, On Start Turn, End Turn |
| `episodes.csv` | Define Level Up Rewards, **Start Game**, After Level Up, **Change Floor**, Before Start Turn, On Start Turn, End Turn, Start Combat, After Combat |
| `statuseffects.csv` | When inflicted, On any status infliction, Before/On Start Turn, On any equipment use, On any countdown reduce, End Turn, After Combat, On damage inflicted, On damage taken, On status remove |
| `skills.csv` | Script |

`episodes.csv` → **Change Floor** and **After Level Up** are the only known hooks
that fire outside battle. They would give floor-change and level-up announcements
for the episodes the mod overrides, at the cost of replacing a 32 KB shared data
file. Not taken; `getcurrentfloor()` in combat scope covers floor changes for free.

---

## 4. Rejected approaches

| Approach | Why it was rejected |
|---|---|
| Harmony / BepInEx style patching | No managed code. `diceydungeons.exe` is a native hxcpp build. |
| Reaching `haxegon.Core.registerplugin` for a per-frame hook | `haxegon` is not in the script whitelist (`Unknown variable "haxegon"`). Measured. |
| `haxegon.Input` for in-game hotkeys | Same. Hotkeys are handled by the companion's low-level keyboard hook instead. |
| File / socket / process I/O from script for the speech bridge | `sys.*`, `Sys`, `Type` and `Reflect` are all outside the whitelist. Measured against 252 identifiers. |
| Reading UI state via `states.*` | Package paths do not resolve as values; only `new states.Foo()` works, which constructs a fresh object rather than reaching the live screen. |
| A long-running script with `delay()` as a poll loop | The game waits for combat scripts to finish before continuing, so a loop would hang the turn. Not attempted in a shipped path. |

---

## 5. Why four hooks are "registered" and not "verified"

All seven are registered by the same `Rules.addextrascript` loop, in one file, and
they all run the same emitter script. Three of them (`startcombat`,
`beforestartturn`, `onstartturn`) fire within two seconds of launch and are
confirmed in logs. The other four only fire once a turn is actually played, and a
turn cannot be played without focused input:

- Dicey Dungeons resolves combat by dragging dice onto cards with the mouse.
- Synthetic input to an unfocused window is discarded by SDL, and
  `SetForegroundWindow` is refused to a background process (CLAUDE.md §6).
- Three script-side ways of ending a battle were tried and all failed:
  `Fighter.endturnnow()` (needs combat phase `allocatedice`, which a `delay()`ing
  script prevents the loop from reaching), `attackself()` (not in scope for
  `addextrascript` snippets — "is a null object"), and `Fighter.requestflee`
  (set, but only consulted during the turn-end flow that never runs).

**How to close this out:** launch through `DiceyA11y.exe`, play one battle to the
end, and check `diceya11y-game.log` for `A11Y1|ev|endturn`,
`A11Y1|ev|playerequipmentuse`, `A11Y1|ev|enemyequipmentuse` and
`A11Y1|ev|aftercombat`, and `diceya11y-spoken.log` for the damage and victory
lines. One battle covers all four.

---

## 6. Test harness targets

Undocumented command-line arguments, found in the exe string table and confirmed
by the log line `Main.hx:431: Quickstarting as Warrior: The Warrior's Welcome`:

```
mod=<ids>  character=<Name>  episode=<n>  enemy=<Name>  boss=<Name>
equipment=<..>  gadget=<..>  allenemies=<..>  remix=<..>
testscenario=<..>  testscenariovariant=<..>  testtag=<..>  modcheats  translator
```

`modcheats` turns on the debug conveniences listed in `data/text/modsettings.json`
(instant win on right click, take/deal damage keys, extra gold).

Game window for synthetic input: class `SDL_app`, title `Dicey Dungeons`.
