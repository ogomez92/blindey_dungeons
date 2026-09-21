# Wire protocol

> **Status: fallback path.** This file describes the original script-mod route, which can
> only see battles. The mod now works through an in-process bridge that reads every screen
> (`CLAUDE.md` §2-3). With the bridge installed, nothing here is used; it remains for a game
> folder where the bridge cannot be installed.

The mod and the companion are separate processes in different languages. They are
connected by one thing: lines the mod prints with `trace()`, which land on the
game's stdout, which the companion reads.

Everything the companion knows about the game comes through here.

---

## Framing

hxcpp prefixes every trace with a source position, so a real line looks like:

```
hscript:0: A11Y1|f|who=self|name=Warrior|hp=24|maxhp=24|player=1
```

The parser finds `A11Y1|` anywhere in the line and ignores everything before it.
Lines without that tag are ordinary game logging and are passed through to
`diceya11y-game.log`.

- Record layout: `A11Y1|<kind>|<key>=<value>|<key>=<value>...`
- `<kind>` is positional; everything after it is key=value in any order.
- An unrecognised key is ignored. An unrecognised `<kind>` is ignored.

### Escaping

`|` is **the game's own line break inside card text**, so it cannot appear raw in a
value. The mod rewrites, inside every value it emits:

| In game data | On the wire | Spoken as |
|---|---|---|
| `\|` | `^` | a clause break (", ") |
| newline / carriage return | space | space |

`TextCleanup` additionally resolves the game's display markup: `[sword]` → "damage",
`[gray]` → dropped, `<d6>` → "dice value". The full table is in
`companion/Core/TextCleanup.cs`, derived from `data/text/symbols.csv`.

---

## Records

### `boot` — the mod loaded

```
A11Y1|boot|v=1
```

Sent once per run from `flexible_generator.hx`. If the companion never sees this,
the mod did not load and it says so in the log.

### `run` — run-scope facts

```
A11Y1|run|player=Warrior
A11Y1|run|floor=1
```

One fact per line, because a single failing call would otherwise take the whole
line with it (there is no try/catch in this hscript).

### `ev` … `end` — a snapshot block

```
A11Y1|ev|onstartturn
  ... fighter / status / equipment / dice records ...
A11Y1|ctx|floor=2
A11Y1|end|onstartturn
```

`ev` opens a block and `end` closes it; records in between belong to it. The
companion only acts on a block once `end` arrives, so a snapshot cut short by a
script error is discarded rather than half-announced.

### `f` — fighter identity and health

```
A11Y1|f|who=self|name=Warrior|hp=24|maxhp=24|player=1
```

`who` is `self` or `target` as the *script* saw it. **These swap** depending on
whose turn it is; `player=1` is the authority on which one is the player.

### `f2` — resources

```
A11Y1|f2|who=self|lvl=1|gold=50|dice=2|extradice=0|mana=100|maxmana=100|limit=0|limitmax=8|cpu=0
```

`cpu=1` means this fighter uses the Robot's CPU counter instead of dice.
`mana`/`maxmana` read `100/100` for characters that do not use mana, so they are
carried but not announced by default.

### `s` — status effect

```
A11Y1|s|who=target|i=0|name=Shield|value=3|type=SHIELD|hidden=0
```

`hidden=1` is the game's `invisible` flag; those are excluded from readouts.

### `e` — equipment (card)

```
A11Y1|e|who=self|i=0|name=Sword|mod=|size=2|cd=0|cdmax=0|used=0|times=0
```

`mod` is the name modifier (`+` for upgraded). `cd`/`cdmax` are
`remainingcountdown`/`countdown`; `cdmax=0` means the card has no countdown.

### `ed` — equipment description

```
A11Y1|ed|who=self|i=0|desc=Do [sword]<d6> damage
```

Split from `e` so that a description failure cannot cost the card list. `i`
matches the `i` of the corresponding `e` record.

### `d` — a die

```
A11Y1|d|who=self|i=0|v=4|base=4|mod=0|locked=0|frozen=0|burn=0|on=Sword
```

`v` is `base + mod`, computed in the mod: `Dice.value` is a property getter and
reads as `null` through hscript. `on` is the card the die is currently placed on,
empty when unassigned.

### `ctx` — per-snapshot context

```
A11Y1|ctx|floor=2
```

Emitted last in a block because it calls a `ScriptFunctions` global; if such a call
were ever unavailable, nothing earlier is lost.

---

## Adding a field

1. Add the `trace(...)` in `mod/data/text/scripts/diceydungeons/a11y/snap.hx`,
   **at the end of the stage it belongs to** — a bad field reference aborts the
   script from that point on, so new and less certain fields go last.
2. Add the key to `ProtocolParser` and the model class in `Protocol/Snapshot.cs`.
3. Redeploy with `tools/deploy_mod.sh` and run a battle; unknown keys are ignored,
   so an old companion and a new mod still work together.
