using System.Globalization;
using System.Text;

namespace DiceyA11y.Bridge;

public enum GameValueKind { Null, Int, Float, Bool, String, Object, Class, Array, Function, Enum, Other, Error }

/// <summary>
/// One value as the bridge describes it (see Repr in bridge/bridge.cpp):
/// n | i:12 | f:1.5 | b:1 | s:text | o:#h:Class | c:#h:Class | a:#h:len | fn:#h:args |
/// e:#h:Name | x:type | !error message
/// </summary>
public readonly struct GameValue
{
    public string Raw { get; }
    public GameValueKind Kind { get; }

    public GameValue(string? raw)
    {
        Raw = raw ?? "n";
        Kind = Classify(Raw);
    }

    public static readonly GameValue Null = new("n");

    private static GameValueKind Classify(string r)
    {
        if (r.Length == 0 || r == "n") return GameValueKind.Null;
        if (r[0] == '!') return GameValueKind.Error;
        if (r.StartsWith("i:")) return GameValueKind.Int;
        if (r.StartsWith("f:")) return GameValueKind.Float;
        if (r.StartsWith("b:")) return GameValueKind.Bool;
        if (r.StartsWith("s:")) return GameValueKind.String;
        if (r.StartsWith("o:")) return GameValueKind.Object;
        if (r.StartsWith("c:")) return GameValueKind.Class;
        if (r.StartsWith("a:")) return GameValueKind.Array;
        if (r.StartsWith("fn:")) return GameValueKind.Function;
        if (r.StartsWith("e:")) return GameValueKind.Enum;
        return GameValueKind.Other;
    }

    public bool IsNull => Kind == GameValueKind.Null;
    public bool IsError => Kind == GameValueKind.Error;
    public bool Exists => Kind != GameValueKind.Null && Kind != GameValueKind.Error;
    public string Error => IsError ? Raw[1..] : "";

    public bool AsBool(bool fallback = false) => Kind == GameValueKind.Bool ? Raw == "b:1" : fallback;

    public int AsInt(int fallback = 0) => Kind switch
    {
        GameValueKind.Int => int.TryParse(Raw.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : fallback,
        GameValueKind.Float => (int)Math.Round(AsDouble(fallback)),
        _ => fallback,
    };

    public double AsDouble(double fallback = 0) => Kind is GameValueKind.Int or GameValueKind.Float
        && double.TryParse(Raw.AsSpan(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    /// <summary>String content, unescaped. Empty for anything that is not a string.</summary>
    public string AsString() => Kind == GameValueKind.String ? Unescape(Raw.AsSpan(2)) : "";

    /// <summary>"#12", usable as an expression root while the handle is alive.</summary>
    public string Handle
    {
        get
        {
            var parts = Raw.Split(':', 3);
            return parts.Length >= 2 && parts[1].StartsWith('#') ? parts[1] : "";
        }
    }

    /// <summary>Class name of an object or class, constructor name of an enum value.</summary>
    public string TypeName
    {
        get
        {
            if (Kind is not (GameValueKind.Object or GameValueKind.Class or GameValueKind.Enum)) return "";
            var parts = Raw.Split(':', 3);
            return parts.Length == 3 ? Unescape(parts[2]) : "";
        }
    }

    public int ArrayLength
    {
        get
        {
            if (Kind != GameValueKind.Array) return 0;
            var parts = Raw.Split(':', 3);
            return parts.Length == 3 && int.TryParse(parts[2], out var n) ? n : 0;
        }
    }

    public static string Unescape(ReadOnlySpan<char> s)
    {
        if (s.IndexOf('\\') < 0) return s.ToString();
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
            var c = s[++i];
            switch (c)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'x' when i + 2 < s.Length:
                    sb.Append((char)Convert.ToInt32(s.Slice(i + 1, 2).ToString(), 16));
                    i += 2;
                    break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Quotes a string as an expression literal.</summary>
    public static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\t", "\\t") + "\"";

    public override string ToString() => Raw;
}
