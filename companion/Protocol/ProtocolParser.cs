using System.Globalization;

namespace DiceyA11y.Protocol;

/// <summary>
/// Reassembles the line protocol the mod emits on the game's stdout into
/// <see cref="Snapshot"/> objects.
///
/// Wire format (one record per line, fields are key=value separated by '|'):
///   A11Y1|boot|v=1
///   A11Y1|ev|&lt;eventName&gt;          -- starts a snapshot
///   A11Y1|f|who=self|name=..|hp=..|maxhp=..|player=1
///   A11Y1|f2|who=self|lvl=..|gold=..|...
///   A11Y1|s|who=self|i=0|name=..|value=..|type=..|hidden=0
///   A11Y1|e|who=self|i=0|name=..|mod=..|size=..|cd=..|cdmax=..|used=..|times=..
///   A11Y1|d|who=self|i=0|v=..|locked=..|frozen=..|burn=..
///   A11Y1|ed|who=self|i=0|desc=..
///   A11Y1|end|&lt;eventName&gt;         -- snapshot is complete
///
/// The mod strips '|' and newlines from values, but the parser stays tolerant:
/// an unparseable field is skipped rather than dropping the whole record, because
/// a snapshot with one missing description is far better than silence.
/// </summary>
public sealed class ProtocolParser
{
    public const string Prefix = "A11Y1|";

    private Snapshot? _current;

    /// <summary>Emitted when a snapshot block has been fully received.</summary>
    public event Action<Snapshot>? SnapshotReady;

    /// <summary>Emitted for the mod's boot line, so the companion knows the mod loaded.</summary>
    public event Action<string>? ModBooted;

    /// <summary>Run-scope facts the combat hooks cannot see (character, floor).</summary>
    public event Action<string, string>? RunFact;

    /// <summary>Per-snapshot context facts (floor, turn).</summary>
    public event Action<string, string>? Context;

    /// <summary>
    /// Feeds one raw stdout line. Returns true if the line belonged to the protocol.
    /// hxcpp prefixes traces with a source position ("hscript:0: "), which is stripped here.
    /// </summary>
    public bool Feed(string rawLine)
    {
        var line = Strip(rawLine);
        if (line is null) return false;

        var fields = line.Split('|');
        if (fields.Length < 2) return true;

        var kind = fields[1];
        switch (kind)
        {
            case "boot":
                ModBooted?.Invoke(Value(fields, "v") ?? "?");
                return true;

            case "run":
            {
                foreach (var f in fields.Skip(2))
                {
                    var eq = f.IndexOf('=');
                    if (eq > 0) RunFact?.Invoke(f[..eq], f[(eq + 1)..]);
                }
                return true;
            }

            case "ev":
                _current = new Snapshot { Event = fields.Length > 2 ? fields[2] : "" };
                return true;

            case "ctx":
            {
                // Inside a block this decorates the snapshot; after the end marker it
                // is a standalone fact (see snap.hx on why `turn` is emitted late).
                if (_current is not null) _current.Floor = Int(fields, "floor", _current.Floor);
                foreach (var f in fields.Skip(2))
                {
                    var eq = f.IndexOf('=');
                    if (eq > 0) Context?.Invoke(f[..eq], f[(eq + 1)..]);
                }
                return true;
            }

            case "end":
                if (_current is not null)
                {
                    _current.Complete = true;
                    var done = _current;
                    _current = null;
                    SnapshotReady?.Invoke(done);
                }
                return true;
        }

        if (_current is null) return true;
        var target = Who(fields);
        if (target is null) return true;

        switch (kind)
        {
            case "f":
                target.Name = Value(fields, "name") ?? target.Name;
                target.Hp = Int(fields, "hp", target.Hp);
                target.MaxHp = Int(fields, "maxhp", target.MaxHp);
                target.IsPlayer = Bool(fields, "player");
                break;

            case "f2":
                target.Level = Int(fields, "lvl", target.Level);
                target.Gold = Int(fields, "gold", target.Gold);
                target.Dice = Int(fields, "dice", target.Dice);
                target.ExtraDice = Int(fields, "extradice", target.ExtraDice);
                target.Mana = Int(fields, "mana", target.Mana);
                target.MaxMana = Int(fields, "maxmana", target.MaxMana);
                target.Limit = Int(fields, "limit", target.Limit);
                target.LimitMax = Int(fields, "limitmax", target.LimitMax);
                target.UsesCpu = Bool(fields, "cpu");
                break;

            case "s":
                target.Statuses.Add(new StatusInfo
                {
                    Index = Int(fields, "i", target.Statuses.Count),
                    Name = Value(fields, "name") ?? "",
                    Value = Int(fields, "value", 0),
                    Type = Value(fields, "type") ?? "",
                    Hidden = Bool(fields, "hidden"),
                });
                break;

            case "e":
                target.Equipment.Add(new EquipInfo
                {
                    Index = Int(fields, "i", target.Equipment.Count),
                    Name = Value(fields, "name") ?? "",
                    Modifier = Value(fields, "mod") ?? "",
                    Size = Int(fields, "size", 1),
                    Countdown = Int(fields, "cd", 0),
                    CountdownMax = Int(fields, "cdmax", 0),
                    UsedThisBattle = Bool(fields, "used"),
                    TimesUsed = Int(fields, "times", 0),
                });
                break;

            case "d":
                target.DicePool.Add(new DieInfo
                {
                    Index = Int(fields, "i", target.DicePool.Count),
                    Value = Int(fields, "v", 0),
                    Locked = Bool(fields, "locked"),
                    Frozen = Bool(fields, "frozen"),
                    Burning = Bool(fields, "burn"),
                });
                break;

            case "ed":
            {
                var index = Int(fields, "i", -1);
                var equip = target.Equipment.FirstOrDefault(x => x.Index == index);
                if (equip is not null) equip.Description = Value(fields, "desc") ?? "";
                break;
            }
        }
        return true;
    }

    /// <summary>Removes hxcpp's "file.hx:line: " trace prefix and checks the tag.</summary>
    private static string? Strip(string rawLine)
    {
        if (string.IsNullOrEmpty(rawLine)) return null;

        var at = rawLine.IndexOf(Prefix, StringComparison.Ordinal);
        return at < 0 ? null : rawLine[at..].TrimEnd();
    }

    private FighterInfo? Who(string[] fields)
    {
        var who = Value(fields, "who");
        if (_current is null || who is null) return null;
        return who == "self" ? _current.Self : who == "target" ? _current.Target : null;
    }

    private static string? Value(string[] fields, string key)
    {
        var lead = key + "=";
        for (var i = 2; i < fields.Length; i++)
        {
            if (fields[i].StartsWith(lead, StringComparison.Ordinal))
                return fields[i][lead.Length..];
        }
        return null;
    }

    private static int Int(string[] fields, string key, int fallback)
    {
        var raw = Value(fields, key);
        return raw is not null
               && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;
    }

    private static bool Bool(string[] fields, string key) => Value(fields, key) == "1";
}
