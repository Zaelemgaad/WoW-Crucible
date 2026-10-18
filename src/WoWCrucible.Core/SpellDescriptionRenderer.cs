using System.Globalization;
using System.Text.RegularExpressions;

namespace WoWCrucible.Core;

public sealed record SpellDescriptionPreview(string Text, IReadOnlyList<string> Notices);

/// <summary>Presentation of WoW spell templates. Source strings are never rewritten.</summary>
public sealed partial class SpellDescriptionRenderer(Func<uint, string, object?> field, Func<string, uint, string, object?> tableField)
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    public static SpellDescriptionRenderer WithoutData { get; } = new((_, _) => null, (_, _, _) => null);
    public SpellDescriptionPreview Render(string? source, uint spellId = 0)
    {
        source ??= "";
        if (source.IndexOfAny(['$', '@', '|']) < 0) return new(source.Trim(), []);
        var notices = new HashSet<string>();
        string text;
        try { text = Expand(source, spellId, new HashSet<uint>(), notices, 0); }
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            notices.Add("Malformed numeric instruction: " + ex.Message);
            text = "Description preview unavailable: this source contains a malformed instruction.";
        }
        // Malformed or unsupported instructions must not escape into any presentation surface.
        if (HasInstructions(text)) { notices.Add("Unrecognized description instruction; inspect the original source field."); text = "Description preview unavailable: this source uses an unsupported instruction."; }
        return new(text.Trim(), notices.ToArray());
    }

    public static bool HasInstructions(string text) => Instruction().IsMatch(text);
    public static string PlainText(string text) => WithoutData.Render(text).Text;
    private string Expand(string text, uint id, HashSet<uint> visiting, HashSet<string> notices, int depth)
    {
        if (depth > 8 || text.Length > 32768) { notices.Add("Description expansion limit reached."); return "[linked description unavailable]"; }
        string Missing(string reason) { notices.Add(reason); return "[value unavailable]"; }
        string Name(uint other) => Convert.ToString(field(other, "Name[enUS]"), Invariant) is { Length: > 0 } name ? Markup(name) : $"Spell {other}";
        string Link(uint other, string column, bool heading)
        {
            if (!visiting.Add(other)) { notices.Add($"Circular spell description reference: {other}."); return Name(other) + " (circular reference)"; }
            try {
                var raw = Convert.ToString(field(other, column), Invariant);
                if (string.IsNullOrEmpty(raw)) { notices.Add($"Missing {column} for spell {other}."); return Name(other) + " (description unavailable)"; }
                return (heading ? Name(other) + ": " : "") + Expand(raw, other, visiting, notices, depth + 1);
            } finally { visiting.Remove(other); }
        }
        text = Markup(text);
        text = Regex.Replace(text, @"@ext:([\s\S]*?):ext@", m => m.Groups[1].Value);
        text = Regex.Replace(text, @"@(ifknown|ifnotknown):(\d+):([\s\S]*?):\1@", m =>
            $"\nIf {Name(uint.Parse(m.Groups[2].Value))} is {(m.Groups[1].Value == "ifknown" ? "known" : "not known")}: {m.Groups[3].Value}");
        text = Regex.Replace(text, @"@s:(\d+)(?::[^@]*)?@", m => "\n" + Link(uint.Parse(m.Groups[1].Value), "Description[enUS]", true));
        text = Regex.Replace(text, @"@(req|unlockby):(\d+)@", m => $"Requires {Name(uint.Parse(m.Groups[2].Value))}.\n");
        text = Regex.Replace(text, @"@wflocation:([^@]*)@", m => "Location: " + m.Groups[1].Value);
        text = Regex.Replace(text, @"@([A-Za-z]+):[^@]*@", m => { notices.Add("Unsupported client annotation: " + m.Groups[1].Value); return "[additional client information unavailable]"; });
        text = Regex.Replace(text, @"\$@spell(name|desc|aura)(\d+)", m => m.Groups[1].Value == "name" ? Name(uint.Parse(m.Groups[2].Value)) : Link(uint.Parse(m.Groups[2].Value), m.Groups[1].Value == "aura" ? "AuraDescription[enUS]" : "Description[enUS]", false), RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\$[gG]([^:;$]*):([^;$]*);", m => m.Groups[1].Value == m.Groups[2].Value ? m.Groups[1].Value : m.Groups[1].Value + "/" + m.Groups[2].Value);
        // No live aura/talent context is assumed; retain both conditional branches.
        text = Regex.Replace(text, @"\$\?([a-zA-Z]*)(\d+)\[([^\[\]]*)\]\[([^\[\]]*)\]", m =>
            $"(If {Name(uint.Parse(m.Groups[2].Value))} applies: {m.Groups[3].Value}; otherwise: {m.Groups[4].Value})");
        text = Regex.Replace(text, @"\$\{([^{}]*)\}(?:\.\d+)?", m => {
            try { return new Formula(m.Groups[1].Value, token => Variable(token, id, true, notices)).Parse().Text; }
            catch (FormatException) { return Missing("Unsupported formula in spell " + id + ": " + m.Groups[1].Value); }
        });
        text = VariablePattern().Replace(text, m => Variable(m.Value, id, false, notices).Text);
        text = Regex.Replace(text, @"\$[lL]([^:;$]*):([^;$]*);", m => m.Groups[2].Value);
        text = Regex.Replace(text, @"\$<[^>]*>|\$[A-Za-z@?][^\s,.;]*", _ => Missing("Unknown spell description variable in " + id + "."));
        text = Regex.Replace(text, @"[ \t]+", " ");
        return Regex.Replace(text, @"\n[ \t]*\n(?:[ \t]*\n)+", "\n\n");
    }

    private Value Variable(string token, uint current, bool formula, HashSet<string> notices)
    {
        var match = Regex.Match(token, @"^\$(\d+)?([A-Za-z]+)(\d*)$");
        if (!match.Success) return Unknown();
        var id = match.Groups[1].Success ? uint.Parse(match.Groups[1].Value) : current;
        var code = match.Groups[2].Value; var index = match.Groups[3].Value.Length == 0 ? 0 : int.Parse(match.Groups[3].Value) - 1;
        var stat = code.ToUpperInvariant() switch {
            "SP" => "spell power", "AP" => "attack power", "RAP" => "ranged attack power", "PL" => "character level",
            "BH" => "healing power", "SPH" => "Holy spell power", "SPN" => "Nature spell power", "SPS" => "Shadow spell power",
            "SPFI" => "Fire spell power", "SPFR" => "Frost spell power", "SPA" => "Arcane spell power",
            "STA" => "Stamina", "INT" => "Intellect", "SPI" => "Spirit", "STR" => "Strength", "AGI" => "Agility",
            "MW" => "main-hand weapon damage", "MWB" => "main-hand base weapon damage", "MWS" => "main-hand weapon speed", "W" => "weapon damage", _ => null };
        if (stat is not null) return new(null, stat);
        double? N(string name) { var raw=field(id,name);return raw is null ? null : Convert.ToDouble(raw,Invariant); }
        double? T(string table, string key, string name) { var keyValue=N(key);var raw=keyValue is null ? null : tableField(table,(uint)keyValue,name);return raw is null ? null : Convert.ToDouble(raw,Invariant); }
        double? duration() => T("SpellDuration","DurationIndex","Duration") / 1000d;
        double? amount() => N($"EffectBasePoints[{index}]") + (N($"EffectDieSides[{index}]") == 0 ? 0 : 1);
        if (index is < 0 or > 2 && match.Groups[3].Value.Length > 0) return Unknown();
        var number = code.ToLowerInvariant() switch {
            "s" or "m" => amount(), "t" => N($"EffectAuraPeriod[{index}]") / 1000d,
            "d" => duration(), "h" => N("ProcChance"), "n" => N("ProcCharges"), "u" => N("CumulativeAura"), "i" => N("MaxTargets"),
            "x" => N($"EffectChainTargets[{index}]"), "e" => N($"EffectMultipleValue[{index}]"), "ppl" => N($"EffectRealPointsPerLevel[{index}]"),
            "a" => T("SpellRadius",$"EffectRadiusIndex[{index}]","Radius"),
            "o" => N($"EffectAuraPeriod[{index}]") is > 0 ? amount()*duration()*1000/N($"EffectAuraPeriod[{index}]") : null,
            _ => null };
        if (code == "M" && number is not null) number=N($"EffectBasePoints[{index}]")+Math.Max(0,N($"EffectDieSides[{index}]")??0);
        if (number is null) return Unknown();
        if (code.Equals("d",StringComparison.OrdinalIgnoreCase) && !formula)
            return new(null,number<0 ? "until cancelled" : Value.Number(number.Value).Text + (number==1 ? " second" : " seconds"));
        return Value.Number(number.Value);
        Value Unknown() { notices.Add($"Unavailable variable {token} for spell {current}."); return new(null,"[value unavailable]"); }
    }

    private static string Markup(string text)
    {
        text = Regex.Replace(text, @"\|H[^|]*\|h([^|]*)\|h", "$1", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\|[TA][\s\S]*?\|[ta]", "[icon]");
        text = Regex.Replace(text, @"\|c[0-9a-fA-F]{8}|\|r", "", RegexOptions.IgnoreCase);
        return text.Replace("|n","\n").Replace("\r\n","\n").Replace("\r","\n");
    }
    [GeneratedRegex(@"\$(?:\d+)?(?:SPFI|SPFR|SPH|SPN|SPS|RAP|PPL|MWB|MWS|SPA|STA|INT|SPI|STR|AGI|SP|AP|PL|BH|MW|[smtahniuoxeprdwbqf])(?![A-Za-z])\d*",RegexOptions.IgnoreCase)]
    private static partial Regex VariablePattern();
    [GeneratedRegex(@"\$(?:\{|<|\?|@|[A-Za-z]|\d+[A-Za-z])|@[A-Za-z]+:|\|(?:c[0-9a-fA-F]|[rHTAht])")]
    private static partial Regex Instruction();

    private sealed record Value(double? Numeric, string Text, int Precedence = 3)
    {
        public static Value Number(double n) => double.IsFinite(n) ? new(n,n.ToString("0.##",Invariant)) : throw new FormatException("Non-finite formula.");
    }
    // A bounded arithmetic parser, never eval or executable Lua/JavaScript.
    private sealed class Formula(string source, Func<string,Value> variable)
    {
        private int _at;
        public Value Parse() {var result=Expression(0);Skip();if(_at!=source.Length)throw new FormatException();return result;}
        private void Skip(){while(_at<source.Length&&char.IsWhiteSpace(source[_at]))_at++;}
        private Value Expression(int min)
        {
            var left=Atom();
            while(true){Skip();if(_at>=source.Length)break;var op=source[_at];var precedence=op is '+' or '-'?1:op is '*' or '/'?2:0;if(precedence==0||precedence<min)break;_at++;var right=Expression(precedence+1);
                if(left.Numeric is {} a&&right.Numeric is {} b)left=Value.Number(op switch {'+'=>a+b,'-'=>a-b,'*'=>a*b,'/'=>b==0?throw new FormatException():a/b,_=>throw new FormatException()});
                else if(op=='*' && (left.Numeric is {} || right.Numeric is {})) {var n=left.Numeric??right.Numeric!.Value;var term=left.Numeric is null?left:right;left=new(null,n is >0 and <1 ? Value.Number(n*100).Text+"% of "+Wrap(term,3) : Value.Number(n).Text+" × "+Wrap(term,3),2);}
                else left=new(null,Wrap(left,precedence)+" "+(op=='*'?"×":op=='/'?"÷":op.ToString())+" "+Wrap(right,precedence+1),precedence);
            }return left;
        }
        private static string Wrap(Value value,int precedence)=>value.Precedence<precedence?"("+value.Text+")":value.Text;
        private Value Atom()
        {
            Skip();if(_at>=source.Length)throw new FormatException();var c=source[_at++];
            if(c=='('){var value=Expression(0);Skip();if(_at>=source.Length||source[_at++]!=')')throw new FormatException();return value;}
            if(c is '+' or '-'){var value=Atom();return c=='+'?value:value.Numeric is {} number?Value.Number(-number):new(null,"−"+Wrap(value,3));}
            var start=_at-1;
            if(c=='$'){while(_at<source.Length&&char.IsLetterOrDigit(source[_at]))_at++;return variable(source[start.._at]);}
            if(char.IsDigit(c)||c=='.'){while(_at<source.Length&&(char.IsDigit(source[_at])||source[_at]=='.'))_at++;if(double.TryParse(source[start.._at],NumberStyles.Float,Invariant,out var number))return Value.Number(number);}
            throw new FormatException();
        }
    }
}
