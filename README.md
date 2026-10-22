# Dicey Dungeons Accessibility (DiceyA11y)

Makes Dicey Dungeons playable with a screen reader: menus, the dungeon map and battles
are spoken, and the whole game is driven from the keyboard or a controller.

Dicey Dungeons has no keyboard controls of its own — it is played with a mouse or a
gamepad. This mod plugs a virtual gamepad into the game and connects your keyboard to it,
so every key press goes through the game's own navigation. What you hear focused is what
Enter will act on.

The mod speaks in the game's language: all 21 languages the game ships have a translation
of the mod's own words (English and Spanish by hand, the others machine translated -
corrections welcome). Game text is always in whatever language the game is set to.

**Status.** All six contestants, the Bear, every kind of map stop (shop, upgrade, copy
shop, trade, chest, apple), the inventory, level-ups, bosses, the finale against Lady Luck,
the Bonus Round's rule changes, the menus (options, progress editor, mods, challenges) and
the Reunion and Halloween packs have been driven from the keyboard with the speech output
checked. `SCREENS.md` lists each screen with what was checked. For anything that only a
mouse could reach there is a screen cursor (Ctrl+Page Down / Page Up, Ctrl+Enter clicks).
It has not yet been tested by a blind player — reports are very welcome.

---

## Requirements

