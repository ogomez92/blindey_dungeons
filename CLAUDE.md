# DiceyA11y — architecture and pitfalls

Screen reader accessibility mod for **Dicey Dungeons**. Read this before changing
anything; a future session starts with no memory of the one that wrote it.

Every fact below was read from the game binary, the published source extracts, or a
logged live run. Anything not verified is labelled **HYPOTHESIS**.

> **History that matters.** The first version of this mod concluded that the game could
> not be injected and shipped battles only, fed by an hscript mod printing to stdout.
> That conclusion was wrong: it examined the script sandbox and stopped. The game loads a
> native library that can be replaced (§3), which gives full in-process reflection. Do not
> repeat the mistake of treating the sandbox as the boundary.

---

## 1. What this game is

| Question | Answer | How it was established |
|---|---|---|
| Engine | **Haxe + OpenFL/Lime + Starling**, on a fork of **haxegon**. Not Unity. | Package roots in the exe string table. |
| Build | Native **hxcpp 4.0.4**, x64, GUI subsystem | PE header; `c:/coding/haxelib/lib/hxcpp/4,0,4/...` paths in the exe. |
| Native libraries | `lime.ndll` (custom build, SDL ≤ 2.0.8 era; matches no haxelib release) and `steamwrap.ndll`, **loaded dynamically** | Files next to the exe; the exe imports only system DLLs. |
| Mod loader | Polymod, mod API 1.0 (data and hscript only) | `data/MODDING.txt`. |
| Source available? | **Nine files only**: `Rules.hx` and eight `elements/*` classes, published as modder reference. The UI (`states/*`, `displayobjects/*`), `Main`, `Game` and the haxegon fork are **not** published, so the game cannot be forked or rebuilt. | `game_src/src/readme.md`. |
| Keyboard navigation | **None.** Menus, map and combat are mouse or gamepad only. Arrow-key `KeyboardEvent`s dispatched to the stage do nothing. Esc opens options; that is all. | Measured through the bridge. |
| Gamepad navigation | **Complete** (the game shipped on consoles): `ButtonGamepadNavigation`, `GamepadManager`, `ControlMode`. | Live run. |
| Localization | `MyLocale.translate(key)`; `MyLocale.currentlanguage`. Internal names (button labels, enemies, equipment, statuses) are English keys. | Live run. |
| Rendering | Immediate mode, rebuilt every frame into a Starling display list under `Screen.stage`. Canvas is always 3840×2160. | Live run. |

Navigation tier (playbook §1): **tier B** everywhere — the game has its own navigation
system that the keyboard does not drive — except the map, which is effectively tier C
(§6).

---

## 2. Architecture

```
 Dicey Dungeons process                          DiceyA11y.exe (.NET 9, x64)
 ┌───────────────────────────────┐   named pipe  ┌──────────────────────────────────┐
 │ lime.ndll  = our bridge       │◀─────────────▶│ BridgeClient   requests/replies  │
 │   forwards 4445 exports to    │  reflection   │ ScreenMonitor  polls, routes      │
 │   lime_orig.dll (the real one)│  protocol     │ ScreenHandler  generic fallback   │
 │   + reflection server on the  │               │  Dungeon/Combat/LevelUp handlers  │
 │     game's main thread        │               │ BattleNarrator diffs fighters     │
 └───────────────────────────────┘               │ VirtualPad     keys -> gamepad    │
                                                 │ HotkeyHook     low-level keyboard │
                                                 │ ScreenReaderOutput -> prism.dll   │
                                                 └──────────────────────────────────┘
```

