# Screen inventory

Every screen in Dicey Dungeons and where this mod stands on it.

**Sources.** (1) *Code*: the `states.*` class list from the string table of
`diceydungeons.exe` (exhaustive: every function in the game embeds its
`pkg.Class.method` name). (2) *Runtime*: `haxegon.Scene`'s current scene, polled through
the bridge and logged as `Screen: ...` in `diceya11y.log`; anything that falls to the
generic handler is also logged once as `No dedicated handler for ...`.

**Status** (playbook §3):

- **verified** — driven in the running game through the mod's real key path, with the
  spoken output checked in the log.
- **navigable, unverified** — the generic handler applies (the screen is ordinary
  buttons and text) but nobody has walked it yet.
- **open** — known gap, described.

Focus model is **tier B** throughout (the game's own gamepad navigation, driven by the
mod's virtual pad) except the map and the mod list, where the mod owns movement.
See `CLAUDE.md` §4–6.

"Verified" here was done by the developer reading logs, with NVDA as the Prism backend,
in Spanish. **It has not yet been played by a blind player**, which is the real test.
Test runs used developer shortcuts (enemy health set to 1, teleporting on the map, the
game's own quickstart arguments) to reach screens; every screen was then operated only
through the mod's keys.

---

## Verified

| Screen | Class | Handler | What is spoken | Notes |
|---|---|---|---|---|
| Language selection | `states.LanguageSelection` | generic | Name, translator credit, each language with its native and local name, window options, Play | Languages drawn as images are named |
| Title | `states.Title` | generic | Name, options hint, focused button "n of 4" | |
| Pause / options overlay | layer of the current scene (`states.Settings.screens`) | generic | Continue, Options, Reset progress, Mods / Main menu | F11. In battle the overlay's buttons are kept apart from battle panels |
| Options → Display | same | generic | Fullscreen, x2, "Disable screen effects, checkbox, off", "Disable shaders…", "Battle animation: very fast, slider" | Checkbox state from `Screen.disablescreeneffects / disableshaders / fullscreen` |
| Options → Sound | same | generic | "Master volume: 100, slider", new value on change | |
| Options → Gameplay | same | generic | "Relaxed mode, checkbox, off" and its explanation | `Game.relaxedmode` |
| Options → Language | same | generic | Same list as language selection | Escape backs out of every options page |
| Progress editor ("Reset progress") | `states.ProgressEditor` | `ProgressEditorHandler` | Intro and its "I understand" checkbox; then keys, "Thief, episode 1: completed", "Unlocked: Inventor" | It is an editor, not a confirmation box. X (E) unlocks the next contestant, Enter toggles an episode, Y (R) four times resets everything |
| Mods | `states.Modmode` | `ModsHandler` | Buttons; a list of every mod with title, description, author and "will be loaded / not loaded" | The game's lists are mouse-only, so the mod adds a virtual list that edits `modlist` / `loadlist`. "Load mods" itself was not pressed |
| Bonus episodes | `states.SelectLevelpack` | generic | "Choose level pack", Dicey Dungeons / Halloween Special / Reunion | Reunion was loaded and played one fight; Halloween not entered |
| Credits | `states.Credits` | generic | Role and name pairs | |
| Cutscenes | `states.ViewCutscene` | generic | Speaker, then each line as it appears | Enter advances |
| Character select | `states.CharacterSelect` | `CharacterSelectHandler` | "Thief, 2 of 6, difficulty 2 of 5, 1 episodes completed", biography, or "Locked contestant" and how to unlock | Difficulty is drawn as pictures. Y (R) opens challenges |
| Episode select | `states.Episodestate` | generic | Episode title, wins and losses or "incomplete", description, rules, for each door as you move | Walked with two episodes; started "Finders Keepers" from it |
| Challenges | `states.ChallengeScreen` | `ChallengeHandler` | "Frog, 1 of 53", the task, done / not done, Take, and the enemy's fact sheet once claimed | Claimed one reward |
| Continue prompt | `states.DoYouWantToContinue` | generic | "You have a run in progress", the run's summary, Continue / New game | |
| Dungeon map | `states.Dungeon` | `DungeonHandler` | Where you are, exits, everything else with distance and first step, blocked steps; health and gold changes (apple, purchases); F5 adds health, gold, level, dice, floor | Mod-owned movement |
| Item found | `states.PickupEquipment` | `PickupHandler` | "You found", the whole card, whether it will be equipped or go to the backpack, Take | |
| Inventory | `states.Backpack` | `BackpackHandler` | Summary of both grids; per cell the card in full or "empty"; picked up, placed, swapped with which card | R (Y) on the map. Cells, not buttons |
| Witch's spellbook | `states.ViewSpellbook` | `SpellbookHandler` | Prepared spells and the six pages; each page in full; "prepared in slot n"; upgraded preview | R (Y) on the map as the Witch |
| Jester's deck | `states.ViewDeck` | `DeckHandler` | Card in the middle of the strip in full, "3 of 9"; F5 lists the deck grouped | R (Y) on the map as the Jester |
| Shop | `states.Shopstate` | `ShopHandler` | Gold and the wares with prices; each ware in full with price and "cannot afford"; "Bought X, n gold left, equipped / backpack" | Buying closes the shop; it can be entered again |
| Copy shop | `states.Copyshop` | `BlacksmithHandler` | Same grid as the anvil: each card, "Enter makes a copy", "A copy was added to your backpack" | Node created with the game's `GraphNodeData.createcopyshop()` for the test |
| Upgrade (anvil) | `states.Blacksmith` | `BlacksmithHandler` | Shopkeeper's line, keys; per cell the card and what it becomes; "Equipment upgraded" result | Also reached from the level-up "Upgrade" reward. R (Y) switches to the backpack |
| Trade | `states.Barterstate` | `BarterHandler` | Trader's line, "You give … You get …" in full, Enter accepts / Escape refuses, outcome | Refused once, accepted once |
| Apple (heal) node | map | `DungeonHandler` | "Healed 5. Health 15 of 28" | |
| Battle | `states.Combat` | `CombatHandler`, `BattleNarrator` | Intro, enemy's cards, turns, dice (also new dice made mid-turn), focused card, hovered dice with live preview, damage/heal, statuses, enemy card use, countdown ready, victory/defeat, dialogue | Tab previews the enemy's cards. Review lists hold full card text and status descriptions. An arrow that moves nothing repeats the focus |
| — limit break | `displayobjects.LimitBreakPrompt` | `CombatHandler` | "Limit break ready: name, effect. Press W"; the prompt; Enter / Escape | Used as Thief and Robot |
| — flee | `Combat.flee_showprompt`, `attempttoflee` | `CombatHandler` | "Try to flee? Enter flees, Escape cancels"; "You try to flee… press Enter to go on"; the enemy's turn; "fled"; back on the map | Done for real as the Warrior. The game waits for Enter on both messages |
| — Thief | | | Stolen card read as the real card (`Fighter.stolencard`), "You stole X" each turn | Full episode won |
| — Robot | | | CPU panel: Calculate button, "CPU 8 of 9", Error, Jackpot, the three prizes each with its effect | Enter on the panel opens its buttons (`gamepad_buttonmode`) |
| — Inventor | | | Gadget panel as a button with its effect; after each fight "Destroy X … New gadget: Y, effect" | The gadget was read, not fired |
| — Witch | | | Spellbook panel: which dice summons which page, dice preview "Summons Infliction: …", spell-slot choice "slot 2 of 4: empty" | |
| — Jester | | | Hand only, "You draw X", next cards in the deck, "matching X: R discards them" | Discard done twice |
| After battle | `states.LevelUpScreen` | `LevelUpHandler` | Experience and gold, experience to next level, "Level up", required reward, reward choice with each card in full, non-card rewards by caption | States `gainxp`, `chooserequiredrewards`, `chooseoptionalrewards`, `inventorgadgets`, `finderskeepers` (the Thief's second episode: the enemy's cards, each in full, with Steal) |
| Boss and episode end | `states.Combat`, `ViewCutscene`, `states.GameComplete` | as above, generic | Boss flagged "(BOSS)", vampire status explained in the status list, ending cutscene, "Episode complete", unlocks | The separate `DramaticBossIntro` scene did not appear for this boss |

| Inventory full | `states.InventoryFull` | `InventoryFullHandler` | "You are carrying too much", which card is waiting, each card you could discard in full with its Discard button | Forced by telling `states.PickupEquipment` there was no room. Escape does nothing here: one card has to go |
| Boss floor intro | `states.DramaticBossIntro` | generic | "Boss floor. Press Enter", "Warrior versus… Madison" | Appears when the last stairs are taken normally |
| Finale: who goes through the door | `states.FinaleCharacterSelection` | `FinaleSelectHandler` | Contestant name "n of 6", then the chosen one's lines | The run was played through it; the name-by-name cursor announcement was added afterwards and not re-walked |
| Finale run | map, battle, `states.ViewParty` | `DungeonHandler`, `CombatHandler`, `DeckHandler` | Contestant spaces ("Witch, a contestant to free by beating them"), "joins your group", the group view, the in-battle fighter chooser ("The Witch, 24/24. Choose"), "You now fight as Witch" | Freed the Witch, switched to her in battle |
| Lady Luck and the ending | battle, `ViewCutscene`, `states.FancyCredits` | as battle, generic | Her changing cards, the long ending, scrolling credits as they appear; Escape leaves | Won with developer shortcuts |
| Rule change (remix) | `states.Remixstate` | `RemixHandler` | "Choose a rule change: 2 options", each option with its effect, rules already in force; E on the map reviews them | Bonus Round episode |
| Bear | battle, map, inventory | `BattleNarrator` | "You now fight as Bear: health, cards" | Drank a Bear Potion as the Warrior, won a fight and opened the inventory as the Bear |
| Reunion pack | character select, map, battle, `states.ViewTraining` | `CharacterSelectHandler`, `DeckHandler`, `CombatHandler` | Warrior: "loaded, Enter plays it" (cards fire on a second Enter), skill panel "press R to use it: reroll", workout card reward with its dice combination, the Exercises view (E on the map). Thief: see Open | |
| Halloween Special pack | as the base game | | Character select (3 contestants), map, one fight | |
| Mods → Load mods | `states.Modmode` | `ModsHandler` | Back on the title; the version line in Options lists the loaded mods | Loaded Frogurt through the keyboard list |
| Fatal errors | native Windows message box titled "Error" | none needed | Read by the screen reader itself | Seen when the game was started with an invalid quickstart argument |

| Episode types | | | Played at least one fight in every kind: first episodes, Finders Keepers, Double Rubble (two gadgets, both fired from the panel), Worse than a Curse, You Choose You Lose (the Robot's "Ask for a 1…6" panel), Elimination Round, Parallel Universe (Witch, Robot, Jester), Bonus Round | Their twists are text and cards inside verified screens |
| Reunion Thief's "Steal" | battle | `CombatHandler` | The panel is described in F5 ("press R once per battle to steal it for good"), "Stolen: you keep X", and after the fight "It is yours now" with the card in full | R (Y). The game only hides the button when it works, which is why it first looked dead |
| Tooltips | any screen | `ScreenMonitor.RefreshTooltips` | A review list "tooltips" (Ctrl+Left / Right to reach it) with every tooltip the screen registers, e.g. the limit break with "ready in 8 HP" | From `displayobjects.TooltipManager.tooltips[*].translatedtext` |
| Mods: details and load order | `states.Modmode` | `ModsHandler` | Each row also gives version and licence; Left / Right move a mod in the load order | The game's own details panel and its Move up / Move down buttons are mouse-only |
| Quit | title | generic | Leaves the game (exit code 0) | |
| Screen cursor (mouse fallback) | every screen | `ScreenMonitor.MoveScreenCursor / ClickScreenCursor` | Ctrl+Page Down / Up step through every piece of text on screen, Ctrl+Enter clicks it | Clicked "End turn" in a battle and a mod row in the mod manager. Exists so that nothing is ever mouse-only |

## Not reachable in this build

| Scene | Finding |
|---|---|
| Mod manager "new mod" and "shared mod" pages, `states.ModalPopup`, `states.ViewTextfile` | `Modmode.currentscreen` only ever is `browse`; no control on the screen leads to the other pages (the code for them is in the exe, the buttons are not drawn). Nothing to make accessible |
| `states.Errorstate`, `states.StartupError` | Never shown. The one fatal error met (an invalid quickstart argument used in testing) was a native Windows message box, which the screen reader reads itself |
| `states.FindersKeepers` | Not a screen: it has no `changescene`, it is the renderer behind the LevelUpScreen state `finderskeepers`, which is verified |

## Definition of done (playbook §16) — honest status

- [x] Every reachable screen verified through the key path (once each, by the developer).
- [x] A full run playable with the monitor off: a Thief episode was played from the map to
      the ending through the mod's keys (with developer shortcuts for speed).
- [x] What Enter acts on is always what was last announced.
- [x] No arrow key answered with silence: the generic handler repeats the focus when a
      key moved nothing; the map announces blocked steps.
- [x] Claimed keys never reach the game.
- [x] Works with NVDA; falls back to another Prism backend.
- [ ] Tested by a blind player. **No.**
