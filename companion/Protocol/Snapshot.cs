namespace DiceyA11y.Protocol;

public sealed class DieInfo
{
    public int Index;
    public int Value;
    public bool Locked;
    public bool Frozen;
    public bool Burning;

    public string Describe()
    {
        var flags = new List<string>();
        if (Frozen) flags.Add("frozen");
        if (Locked) flags.Add("locked");
        if (Burning) flags.Add("burning");
        return flags.Count == 0 ? Value.ToString() : $"{Value} {string.Join(" ", flags)}";
    }
}

public sealed class EquipInfo
{
    public int Index;
    public string Name = "";
    public string Modifier = "";
    public int Size;
    public int Countdown;         // remaining
    public int CountdownMax;
    public bool UsedThisBattle;
    public int TimesUsed;
    public string Description = "";

    public string DisplayName => string.IsNullOrEmpty(Modifier) ? Name : Name + Modifier;

    /// <summary>Short form for list navigation: name plus the state that matters.</summary>
    public string Describe()
    {
        var parts = new List<string> { DisplayName };
        if (CountdownMax > 0) parts.Add($"countdown {Countdown} of {CountdownMax}");
        if (UsedThisBattle) parts.Add("used");
        return string.Join(", ", parts);
    }
}

public sealed class StatusInfo
{
    public int Index;
    public string Name = "";
    public int Value;
    public string Type = "";
    public bool Hidden;

    public string Describe() => Value > 1 ? $"{Name} {Value}" : Name;
}

public sealed class FighterInfo
{
    public string Who = "";          // "self" or "target" as the script saw it
    public string Name = "";
    public int Hp;
    public int MaxHp;
    public bool IsPlayer;

    public int Level;
    public int Gold;
    public int Dice;
    public int ExtraDice;
    public int Mana;
    public int MaxMana;
    public int Limit;
    public int LimitMax;
    public bool UsesCpu;

    public List<DieInfo> DicePool = new();
    public List<EquipInfo> Equipment = new();
    public List<StatusInfo> Statuses = new();

    public string HealthPhrase => $"{Hp} of {MaxHp} health";

    public IEnumerable<StatusInfo> VisibleStatuses => Statuses.Where(s => !s.Hidden);
}

/// <summary>One complete reading of combat state, as of one script hook.</summary>
public sealed class Snapshot
{
    public string Event = "";
    public DateTime At = DateTime.UtcNow;
    public FighterInfo Self = new() { Who = "self" };
    public FighterInfo Target = new() { Who = "target" };

    /// <summary>
    /// Hooks fire with self/target swapped depending on whose turn it is, so the
    /// narrator always works in player/enemy terms rather than self/target.
    /// </summary>
    public FighterInfo Player => Self.IsPlayer ? Self : Target;
    public FighterInfo Enemy => Self.IsPlayer ? Target : Self;

    /// <summary>Dungeon floor, from getcurrentfloor(). -1 when the game did not report it.</summary>
    public int Floor = -1;

    public bool Complete;
}