- Windows, 64-bit, and Dicey Dungeons (tested on Steam v2.1)
- A screen reader (NVDA or JAWS) or any SAPI voice; [Prism](https://github.com/ethindp/prism) picks the best available

Nothing else: since v1.2.4 the mod carries its own .NET runtime (earlier versions needed the
.NET 9 runtime installed).

## Install

1. Close Dicey Dungeons.
2. Run the installer. There are two, and either will do:
   - **DiceyDungeonsAccessibilityInstaller-v….-offline.exe**, from Patreon. The mod is inside
     it, so it needs no internet connection.
   - **DiceyDungeonsAccessibilityInstaller.exe**, from the
     [releases page](https://github.com/gomods1/dicey/releases/latest). It downloads the latest
     version of the mod from there.

   It finds the game through Steam (there is a Browse button if it cannot) and installs the
   mod. The installer works with a screen reader; `--cli`
   gives a terminal version.
3. Press Play in Steam. The game starts, and a moment later the mod starts with it
   (minimized; the game keeps the keyboard) and says "Dicey Dungeons accessibility
   ready". It closes when the game does.
4. **Recommended: Steam launch options.** After a game update Steam puts its own
   `lime.ndll` back, and the mod can only repair that when it starts *before* the game.
   To have Play always start the mod first, open the **Properties** of Dicey Dungeons in
   your Steam library (Applications key or Shift+F10 on the game) and, on the General tab,
   put this in the **Launch Options** box, quotes and `%command%` included:

   ```
   "C:\Program Files (x86)\Steam\steamapps\common\Dicey Dungeons\DiceyA11y\DiceyA11y.exe" %command%
   ```

   Change the first part if your game is installed somewhere else; the installer's last
   message shows the line with your own path.

Without Steam's help you can also start **`DiceyA11y\DiceyA11y.exe` inside the game folder**
yourself (a desktop shortcut to it helps). If the game is already running it attaches to it
instead.

What gets installed: one folder, `DiceyA11y`, inside the game folder, and the game's
`lime.ndll` is replaced by the mod's bridge, with the original kept next to it as
`lime_orig.dll`. Nothing else in the game is touched.

**Updating:** run the installer from the releases page again (it fetches the newest version),
or download the new offline installer from Patreon. Either shows the version you have and
the version it would install, and installs over the old one.

**After a game update, or Steam's "verify integrity of game files"**, Steam puts its own
`lime.ndll` back. The launcher notices and repairs that by itself the next time you start
it, as long as the game is not already running.

**To remove the mod:** the installer's Uninstall button puts the original `lime.ndll` back and
deletes the `DiceyA11y` folder.

### Without the installer

If you have the mod as a zip (`DiceyA11y-v….zip`), extract it **into the game folder** (so that
the `DiceyA11y` folder ends up next to `diceydungeons.exe`), and start
`DiceyA11y\DiceyA11y.exe`: on its first start the launcher installs the bridge itself.
After that, Steam's Play button starts the mod too (and see step 4 above).
To remove it by hand, run `DiceyA11y\tools\install_bridge.ps1 -Uninstall` in PowerShell (or
verify the game files in Steam) and delete the `DiceyA11y` folder.

## macOS

The Mac version speaks with the system voice (AVSpeech); it does not need VoiceOver, and
its speech rate is its own: **Control Plus** and **Control Minus** change it in steps of 5,
and the setting is kept.

1. Put **Dicey Dungeons Accessibility.app** in the game folder, next to `diceydungeons.app`
   (for the GOG version, `/Applications/Dicey Dungeons`). Anywhere else works too if the
   game is in `/Applications` or Steam's usual place.
2. Open it. It has no window and no Dock icon: it installs the bridge into the game
   (inside `diceydungeons.app/Contents/MacOS`, the original kept as `lime_orig.dll`),
   starts the game, and quits when the game does.
3. The first time, macOS asks for the **Accessibility** permission, which the mod needs to
   read the keyboard. The mod says so until it has it: System Settings, Privacy and
   Security, Accessibility, turn on Dicey Dungeons Accessibility. The keys work as soon as
   it is on; nothing needs restarting. A rebuilt or updated app has to be allowed again.
   **If the mod keeps saying it cannot read the keyboard although the switch is on**,
   macOS is holding a permission for an older copy of the app: select Dicey Dungeons
   Accessibility in that list, remove it with the minus button, then add it again with the
   plus button (it is in the game folder) and turn it on.
4. A downloaded app is blocked the first time ("unidentified developer"): Control-click it
   and choose Open, or run `xattr -cr "Dicey Dungeons Accessibility.app"` once.

The F keys need **fn** held down unless "Use F1, F2, etc. keys as standard function keys" is
on (System Settings, Keyboard). Settings and logs are in
`~/Library/Application Support/DiceyA11y`; the bridge's log stays next to the game's
executable inside `diceydungeons.app`. Tested with the GOG Mac version 1.12 on Apple silicon.

## Keys

Active only while the Dicey Dungeons window has focus. **F1** on any screen lists the keys
for that screen (Ctrl+Up / Ctrl+Down step through them), so you never need this page open
while playing.

### Playing

| Key | Does |
|---|---|
| **Arrow keys** | Move between buttons, cards and dice |
| **Enter** or **Space** | Confirm (the gamepad's A) |
| **Escape** | Back / cancel (B) |
| **E** | X button — **ends your turn** in battle |
| **R** | Y button |
| **Q**, **W** | Shoulder buttons |
| **F11** | Pause menu (Start): continue, options, mods |
| **Backspace** | Back/Select button |
| **Tab**, **Shift+Tab** | In battle: next / previous group: enemy cards, enemy status effects, your status effects, your actions |
| **Up**, **Down** | In battle: next / previous item of the current group (Left and Right stay on your cards) |
| **Left Control** | Stops speech |

When the game shows a gamepad button in a prompt, the mod says the key instead
("press Enter again to choose your dice").

### In battle

Arrow keys move between your cards; each is read with its text and what dice it takes.
Enter picks the card; now the arrows move between your dice, and each is read with what
the card would do with it ("dice 5. Does 5 damage"). Enter plays it, Escape backs out.
**E** ends your turn. Damage, healing, status effects, what the enemy plays and whose turn
it is are spoken as they happen. A status effect is explained the first time it appears in a
fight ("Frog shield 3. Blocks the opponent's damage").

Everything else on the board is in four groups. **Tab** and **Shift+Tab** move between them:
the enemy's cards, the enemy's status effects, your status effects, and your actions (the
limit break with its charge, a Reunion skill, the Jester's discard, fleeing). **Down** and
**Up** move within the group (Left and Right stay on your own cards); each item is read with its place ("2 of 3"). The item
also becomes the current review list, so **Ctrl+Up** reads its details line by line, ending
with the keywords its text names: a card that says "Do 3 burn damage" gets a "Burn: ..." line
with the game's own rule for it. The same keyword lines follow your own cards as the arrows
move over them.

- **W** uses your limit break once the mod says it is ready (Enter activates, Escape cancels).
- **Backspace** asks to flee the fight.
- **Thief**: the stolen card is one of your cards and changes every turn ("You stole …").
- **Robot**: move right to the CPU panel and press Enter to ask for a dice. You hear the
  total against the target ("CPU 8 of 9"); going over is an error and ends your turn, hitting
  it exactly is a jackpot with three prizes (Enter on the panel, up and down, Enter).
- **Inventor**: the gadget is the last card on the right. After every fight you choose a
  card to destroy; each choice says which gadget it becomes and what that gadget does.
- **Witch**: play a dice on the spellbook panel to summon the spell on the page with that
  number (each dice tells you what it would summon), then choose a spell slot with the
  arrows and Enter. **R** on the map opens the spellbook to prepare spells. When you find a
  new spell, up and down go through the six pages of your book ("Page 5 of 6, empty"),
  Enter writes the spell on that page (replacing what is there), Escape leaves it, and Q
  shows the upgraded spell.
- **Jester**: you hold a hand of cards. **R** discards matching cards and draws new ones;
  F5 tells you what is next in the deck. **R** on the map shows the deck.

An arrow key that cannot move anything repeats where you are, so silence always means the
mod did not get the key.

### Inventory, shops and other stops

- **R** on the map opens the inventory: two grids, equipped cards and backpack. Equipped has
  3 columns of 2 cells and the backpack 4 columns of 5 (the mod says so when it opens). A
  small card takes one cell; a large card takes two, one above the other, and is read as
  "column 1, rows 1 and 2" from either cell. Arrows move between cells (left from the
  equipped cards enters the backpack), Enter picks a card up, Enter again puts it down;
  putting it on another card swaps them.
- **Shop**: you hear your gold and what is for sale; left and right read each item in full
  with its price, Enter buys. Buying leaves the shop; you can walk in again.
- **Upgrade**: arrows read each of your cards with what it becomes, Enter upgrades, **R**
  switches between equipped cards and backpack.
- **Trade**: you hear what you give and what you get. Enter accepts, Escape refuses.
- **After a fight**: experience and gold, and on a level up the rewards to choose from.
- **Character select**: left and right, Enter starts, **R** opens the challenges.
- **Episode select**: left and right move through the open episodes ("episode 2 of 6").
  On arrival you hear how many are open and what each locked one needs: episodes 4, 5
  and 6 open after 2, 3 and 4 wins with that contestant. **W** and **Q** turn to the
  Extra Episodes page when a mod adds more; F5 repeats the summary.
- **Pause menu (F11) → Reset progress** is the game's progress editor: **E** unlocks the
  next contestant, Enter marks an episode completed, **R** pressed four times resets all.
- **Inventory full**: left and right read the cards you could discard, Enter discards one.
- **Bonus Round episodes**: between floors you choose a rule change (up and down, Enter);
  **E** on the map reads the rule changes in force.
- **The finale**: left and right choose who goes through the last door. Spaces with a
  contestant are fights that free them; **R** on the map shows your group, and in battle
  the last card switches fighter.
- **Reunion episodes**: **R** in battle uses your skill panel (F5 says what it does; for the
  Thief it steals the enemy card shown on the panel, once per battle); the
  Warrior's cards fire on a second Enter once a dice is on them, and **E** on the map shows
  his exercise cards.
- **Mods**: down arrow enters the list of mods, Enter switches one on or off, Left and
  Right change the load order, then go back up to "Load mods".

### On the map

The game's own map controls cannot reach every space from a keyboard, so here the keys
work differently:

| Key | Does |
|---|---|
| **Arrow keys** | Move a cursor along the paths, from space to space, to explore the map. This never moves you. Paths run up right, down right, down left, up left and sometimes straight up or down. An arrow follows the path on that side; when two paths lead that way both are named and one of the other arrows picks, so "up right" is Up and Right in either order. Each space says what it is and how far from you it is |
| **Comma / Period** | Jump the cursor to the previous / next point of interest: the exits from your space first, then enemies, chests, shops, the way down. A chest you opened or an enemy you beat becomes an empty space and leaves the list, so the list is what is left to do on the floor |
| **Space** | Bring the cursor back to the space you are on, and say what is around it |
| **M** | Mark the space under the cursor (an exit, an apple or a shop you leave for later); M again on it removes the mark. One mark per floor |
| **G** | Jump the cursor to the marked space; then P walks you there |
| **P** | Walk to the space under the cursor. The way is said first; any key stops the walk, and so does an enemy standing in the way |
| **Enter** | On a space one step away: walk there. On your own space: use it (open the chest, start the fight, enter the shop, go down). Further away nothing happens, and you are told how far it is |

Your health, gold, level, dice and cards are in the **Player** list (Ctrl+Left / Right), and
**Ctrl+H** reads your health from anywhere. **F5** describes the whole floor: where you are,
your exits and everything further away.

Walking is always safe: nothing happens on a space until you press Enter there. An enemy
blocks the way past it until you fight it, and the mod tells you so.

### Reading

| Key | Does |
|---|---|
| **F1** | Help for the current screen, as a list (Ctrl+Up / Ctrl+Down to step through) |
| **F2** | Repeat the last announcement |
| **F3** | Read the focused item again, in full |
| **F4** | Read everything on screen |
| **F5** | Overview: on the map, the floor; in battle, both fighters, your dice and all cards |
| **F6 / F7** | Your cards / the enemy's cards |
| **F8** | Status effects on both sides |
| **F9** | Events, newest first |
| **Ctrl + Up / Down** | Step through the current list |
| **Ctrl + Left / Right** | Switch list. The first list, *Focused item*, always follows what you are on (a card, a dice, a button, a map space) and becomes the current list whenever focus moves; tooltips, what hovering the mouse would show, sit at its top. Help is only a list between pressing F1 and moving away from it |
| **Ctrl + Page Down / Page Up** | Screen cursor: step through every piece of text on screen |
| **Ctrl + Enter** | Click what the screen cursor is on (for anything only a mouse could reach) |
| **Ctrl + D** | In battle: your dice that are still free |
| **Ctrl + H** | Your health, current and maximum |
| **Ctrl + E** | In battle: the enemy's health |
| **F12** | Where am I |

Announcements are queued, never cut off. Your own key presses do interrupt: moving focus
cuts off the previous focus reading, as any screen reader does.

## Controller

Plug in any controller the game itself accepts and the
mod takes it over: it says "Controller ready", and from then on every button goes through the
mod exactly as the keys do, so your presses cut off the speech before them and the mod's own
helpers (the card list, the map cursor, the battle groups, the review lists) work on the pad.
Prompts and help name buttons while you play on the controller and keys once you touch the
keyboard again.

| Control | Does |
|---|---|
| **D-pad** or **left stick** | Move (the arrow keys) |
| **A**, **B**, **X**, **Y**, **LB**, **RB**, **Start** | As in the game (Enter, Escape, E, R, Q, W, F11) |
| **Back** (View, Select, Share) tapped | The game's Back button (Backspace): flee in battle |
| **Right trigger / left trigger** | In battle: next / previous group (Tab / Shift+Tab). On the map: next / previous point of interest (period / comma) |
| **Left stick click** | On the map: walk to the cursor (P) |
| **Right stick click** | On the map: cursor back to your space and what is around it (Space). Elsewhere: stop speech |
| **Right stick** | Review lists: up / down step through the current list, left / right change list (Ctrl + arrows) |

Hold **Back** for the reading buttons (Back is only sent to the game when tapped alone):

| Back + | Does |
|---|---|
| **Start** | Help (F1) |
| **B** | Repeat the last announcement (F2) |
| **A** | Read the focused item again (F3) |
| **X** | Read everything on screen (F4) |
| **Y** | Overview (F5) |
| **D-pad up / down / right / left** | Your health / your dice / the enemy's health / where am I |
| **LB / RB** | Your cards / the enemy's cards (F6 / F7) |
| **Left / right trigger** | Status effects / events (F8 / F9) |
| **Right stick left / right, click** | Screen cursor (Ctrl+Page Up / Page Down, Ctrl+Enter) |

`ControllerSupport=False` in the settings leaves the controller to the game alone (the mod then
neither reads it nor interrupts speech for it). When the mod closes it gives the controller
back to the game.

## Settings

`DiceyA11y.cfg` next to `DiceyA11y.exe`, plain `key=value`. The game folder is found
automatically; `GameDirectory` only matters for a game outside Steam.

```
GameDirectory=S:\steam\steamapps\common\Dicey Dungeons
AnnounceDice=True             # read your dice at the start of your turn
AnnounceEnemyEquipment=True   # list the enemy's cards when a battle starts
AnnounceStatusChanges=True
MaxEventBuffer=200
ScopeHotkeysToGame=True       # False makes the keys global (not recommended)
ControllerSupport=True        # the mod reads your controller; False leaves it to the game
ModIds=                       # other Polymod mods to load, comma separated
SpeechRate=50                 # macOS only: 0-100, 50 is normal (Control Plus / Minus)
MacVoice=                     # macOS only: a voice name (Samantha) or language (es-ES)
```

## If something is wrong

Logs are written next to the exe and overwritten each launch:

| File | Contents |
|---|---|
| `diceya11y.log` | Everything the mod did: every key pressed (`Key:`), every `Spoke:` line, every `Screen:` change |
| `diceya11y-spoken.log` | Only what was said |
| `<game folder>\diceya11y-bridge.log` | The in-game half: whether it loaded and hooked |

- **Nothing is spoken at all** — if there is no fresh `diceya11y.log`, the mod never
  started: most likely a game update put Steam's `lime.ndll` back. Start
  `DiceyA11y\DiceyA11y.exe` once with the game closed (it repairs that), and set the launch
  options of Install step 4. Otherwise look for `Speech backend:` in `diceya11y.log`.
- **Only "ready" is spoken, then silence** — the bridge is not installed (Steam restored
  the file while the game was running, say). Close the game and start `DiceyA11y.exe`
  again, or run the installer again. The log says `Waiting for the bridge pipe`.
- **The game will not start after installing** — `lime_orig.dll` is missing or wrong.
  Verify the game files in Steam, then install again.
- **Something reads wrong** — send `diceya11y.log`; the `Key:`, `Screen:` and `Spoke:` lines pin it down.
  It is overwritten the next time the mod starts, so copy it before playing again.

## For developers

```powershell
.\bridge\build.ps1            # build the bridge (MSVC); regenerates forwarders from the game's lime.ndll
.\tools\dev_bridge.ps1        # stop game, rebuild bridge, install, relaunch
dotnet build companion\DiceyA11y.csproj -c Release
.\tools\package_release.ps1   # release folder and zip, as the release workflow builds them
```

On a Mac: `companion/Mac/package_mac.sh --install` builds the bridge (`bridge/build_mac.sh`),
the Swift helper and a self-contained app, and copies it into `/Applications/Dicey Dungeons`.
`tools/bridge.py` and `tools/dev.py` work on the Mac too.

Releases are cut by `.github/workflows/release.yml` on a `v*.*.*` tag that matches
`<Version>` in `companion/DiceyA11y.csproj`. It builds everything without the game (the
bridge from `bridge/lime_exports.txt`) and attaches the zip and the installer
(`installer/`, built from the shared installer codebase of these mods).

Read `CLAUDE.md` first: how the bridge gets into a native game, the measured facts about
this hxcpp build, and the mistakes already made. `SCREENS.md` is the screen inventory,
`HOOKS.md` and `PROTOCOL.md` describe the older script-mod fallback.

## Licence

MIT for this mod's own code. Dicey Dungeons and its data are © Terry Cavanagh and are not
redistributed here: the bridge contains no game code and forwards to the copy of Lime
already on your machine. Prism is used under its own licence (`licenses/prism`).