- **bridge/** (C++, MSVC) knows nothing about Dicey Dungeons. It evaluates expressions
  against live Haxe objects and returns values. All game knowledge is in the companion,
  so wording and screen logic change without touching native code or restarting the game.
- **companion/** owns the keyboard, decides what to say, and speaks through Prism (Windows) or AVSpeech (macOS, §9).
- **mod/** (Polymod hscript) is the *old* battle-only path. It is now a fallback used only
  when the bridge is not installed; with the bridge present its output is ignored.

---

## 3. The bridge (how code gets into a native game)

`lime.ndll` is loaded by hxcpp's CFFI loader (`src/hx/Lib.cpp`), which calls the library's
exported `hx_set_loader(resolver)` and thereby hands it **the entire CFFI API**:
`val_field`, `val_call*`, `val_iter_fields`, `alloc_*`, GC roots. That is reflection over
every Haxe object in the process, given to us by the game.

The bridge replaces `lime.ndll`, keeps the original as `lime_orig.dll`, forwards every
export to it, and intercepts three:

| Export | Why |
|---|---|
| `hx_set_loader` | Receives the CFFI API. Called once per library *name*, so guard against double init. |
| `lime_application_event_manager_register__prime` | Its callback argument is a bound method of Lime's `NativeApplication`: a live Haxe object to start from. |
| `lime_window_context_flip__prime` | Called by Haxe once per frame on the main thread: our tick. Haxe values are only ever touched here. |

`__prime` getters return an abstract of kind `0x102` wrapping the raw function pointer;
the hook reads it with `val_data` and returns its own abstract around the hook function.
If the signature string is not the expected one (`oov`, `ov`) it passes the original
through untouched.

**Getting from one object to any class.** The closure's receiver sits at `+8` (hxcpp
objects are 4-aligned, not 8 — they follow a 4-byte GC header). From it:
`parent.window.stage.__children[0].__loaderInfo.applicationDomain.getDefinition(name)`
is OpenFL's wrapper over `Type.resolveClass`. Static fields are then plain `val_field`
reads on the Class object.

### Facts about this hxcpp build that cost time

- `val_type` reports **8 for an instance of a class**, and `4` for both anonymous objects
  *and Class objects*. hxcpp's header calls 8 "valtClass", which is misleading.
- A dynamic field read does **not** run property getters. The bridge falls back to
  `get_<name>()` when a field reads null (needed for `Game.player`, `Game.monster`).
- `Type.getClassFields` is stripped from this build, so **statics cannot be listed**.
  They are found by trying names: `tools/bridge.py probe "{Class}" words.txt`, with a word
  list cut from the exe's string table. `Type.getInstanceFields`, `createInstance` and
  `getClassName` are present.
- The exe embeds a `pkg.Class.method` string for every function (stack frames): a
  complete class and method inventory, though not field names.
- Scene classes (`states.*`) keep **all state in statics**; the scene instances in
  `haxegon.Scene.scenelist` have no fields.
- MSVC accepts export forwarders only as `#pragma comment(linker, "/export:a=b.a")`. The
  same forwarders in a `.def`, a response file or on the command line fail as unresolved
  externals. `build.ps1` therefore generates `forwards.h`.
- Build with `/EHa`: Haxe exceptions are C++ exceptions and a bad pointer is an SEH fault;
  `catch (...)` must stop both or a bad request takes the game down.

### Protocol

One request per line, `id TAB command TAB argument [TAB extra]`; reply `id TAB result`,
where a result starting with `!` is an error. Up to four clients at once (the companion
plus developer tools). Commands: `eval`, `batch` (same frame), `dump`, `pluck` (paths
over every element of an array, one request), `stagetext`, `iter`, `set`, `seti`,
`members`, `probe`, `peek`/`adopt` (raw layout work), `ping`.

Expressions: `{pkg.Class}` class, `#12` handle (`#0` = Lime application), `.field`,
`[index]` or `["key"]`, `(args)` call, literals, `[a, b]` array literal.
Values: `n`, `i:`, `f:`, `b:`, `s:`, `o:#h:Class`, `c:#h:Class`, `a:#h:len`, `fn:#h:n`,
`e:#h:Name`. Handles live in a 16384-slot ring and expire; never keep one across polls.

---

## 4. Input: the virtual gamepad

`GamepadManager.gamepads` holds a `GamepadState` per connected pad and the game folds
them into `GamepadManager.global` every frame (writing to `global` directly is
overwritten — measured). The companion pushes a `GamepadState` created with
`Type.createInstance(GamepadState, [])` (no Lime gamepad behind it, `gamepad == null`,
which is also how it finds its own slot again) and calls `onButtonDown(n)` /
`onButtonUp(n)` / `onAxisMove(axis, value)` on it — exactly what Lime's events do.

Button numbers are Lime's `GamepadButton`: A0 B1 X2 Y3 Back4 Start6 LB9 RB10 Up11 Down12
Left13 Right14. Axes: 0/1 left stick, 2/3 right stick.

Keys are held and released on the pad as pressed, so the game's own hold-to-repeat
applies. The keyboard hook swallows them, so nothing acts twice. Default map: arrows =
d-pad, Enter/Space = A, Escape = B, E = X, R = Y, Q/W = shoulders, F11 = Start,
Backspace = Back. Space is A everywhere but
the map (there it recentres the map cursor); comma, period and P exist only for the map and
never reach the pad. Tab / Shift+Tab are the mod's own
(`NavKey.GroupNext/GroupPrevious/ItemNext/ItemPrevious`, `CombatGroups.cs`): in battle Tab
moves between four groups (enemy cards, enemy statuses, your statuses, your actions) and Up /
Down within one whenever the card list has the arrows (`CardListActive`; Left / Right stay the
card list, Up / Down used to be their twins); every item fills the item review list with its details plus keyword
lines (`StatusKeywords`: the status effects behind the icons in the card's raw text, from
`data/text/statuseffects.csv`, translated by the game). Enemy cards are read from the card
objects, never the stage (off the preview they are parked over the player's cards). In battle
the tooltips are no longer put on the item list: the limit break's tooltip read as if it
belonged to whatever card was focused. (Tab used to hold the right stick up, the game's
on-screen preview: every card was read in one go, and the release handler slept 1.6 s on the
key-action queue, so the next arrow waited behind it.)
Left Control stops speech through `HotkeyHook.BindObserved`: the hook runs the action and
passes the key on, so it is still the Control of Ctrl+D and Alt+Tab.
Ctrl+D / Ctrl+H / Ctrl+E read dice, health and enemy health. The title's own
"(Esc) Options" hint is therefore wrong under the mod; `StageCleaner` rewrites it to F11.

**Both stick axes must be set in the same frame** (one `batch`). With x set a frame
before y the game reads a horizontal push and walks to the wrong map node (seen).

---

## 5. Reading the screen

- **Focus**: `ButtonGamepadNavigation.activebutton` (`label`, `x`, `y`) and
  `buttonrecords`. Labels are internal keys → `MyLocale.translate`. Each button appears
  twice (this frame and last), a few pixels apart while it slides in.
- **Text**: every visible `starling.text.TextField` under `Screen.stage` (`_text`, `__x`,
  `__y`, `_textBounds`). `stagetext` walks it natively. Text revealed gradually reports
  empty bounds. Off-canvas text is mid-animation and is ignored.
- `StageWatcher` builds paragraphs (stacked lines sharing a left edge or a centre, never
  merging a control's label) and reports what is new once the stage has been still for
  ~320 ms. "New" is by content, not position. A paragraph that grew a line yields only
  the new line.
- `StageCleaner` handles the game's markup: layout/colour/size tags dropped, `[x_button]`
  glyphs become the bound key, `[gold]12` becomes "12 gold", `[comma]`, language-name
  images, and stick wording rewritten to arrow keys per language.

### The cost of reading, and the frame cache

A request is answered on the game's next frame, so **sequential requests cost a frame
each** (measured: 30 evals one after another 461 ms, the same 30 sent together 19 ms; the
bridge serves up to 64 per tick). A battle poll makes ~27 requests, which used to mean a
poll of 450 ms and over a second from an arrow key to speech. `BridgeClient.OpenScope`
fixes that without touching the handlers: inside a scope every pure read is remembered,
and the poll's scope first sends, all at once, the reads the previous poll made. Rules
that follow from it:

- A read is only replayed if it is pure: no `(` except the whitelisted getters in
  `BridgeClient.PureCalls`, no handles except `#0`. Add a getter there before using it in
  a poll. Any other request empties the cache (something is about to change).
- A read that must be instant when the player presses a key has to be made on *every*
  poll, not only when the cursor moves, or it will not be in the prefetch. That is why
  `CombatHandler.OnPoll` describes the selected card each time and throws it away.
- Learned lists are kept **per key** (`poll:<scene>`, `key:<scene>`): a key press has a
  learning scope of its own, so the handler's reads (card list, floor) cost one frame. With
  one shared list the two scopes wiped each other and every poll after an arrow key cost
  600 ms (measured 2026-09-22, then fixed: 3-20 ms after the first key on a screen).
- The poll loop waits on `_wake` (20 ms) and a key press sets it, so what the key changed is
  read on the very next frame instead of after the pause.
- `tools/latency.py Right Left ...` measures key-to-speech; `tools/bridgeperf.py` the raw
  round trip; the log says `Slow poll` with the reads that were not sent ahead. Set
  `DICEY_LOG` to the installed copy's log when testing that one.

### Review lists (`BufferManager`)

The first list, *item*, follows focus: `ctx.FocusItem(lines)` fills it and makes it the
current list (silently), with the screen's tooltips on top. Then events, player (health,
gold, level, statuses), your cards, enemy cards, dice (only dice still free: not assigned,
not `consumed`), status effects. Help is hidden except between F1 and moving off it.

### Generic fallback (`ScreenHandler`)

Every scene without a dedicated handler gets: its name, its text, the focused control;
on focus change the label (plus same-row text and role for sliders, state for known
checkboxes); any text that newly appears. This is what reads the title, options,
credits, cutscenes, item pickup, character select, episode select and the end-of-run
screen today. Rules learned from the second play-test:

- **Speech order.** Focus echoes go through the priority queue and jump ahead of events.
  A focus echo made in the same poll as newly read text goes through `SayAfterEvents`
  (events queue, not recorded) so "You get an extra dice" is heard before "Take".
- **The same caption at another place** is only news once it rests and its old place is
  empty (`SameButtonMoving`, `ButtonAt`): a popup sliding away with its OK still focused was
  heard as "Ok, 1 of 2. Ok, 1 of 2. Ok" (FocusKey carries the position while twins exist).
- **A lone button is what Enter does**: `PressEnterTo` says "Press Enter to take" / "Pulsa
  Intro para coger"; short labels stay "Ok, press Enter".
- Tutorial lines that point at the screen get the key appended (`TutorialHints`: R for the
  inventory, Tab for the enemy's moves), matched on the cleaned text in en and es.
- The map's node prompts ("MEJORAR", "ABRIR", "A LUCHAR") are drawn text, not nav buttons:
  `activebutton` is null there, and they are read as paragraphs.

---

## 6. Screens with their own handler

**Map (`DungeonHandler`)** — `states.Dungeon.playerposition` and
`floor[Game.currentfloor].nodes` are `elements.GraphNode`s: `x`, `y`, `type`
(`elements.DungeonBlockType`), `enemytemplate.name`, `visited`, `connections`.
Arriving on a node does nothing; A acts on it. **The d-pad cannot reach every neighbour**:
neighbours are diagonal, "up" ties between up-left and up-right, and on a tie the game
does not move. A 100 ms stick push at the exact angle moves one step to exactly that
node. So the mod owns movement here, through a cursor of its own that never moves the
player. Paths run in exactly six directions ((+-480, +-240) and (0, +-480); counted with
`tools/mapoffsets.py`), so a node has at most one neighbour in each, and the arrows follow
paths only: Up/Down take the straight path if there is one, otherwise any arrow takes the
single path on its side, and with two paths on that side both are named and the other pair
of arrows picks (`_fork`). Direction words go by the signs of the offset, not its angle, so
"up right" is always Up and Right in either order. (A first version searched a cone for
the nearest node; an exit spoken as "up right" lies 27 degrees above the horizontal and did
not answer to Up, which the player rightly called unreliable.) Comma/period cycle the points
of interest (non-empty spaces: exits first, then the rest with Dijkstra routes that avoid
fights; empty neighbours were once in the cycle and sounded like "empty spaces marked as
POIs"), Space snaps the cursor back, P walks to the cursor one stick push per landed step
(any key, a menu or a blocking enemy ends it), Enter walks when the cursor is one step away
and is the game's A on the player's own node. An enemy blocks onward movement until fought;
a step that goes nowhere is announced, and when the block is an enemy the cursor snaps to
the player's space (`_blockedOn`) so Enter is the fight, not "that is 3 steps away". (This replaced a Left/Right-destination, Up-to-step scheme
after the first blind play-test.) Chest contents (`GraphNode.item`) are deliberately never spoken:
they are not shown on screen.

**Battle (`CombatHandler` + `BattleNarrator`)** — `states.Combat` statics:
`gamepad_selectedequipment` (cursor on a card), `gamepad_dicemode`, `gamepad_hoverdice`,
`state` (`playerturn:allocatedice`, `monsterturn:...`), `turn` (`player` / `monster`),
`previewingequipment`. Flow: d-pad between cards → A → choose dice → A plays it; B backs
out; X ends the turn; **right stick up (held) shows the enemy's cards**. **The mod owns the
card cursor** (`CombatHandler.MoveCardCursor`, second play-test): the game stacks two small
cards in one column (Matchstick over Bump) and only Down reaches the lower one, which a blind
player finds by chance. In card mode the arrows never reach the pad as pressed; the cards are
one list (`ListCards`: ready, shown, on screen, ordered by `tx` then `ty`), Left/Up = previous,
Right/Down = next, no wrap, the ends repeat the card, and every card is spoken with its
position ("2 of 5": tells twins apart, marks the end). The move is made by **the game's own
d-pad**: one press of the button that leads to the wanted card (Up/Down inside a column;
Left/Right across, after writing `gamepad_preferredrow` = the card's `displayrow`, which is
what picks between two stacked cards when the cursor leaves a TALL card - verified; leaving a
small card the game puts that card's own row back every frame, whatever was written, and lands
on the same row of the next column, so `AwaitingListMove` adds one Up/Down press when the
cursor arrives in the wanted column on the other card - third play-test, the Thief's Snake Eye
Charm under Poison Needle was "refused" that way and dropped from the list every turn), so the
game's click and highlight follow.
A first version only wrote `gamepad_selectedequipment`: it worked but was silent, and the
player missed the click at once. There is no such write any more: the game undoes it every
frame for a card it refuses. `AwaitingListMove` gives the cursor 300 ms to arrive; if it does
not, the same button is pressed once more (the game let a press pass in the moment after a card
was played, while the used card slid away, and Combat Roll was dropped from the list that way in
the second play-test; `Combat.state` stays `playerturn:allocatedice` through that animation, so
the state cannot be the guard), and only after a second miss does the card leave the list for
the turn (`_refused`), the log says `Card list: the game kept the cursor on ... twice`, and the
player hears where the cursor still is. Keys stay with the game in
dice mode, in `gamepad_buttonmode`, under prompts, with a textbox open, and when several
buttons are up (jackpot prizes, the six request dice); a panel with ONE focused button (the
Robot's Calculate focuses as soon as the cursor is on the panel) is just a list item, because
the game's own arrows wrap from it to some other card. Verified per character with
`tools/cardlist.py` (walks the list through the real key path; `tools/relaunch_installed.ps1`
and `tools/reunion_fight.ps1` set the fights up): Warrior, Thief, Robot incl. jackpot, Inventor,
Witch with all slots, Jester (in the list too: the game's own arrows wrap around her hand and
Up/Down were dead; her deck panel is not in `Fighter.equipment` and the game skips it), Reunion
Warrior / Thief / Witch. Not walked: Reunion Jester, Robot and Inventor, Halloween, the finale.
Card name and
text are read off the stage from inside the card's rectangle (localized, live values);
dice slots are pictures and come from `Equipment.slots` (`elements.DiceSlotType`).
Narration diffs `Game.player` / `Game.monster` each poll. A blind dice is never read out.
Also narrated: what the enemy rolled (`monsterturn:*`, once its pool settles), the dice
each of its cards was played with, and each dice it puts on a countdown card ("puts 3 on
Spiky Potion, 3 left" / ": ready"; the number left is `remainingcountdown - reducecountdownby`,
because the card ticks down one step every 0.12 s after the dice is spent); a status moving from the fighter onto cards as one event
per card ("Sword shocked", "Sword weakened": cards are matched by name without the "-"
modifier, so no lost/gained pair); a dice that fits no open slot of the chosen card is
"not available for <card>" (`DiceFits`, the rules of `Game.slotcheckvalue`). After E the
state stays `playerturn:allocatedice` for ~2 s while cards slide away: the cursor is kept
quiet for that time. Dead arrow keys repeat the speech line on screen when a textbox is
open (`Tutorial.textboxes[*].actualtextbox.dismissed`, `Art.textboxmap`: **HYPOTHESIS**,
seen only empty) or before the dice are thrown. The pause menu over a battle silences the
card cursor entirely (`_paused`); its buttons stay registered for a moment after Continue, so
`AnnounceChoices` ignores labels seen while paused and the 1.5 s after it closes (it once said
"Choose a prize: Continue, Options, Back to title"). A status that appears on a fighter is
explained once per battle with its own rules text (`StatusInfo.Description`, from
`status[i].description`): "You frozen" alone told the player nothing.

**After a battle (`LevelUpHandler`)** — rewards are drawn as star and coin pictures, so
`LevelUpScreen.lastbattleexpreward`, `lastbattlegoldreward`, `nextlevelexp` are read (once
`giveexp` is 0, or the "to next level" figure is stale). `state` walks `gainxp` →
`chooserequiredrewards` → `chooseoptionalrewards` → (`inventorgadgets`) → `finished`. What
is on offer is `leveluprewards` filtered to `Game.player.level`.

**Cards anywhere (`CardText`, `CardScreenHandler`)** — a card is described from the
`elements.Equipment` object: name + `namemodifier`, rules text, `slots`. Rules text comes
from `equipmentpanel.description[i].textfield._text` (what the card's panel last drew:
the only place an upgrade's "+ 1" exists) and falls back to
`MyLocale.translatearray(card.fulldescription)`; by name alone,
`Gamedata.getequipmenttemplate(name).fulldescription`. `CardScreenHandler` mutes card
faces on the stage, reads cards whole, and ties the focused button to the card whose
x-span contains it (the game's own rule in `Equipment.showgamepadhighlight`). Built on it:
`PickupHandler`, `ShopHandler` (prices exist only as drawn text under each card),
`LevelUpHandler`, `BarterHandler`.

**Grids without buttons** — `BackpackHandler` (`states.Backpack.gamepad_column/row/
inbackpack`, `Inventory.backpack` / `Inventory.equipmentslots`, `getitem(c,r).equipment`,
held card `Game.active_collectible`), `BlacksmithHandler` (same cursor, `upgradefrom`,
`upgradedversion`, result popup = `numbought > 0`), `SpellbookHandler`
(`ViewSpellbook.selectedspellindex`, `selectedequipment`, `precastequipment`,
`elements.Spellbook.spells`), `DeckHandler` (`ViewDeck.targetscrollposition`; the card is
read off the middle column of the stage), `ChallengeHandler` (`currentmonsterindex`).

**Screens the mod had to extend** — `ModsHandler` adds a virtual list because the game's
mod lists are mouse-only (it edits `Modmode.modlist` / `loadlist`, which the screen
redraws from). `ProgressEditorHandler` names the dice toggles whose label is only
`diceradio<x>_<y>`.

**Characters in battle** (all in `CombatHandler` / `BattleNarrator`):

| Character | What the game does | Where it lives |
|---|---|---|
| Thief | third card is a placeholder (`skillcard == "stolencard"`); the real card is `Game.player.stolencard` | `DescribeCard`, narrator "You stole X" |
| Robot | panel card `robot_calculate`: A on it sets `gamepad_buttonmode` and focuses its buttons; `Fighter.roll_total / roll_target / roll_error` | narrator CPU lines, `ButtonDetail` reads a button's effect from under it |
| Inventor | panel card `inventor`, a button named after the gadget; after each fight LevelUpScreen state `inventorgadgets` (`Equipment.gadget`) | `LevelUpHandler` |
| Witch | panel card `witch` takes any dice: page n of `elements.Spellbook.spells` is summoned, else 1 damage; then `Combat.selectspellslotmode` + `gamepad_selectedslot` over `Spellbook.equipmentslots` | `DescribeSpellbook`, `AnnounceSpellSlot` |
| Jester | `Fighter.layout == DECK`; `Fighter.equipment` also holds played cards parked off screen (`ty`), the hand is what is ready and on screen; next up = end of `elements.Deck.drawpile`; Y discards matching cards | narrator "You draw", `DescribeDeck` |

Other battle facts found later: the finale's switch-fighter card (`skillcard ==
"switchfighter"`) opens a chooser whose "Choose" caption is registered as a button only for
the focused fighter; a change of `Game.player.name` mid-battle (finale switch, Bear Potion)
is announced as "You now fight as X". Reunion: skill panels (`*reunion` skillcards) are fired
with Y, and **the cursor does land on them** (an earlier note here said never): the Thief's shows
`Fighter.stolencard`, an enemy card played like his own (`ShowsStolenCard`); the Witch's takes
a dice ("requires 5"); the Warrior's refuses the cursor while he has a card to play and gets
it from the game once he has none; the Warrior's cards need a second A to fire once loaded (manual fire); workout cards
use slot type COMBINATION, whose dice come from `elements.Combination.getstring(card)`.
Names tagged by level packs ("Bump@reunionspacemarine") are translated by the part before @.

Other screens added late: `RemixHandler` (`Remixstate.remixoptions`, `highlightedremix`,
`previewmode` when opened from the map with X), `FinaleSelectHandler` (`charlist`,
`characterselection`), `InventoryFullHandler`, and a generic rule in `ScreenHandler`:
several buttons with the same caption in a row each read the text in their column first.
`DeckHandler` also serves `ViewParty` and `ViewTraining`; `BlacksmithHandler` also serves
`states.Copyshop`.

Test shortcuts that froze the game (never the mod): quickstart `enemy=Jester` (native
"Cannot find fighter template" box; enemy names are template names such as Frog, Slime), `createladyluck()` in a normal episode,
fleeing from a fight reached by teleport, calling `changescene()` or `Modmode.popupOkay`
by hand, quickstart `character=Bear`. Any node type can be made with
`floor[k].updatenodedata(node, {elements.GraphNodeData}.createcopyshop())` and friends; a
full inventory with `states.PickupEquipment.spaceinequipment/spaceinbackpack = false`; late
episodes by editing a *test* save so every `episode_*_win` is 1.

Input facts found last: in **mouse control mode** (at launch, after any mouse movement)
the first press of Start is swallowed, so `ScreenMonitor.WakeGamepadMode` calls
`ControlMode.notifyGamepadInput()` before a key is delivered. A **synthetic mouse click**
works through OpenFL's stage: `#0.parent.window.stage.__onMouse("mouseMove"|"mouseDown"|
"mouseUp", x, y, 0)` in window pixels (canvas 3840x2160 scaled to fit and centred); the
screen cursor (Ctrl+PageDown/PageUp, Ctrl+Enter) is built on it. Tooltips live in
`displayobjects.TooltipManager.tooltips` (StringMap of Tooltip, `translatedtext` lines).
Reunion Thief steal = Y, recorded in `Reunion.thiefcard_selected`. The Robot's request panel
registers six buttons all labelled `dicebutton` (1 2 / 3 4 / 5 6).

Limit break: RB opens `displayobjects.LimitBreakPrompt.showing`, A activates, B cancels.
Flee: Back opens `Combat.flee_showprompt`. The pause overlay is
`states.Settings.screens.length > 0`.

Static *functions* of some classes (`elements.Spellbook`, `elements.Deck`) are not
callable through reflection ("not a function") although their static *variables* are;
`states.Settings.isopen()`, `MyLocale.translate*`, `Gamedata.getequipmenttemplate`,
`ProgressTracking.numepisodeswon` are callable.

---

## 7. Pitfalls found the hard way

- **The installer once destroyed the backup of the real `lime.ndll`.** Its "is this the
  bridge?" test searched for a string that was not in the binary, so it took the bridge
  for the original and overwrote `lime_orig.dll`. Recovery needed Steam's verify. The
  bridge now exports `diceya11y_bridge_marker`, the installer tests for that, refuses to
  save anything under 3 MB as "the original", and was tested against a copy of the game
  folder. Test installer changes on a copy, never on the game.
- Internal names are English whatever the language: compare on those, speak the
  translation.
- Cards, dice and buttons glide into place, so identity must not include a position
  that is still changing (speak once the same key is seen twice / the stage is settled).
- The enemy's cards slide in *over* the player's in the preview: text inside an enemy
  card's rectangle still belongs to the player's card until the enemy card's own name is
  found there.
- In this environment Bash heredocs sometimes halve backslashes. Write files that contain
  backslashes with the editor, not a heredoc.
- The old-path pitfalls (hscript has no try/catch, `|` is a line break in card text,
  `self`/`target` swap per turn) still apply to `mod/` and are documented in `HOOKS.md`.

---

## 8. Testing without being able to see or hear it

- `tools/dev_bridge.ps1` — stop game, rebuild bridge, install, relaunch.
- `tools/deploy_companion.ps1` — publish the companion into the installed copy the player
  starts (`<game>\DiceyA11y`); `-Bridge` also refreshes its `bridge\lime.ndll`, `-Run` starts
  it. A fix built only in `companion\bin` does not reach the player: run this and check there.
- `tools/bridge.py` — REPL-style client: `eval`, `dump`, `probe`, `tree`.
- `tools/pad.py` — press virtual buttons, print scene / focus / buttons / stage text.
- `DiceyA11y.exe --dev` + `tools/dev.py` — drives the companion's **real key path**
  (Windows drops synthetic keystrokes to an unfocused window, so a dev pipe feeds the
  same handlers) and prints what was spoken. `diceya11y.log` has every `Spoke:` line.
- `tools/autoplay.py` — walks and fights far enough to reach later screens.
- Faster: `tools/setf.py "{Game}.monster" hp 1 "{Game}.monster" level 9` wins the fight on
  the next hit and forces a level-up (XP reward = enemy level); `"{Game}.player" gold 60`;
  teleport with `setf.py "{states.Dungeon}" playerposition "{states.Dungeon}.floor[N].nodes[i]"`
  (`tools/mapdump.py N` lists nodes; wait ~6 s for the node's prompt before Enter).
- `tools/relaunch.sh "character=Witch"` restarts game and companion (game started on its
  own so restarting the companion never kills it); `tools/recompanion.sh` rebuilds and
  restarts only the companion, which re-attaches. `tools/dev.py buffer your_cards` reads a
  review list. Screen-specific probes: `backpack.py`, `smith.py`, `spellbook.py`,
  `editor.py`, `axis.py`.
- To *see* a screen: `python tools/gameshot.py out.png` has the game save its own frame
  (`Window.readPixels().encode()` through the bridge). Screen capture does not work: the
  OpenGL surface comes out plain white in PrintWindow and in the user's screenshot tool, and
  `tools/shot.ps1` (CopyFromScreen) needs the window uncovered and catches whatever covers it.
- What the map looks like (seen 2026-09-21): spaces are flat oval pads joined by thin straight
  lines on a cave floor; no grid, no tiles. Not squares, hexes or diamonds.
- Quickstart args (from the exe): `character=<Name> enemy=<Name> episode=<n> modcheats`.
  `enemy=` starts a battle two seconds after launch.
- Save data lives in `%APPDATA%\Terry Cavanagh Games\diceydungeons`. Back it up before
  driving the game; options changed during a test must be put back.

`SCREENS.md` is the inventory and says what has and has not been verified.

---

## 9. macOS

Established 2026-09-22 on the **GOG Mac build 1.12.0** (`/Applications/Dicey Dungeons`),
Apple silicon. Everything above about the game (classes, statics, hxcpp 4.0.4, the three
Lime exports, the resolver path, closure receiver at +8) held unchanged: the companion's
game logic runs as is.

| Fact | Consequence |
|---|---|
| `diceydungeons` and `lime.ndll` are **x86_64 Mach-O, unsigned** (runs under Rosetta; `Entitlements.plist` asks for the sandbox but, unsigned, is never applied) | The bridge is built x86_64 and loads with no signing or library-validation problem. The companion is native arm64; they only share a socket. |
| The executable and `lime.ndll` live in `diceydungeons.app/Contents/MacOS`; `mods/` is beside the `.app` | `GameDirectory` = the folder holding the `.app`; `Launcher/GamePaths.cs` maps it to the swap folder. The backup keeps the name `lime_orig.dll` on both platforms. |
| Mach-O can re-export a whole library | `bridge/build_mac.sh` links with `-reexport_library` against a **stub** `lime_orig.dll` generated from `bridge/lime_exports_mac.txt` (install name `@loader_path/lime_orig.dll`); dyld finds our three exports first and everything else in the real Lime. The build never reads the game. |
| No `/EHa` | `bridge.cpp` installs a SIGSEGV/SIGBUS handler; a fault on the main thread inside `Guard` is `siglongjmp`ed into a `!memory fault` reply, every other fault is chained to the previous handler. Re-installed before each batch in case the game installs its own. **Not yet exercised by a real fault.** |
| Transport | Unix socket `/tmp/diceya11y-bridge-<uid>.sock` (`SO_NOSIGPIPE`, or a vanishing client would kill the game). .NET's `NamedPipeClientStream` treats a rooted name as a socket path, so `BridgeClient` only changes the name. The dev pipe is .NET's own (`$TMPDIR/CoreFxPipe_DiceyA11yDev`). |
| Speech | `Speech/ISpeechEngine`: `PrismEngine` (Windows) / `AvSpeechEngine` (macOS). AVSpeech rate is the mod's (`SpeechRate` 0-100, Ctrl+Plus/Minus, saved); `MacVoice` optional. |
| **AVSpeech needs the main run loop** | Measured: with the main thread blocked, the first utterance plays and every later one is silently dropped (the log still says `Spoke:`). On macOS `Program` waits for the game in `MacNative.WaitOnMainLoop` (runs `CFRunLoopRunInMode` on the main thread) instead of `done.Wait()`. `DICEYA11Y_SPEECH_TRACE=1` prints `AVSpeech started:` per utterance really started. |
| Keyboard | `companion/Mac/Native/DiceyMac.swift` (libDiceyMac.dylib: speech, CGEventTap, frontmost pid). `MacKeyTap` maps Mac keycodes to the Windows VK codes, so **all bindings are shared**; Command counts as Alt (never matches, always reaches the system). The tap needs the **Accessibility** permission; until granted the mod says so every 30 s and starts the tap by itself once it is on. A **modifier on its own is a `flagsChanged` event**, not a keyDown, so the tap masks that type too or Left Control (stop speech) never arrives; it carries no direction, so "down" is read from whether the modifier's own flag is now set, and a modifier is never swallowed. |
| Focus scoping | `CGWindowListCopyWindowInfo`, first layer-0 window's owner. `NSWorkspace.frontmostApplication` never updates in a process without an app run loop. |
| Launching | `open -a diceydungeons.app` (LaunchServices), then attach by process name: a child of a background app can open behind other windows. Game stdout is therefore not read on the Mac (only the old script-mod fallback used it). Attaching to a non-child process still gets `Exited`: measured, the companion quits with the game. |
| Packaging | `companion/Mac/package_mac.sh [--arch x64] [--install]`: self-contained publish inside `Dicey Dungeons Accessibility.app`, `LSUIElement` (no Dock icon, no window, no Terminal), bridge payload in `Contents/MacOS/bridge/`, ad-hoc signed. The Accessibility grant is tied to the signature, so each rebuild must be allowed again. Config and logs: `~/Library/Application Support/DiceyA11y` (`Config.DataDirectory`). |

Not verified on the Mac yet: real key presses through the tap (the dev pipe was used, including the `flagsChanged` path for Control),
the screen cursor's synthetic mouse (`__onMouse` takes window pixels; Retina points vs
pixels untested), the Steam Mac build's folder layout (`GamePaths.MacCandidates` guesses),
Intel Macs.

---

## 10. Releases and the installer

Repository: `gomods1/dicey` (private). Its root is the folder *above* this one: it holds
`DiceyA11y/` and the ignored `game_src/`; `.gitignore` sits there too, and its rules are
written `DiceyA11y/...`. (Until 2026-09-29 this folder was its own repo,
`ogomez92/blindey_dungeons`, and then a submodule; the README's download link and the
installer still point at that repo's releases.) A `v*.*.*` tag matching `<Version>` in
`companion/DiceyA11y.csproj` runs `.github/workflows/release.yml` at the repo root, which
works inside `DiceyA11y/`, calls `tools/package_release.ps1` on a Windows runner and
attaches the zip plus `installer/DiceyDungeonsAccessibilityInstaller.exe`. "Run workflow" by
hand builds the zip as an artifact without releasing.

- **The runner has no game.** `bridge/build.ps1` then builds from the committed
  `bridge/lime_exports.txt` (export names only); with the game present it regenerates that
  file, so commit it when it changes. Prism comes from the `ethindp/prism` release pinned
  (version + SHA-256) at the top of the workflow.
- **Zip layout**: everything under `DiceyA11y/`, extracted into the game folder. The bridge
  travels as `DiceyA11y/bridge/lime.ndll`; it is never extracted over the game's file.
- **Three things can do the lime.ndll swap, with the same rules** (marker export, never
  save a bridge or anything under 3 MB as the original): `tools/install_bridge.ps1`, the
  installer (`mods\installer\src\core\proxy.rs`, refuses *before* extracting), and the
  launcher itself (`Launcher/BridgeInstaller.cs`, at every start when a `bridge\` folder
  sits next to the exe and the game is not running — this is what repairs a Steam update,
  and it also refreshes an older bridge). A developer build has no `bridge\` next to it,
  so the launcher leaves dev installs to `dev_bridge.ps1`.
- The launcher finds the game by itself (`Config.DetectGameDirectory`: parent folder, then
  Steam libraries), and ignores a `GameDirectory` that no longer holds the game.
- The installer exe is committed (its source is the shared `mods\installer` codebase, not
  in this repo). Rebuild there with `build_installer.bat` and copy it into `installer/`.
- Test any change to the swap on a fake game folder (a copy of the real `lime.ndll` plus a
  dummy `diceydungeons.exe`); the installer's `--cli` takes a typed path and a local zip.
