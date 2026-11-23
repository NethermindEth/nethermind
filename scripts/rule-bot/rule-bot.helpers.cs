// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Obj = System.Collections.Generic.OrderedDictionary<string, object?>;

sealed class BotExit(int code, string? message = null) : Exception(message)
{
    internal int Code { get; } = code;
}

sealed class PyRandom
{
    private const int Size = 624;
    private const int Shift = 397;
    private readonly uint[] _state = new uint[Size];
    private int _index = Size;

    internal PyRandom(BigInteger seed)
    {
        var rest = BigInteger.Abs(seed);
        var key = new List<uint>();
        do
        {
            key.Add((uint)(rest & uint.MaxValue));
            rest >>= 32;
        } while (rest > 0);
        Init(19650218);
        int i = 1, j = 0;
        for (var k = Math.Max(Size, key.Count); k > 0; k--)
        {
            _state[i] = (_state[i] ^ ((_state[i - 1] ^ (_state[i - 1] >> 30)) * 1664525)) + key[j] + (uint)j;
            i++;
            j++;
            if (i >= Size) { _state[0] = _state[Size - 1]; i = 1; }
            if (j >= key.Count) j = 0;
        }
        for (var k = Size - 1; k > 0; k--)
        {
            _state[i] = (_state[i] ^ ((_state[i - 1] ^ (_state[i - 1] >> 30)) * 1566083941)) - (uint)i;
            i++;
            if (i >= Size) { _state[0] = _state[Size - 1]; i = 1; }
        }
        _state[0] = 0x80000000;
    }

    private void Init(uint seed)
    {
        _state[0] = seed;
        for (var i = 1; i < Size; i++) _state[i] = 1812433253 * (_state[i - 1] ^ (_state[i - 1] >> 30)) + (uint)i;
    }

    private uint Next()
    {
        if (_index >= Size)
        {
            for (var k = 0; k < Size; k++)
            {
                var y = (_state[k] & 0x80000000) | (_state[(k + 1) % Size] & 0x7fffffff);
                _state[k] = _state[(k + Shift) % Size] ^ (y >> 1) ^ ((y & 1) != 0 ? 0x9908b0dfu : 0);
            }
            _index = 0;
        }
        var value = _state[_index++];
        value ^= value >> 11;
        value ^= (value << 7) & 0x9d2c5680;
        value ^= (value << 15) & 0xefc60000;
        return value ^ (value >> 18);
    }

    private int Below(int limit)
    {
        var bits = 32 - BitOperations.LeadingZeroCount((uint)limit);
        int value;
        do value = (int)(Next() >> (32 - bits)); while (value >= limit);
        return value;
    }

    internal List<T> Sample<T>(IReadOnlyList<T> population, int count)
    {
        var size = population.Count;
        var result = new List<T>(count);
        var setSize = 21;
        if (count > 5) setSize += (int)Math.Pow(4, Math.Ceiling(Math.Log(count * 3) / Math.Log(4)));
        if (size <= setSize)
        {
            var pool = population.ToArray();
            for (var i = 0; i < count; i++)
            {
                var j = Below(size - i);
                result.Add(pool[j]);
                pool[j] = pool[size - i - 1];
            }
            return result;
        }
        var selected = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            int j;
            do j = Below(size); while (!selected.Add(j));
            result.Add(population[j]);
        }
        return result;
    }
}

sealed class CodePointOrder : IComparer<string>
{
    internal static readonly CodePointOrder Instance = new();

    public int Compare(string? left, string? right)
    {
        var a = left ?? "";
        var b = right ?? "";
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            if (a[i] == b[i]) continue;
            var x = char.IsSurrogate(a[i]) ? a[i] + 0x10000 : a[i];
            var y = char.IsSurrogate(b[i]) ? b[i] + 0x10000 : b[i];
            return x.CompareTo(y);
        }
        return a.Length.CompareTo(b.Length);
    }
}

static partial class RuleBot
{
    internal const string Repo = "NethermindEth/nethermind";
    internal const int ValueMin = 5;
    internal const int SizeMax = 60;
    internal const int SampleSize = 20;
    internal const int SampleMinTrue = 18;
    internal const string BotLabel = "rule-bot";
    internal const int CooldownDays = 56;
    internal const int RouteBTries = 1;
    internal const string Unshipped = "src/Nethermind/Nethermind.Analyzers/AnalyzerReleases.Unshipped.md";
    internal const string EditorConfig = ".editorconfig";
    internal const int WindowDays = 180;
    private const int Page = 100;
    private const string ThreadNodes = "pageInfo { hasNextPage endCursor } nodes { isResolved isOutdated comments(first:1) { nodes { databaseId } } }";
    private const string ThreadFields = "author { login } reviewThreads(first:100) { " + ThreadNodes + " }";

    private const string Word = @"[\p{L}\p{N}_]";
    private const string Edge = "(?:(?<=" + Word + ")(?!" + Word + ")|(?<!" + Word + ")(?=" + Word + "))";
    private const string Space = @"[\s\x1c-\x1f]";
    private const string NotSpace = @"[^\s\x1c-\x1f]";
    private const string Digits = @"(?:\d|[\uD800-\uDBFF][\uDC00-\uDFFF])+";
    private const RegexOptions Ignore = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly (string Gate, double Min)[] GateMinimums = [("prs", 10), ("commenters", 3), ("human", 5), ("validity", 0.6)];
    private static readonly Dictionary<string, string> OverlaySections = new()
    {
        ["[*.cs]"] = "warning",
        ["[src/Nethermind/{*Test*,*Benchmark*}/**.cs]"] = "none",
        ["[tools/*/{*Test*,*Benchmark*}/**.cs]"] = "none"
    };
    private static readonly string[] GuardedPrefixes = [".github/", ".agents/", "scripts/"];
    private static readonly string[] AnalyzerDirs = ["src/Nethermind/Nethermind.Analyzers/", "src/Nethermind/Nethermind.Analyzers.Test/"];
    private static readonly string[] FixRoots = ["src/", "tools/"];
    internal static readonly string[] FixDeniedPrefixes = new[]
    {
        "Nethermind.Analyzers", "Nethermind.Analyzers.Test", "Nethermind.Test.Analyzers", "Nethermind.Test.Analyzers.CodeFixes",
        "Nethermind.Serialization.JsonGenerator", "Nethermind.Serialization.SszGenerator", "Nethermind.Serialization.FluentRlp.Generator",
        "Nethermind.JsonRpc.SourceGenerator", "Nethermind.JsonRpc.SourceGenerator.Build", "Nethermind.BeaconChain.Generators"
    }.Select(project => $"src/Nethermind/{project}/").ToArray();
    private static readonly HashSet<string> TrustedAssociations = ["OWNER", "MEMBER", "COLLABORATOR"];
    private static readonly HashSet<string> Bots = ["claude", "copilot-pull-request-reviewer", "github-actions", "coderabbitai", "gemini-code-assist", "cursor", "chatgpt-codex-connector", "dependabot", "codecov"];
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UTF8Encoding LenientUtf8 = new(false, false);

    [GeneratedRegex(Edge + "(fixed|done|addressed|good catch|nice catch|agreed|agree|updated|changed|resolved|thanks|applied|removed|reverted|will fix|yep|yes)" + Edge, Ignore)]
    private static partial Regex Agree();

    [GeneratedRegex(Edge + "(not (an issue|needed|applicable|relevant|true|correct)|false positive|intended|intentional|by design|won'?t fix|no need|disagree|not necessary|this is fine|irrelevant|incorrect|wrong)" + Edge, Ignore)]
    private static partial Regex Rebut();

    [GeneratedRegex(@"\A(?<path>" + NotSpaceNoParen + @"[^(]*)\((?<line>" + Digits + "),(?<col>" + Digits + @")\): (?<sev>warning|error) (?<id>[A-Z]+\d+): (?<msg>.*?)(?: \[(?<proj>[^\]]+)\])?$")]
    private static partial Regex Diagnostic();
    private const string NotSpaceNoParen = @"[^\s\x1c-\x1f(]";

    [GeneratedRegex("^" + Space + @"*dotnet_diagnostic\.([A-Z]+\d+)\.severity" + Space + "*=" + Space + "*(" + Word + "+)", RegexOptions.Multiline)]
    private static partial Regex Severity();

    [GeneratedRegex(@"^NETH(\d{3})" + Edge, RegexOptions.Multiline)]
    private static partial Regex ReleaseId();

    [GeneratedRegex(@"NETH(\d{3})")]
    private static partial Regex TitleId();

    [GeneratedRegex("^" + Space + @"*solution:" + Space + @"*\[([^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex SolutionMatrix();

    [GeneratedRegex("^" + Space + "+-" + Space + @"+((?:" + Word + @"|[./-])+\.slnx)" + Space + "*$", RegexOptions.Multiline)]
    private static partial Regex ToolSolution();

    [GeneratedRegex(@"\Adotnet_diagnostic\.([A-Z]+\d+)\.severity = (" + Word + @"+)\z")]
    private static partial Regex OverlayRow();

    [GeneratedRegex(@"\A@@ -(" + Digits + @")(?:,(" + Digits + @"))? \+(" + Digits + @")(?:,(" + Digits + "))? @@")]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"\Adiff --git a/(" + NotSpace + "+) b/(" + NotSpace + "+)$")]
    private static partial Regex DiffHeader();

    [GeneratedRegex(@"\A(?:---|\+\+\+) (?:[ab]/)?(" + NotSpace + "+)")]
    private static partial Regex FileHeader();

    [GeneratedRegex(@"\Aindex [0-9a-f]+\.\.[0-9a-f]+ (?!100644)")]
    private static partial Regex IndexMode();

    [GeneratedRegex("mode (120000|100755)|mode change")]
    private static partial Regex RiskyMode();

    [GeneratedRegex(@"public" + Space + "+override" + Space + "+void" + Space + "+Initialize" + Space + @"*\(" + Space + "*AnalysisContext" + Space + "+(" + Word + "+)" + Space + @"*\)" + Space + @"*(=>[^;]*;|\{)")]
    private static partial Regex InitializeMethod();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex TestClassName();

    [GeneratedRegex(@"[Ff]ailed:" + Space + @"*(\d+)")]
    private static partial Regex FailedCount();

    [GeneratedRegex(": error |failed:|total:")]
    private static partial Regex TestSummaryLine();

    [GeneratedRegex("total: [0-9]+|Passed!|Failed!")]
    private static partial Regex TestRunSummary();

    [GeneratedRegex(@"\{\{((?:" + Word + @"|[\uD800-\uDBFF][\uDC00-\uDFFF])+)\}\}")]
    private static partial Regex TemplateKey();

    [GeneratedRegex(@"\A-\d+\z|\A-\d*\.\d+\z")]
    private static partial Regex NegativeNumber();

    internal static string Home = AppContext.BaseDirectory;
    internal static Func<string[], bool, CancellationToken, Task<(int Code, string Output, string Error)>> Exec =
        (argv, echo, token) => ScriptProcess.Run(argv[0], argv[1..], token, echo, capture: !echo);
    internal static Func<string[], CancellationToken, Task<object?>> Api = RealApi;
    internal static Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;

    internal static Obj D(params (string Key, object? Value)[] items)
    {
        var obj = new Obj();
        foreach (var (key, value) in items) obj[key] = value;
        return obj;
    }

    internal static List<object?> L(IEnumerable<object?> items) => [.. items];
    private static Obj O(object? value) => (Obj)value!;
    internal static List<object?> A(object? value) => (List<object?>)value!;
    private static object? G(Obj obj, string key) => obj.TryGetValue(key, out var value) ? value : null;
    private static Obj Child(Obj obj, string key) => G(obj, key) as Obj ?? [];
    internal static string S(object? value) => (string)value!;
    private static long N(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);
    private static IEnumerable<string> Strings(object? value) => A(value).Select(S);

    internal static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        long n => n != 0,
        int n => n != 0,
        BigInteger n => !n.IsZero,
        double d => d != 0,
        Obj o => o.Count > 0,
        List<object?> l => l.Count > 0,
        _ => true
    };

    private static string Text(object? value) => value switch
    {
        null => "None",
        bool b => b ? "True" : "False",
        string s => s,
        double d => FloatText(d),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Dumps(value)
    };

    private static string FloatText(double value)
    {
        var text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('E') ? text.ToLowerInvariant() : text.Contains('.') ? text : text + ".0";
    }

    internal static object? Loads(string text)
    {
        using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 512 });
        return FromElement(document.RootElement);
    }

    private static object? FromElement(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new Obj();
                foreach (var property in element.EnumerateObject()) obj[property.Name] = FromElement(property.Value);
                return obj;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(FromElement).ToList();
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                var raw = element.GetRawText();
                if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0) return double.Parse(raw, CultureInfo.InvariantCulture);
                return long.TryParse(raw, CultureInfo.InvariantCulture, out var number) ? (object)number : BigInteger.Parse(raw, CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    internal static string Dumps(object? value, int? indent = null)
    {
        var output = new StringBuilder();
        WriteJson(output, value, indent, 0);
        return output.ToString();
    }

    private static void WriteJson(StringBuilder output, object? value, int? indent, int depth)
    {
        switch (value)
        {
            case null: output.Append("null"); break;
            case bool b: output.Append(b ? "true" : "false"); break;
            case string s: WriteString(output, s); break;
            case double d: output.Append(FloatText(d)); break;
            case Obj obj:
                WriteItems(output, '{', '}', obj.Count, indent, depth, obj.Select<KeyValuePair<string, object?>, Action>(item => () =>
                {
                    WriteString(output, item.Key);
                    output.Append(": ");
                    WriteJson(output, item.Value, indent, depth + 1);
                }));
                break;
            case List<object?> list:
                WriteItems(output, '[', ']', list.Count, indent, depth, list.Select<object?, Action>(item => () => WriteJson(output, item, indent, depth + 1)));
                break;
            case IFormattable number: output.Append(number.ToString(null, CultureInfo.InvariantCulture)); break;
            default: throw new InvalidOperationException($"not JSON: {value.GetType()}");
        }
    }

    private static void WriteItems(StringBuilder output, char open, char close, int count, int? indent, int depth, IEnumerable<Action> items)
    {
        output.Append(open);
        if (count > 0)
        {
            var first = true;
            foreach (var item in items)
            {
                if (!first) output.Append(indent is null ? ", " : ",");
                first = false;
                if (indent is { } width) output.Append('\n').Append(' ', width * (depth + 1));
                item();
            }
            if (indent is { } closing) output.Append('\n').Append(' ', closing * depth);
        }
        output.Append(close);
    }

    private static void WriteString(StringBuilder output, string text)
    {
        output.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': output.Append("\\\""); break;
                case '\\': output.Append("\\\\"); break;
                case '\n': output.Append("\\n"); break;
                case '\r': output.Append("\\r"); break;
                case '\t': output.Append("\\t"); break;
                case '\f': output.Append("\\f"); break;
                case '\b': output.Append("\\b"); break;
                default:
                    if (c is >= ' ' and <= '~') output.Append(c);
                    else output.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
            }
        }
        output.Append('"');
    }

    private static string Universal(string text) => text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
    internal static string ReadText(string path, bool replace = false) => Universal((replace ? LenientUtf8 : StrictUtf8).GetString(File.ReadAllBytes(path)));
    private static void WriteText(string path, string text) => File.WriteAllText(path, text, StrictUtf8);

    private static List<string> FileLines(string text)
    {
        var lines = text.Split('\n').ToList();
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    internal static object? Load(string path, object? fallback = null) =>
        fallback is not null && !File.Exists(path) ? fallback : Loads(ReadText(path));

    internal static void Save(string path, object? data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        WriteText(path, Dumps(data, 1));
    }

    internal static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        int start = 0, i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c is '\n' or '\r' or '\v' or '\f' or '\u001c' or '\u001d' or '\u001e' or '\u0085' or '\u2028' or '\u2029')
            {
                lines.Add(text[start..i]);
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                start = ++i;
            }
            else i++;
        }
        if (start < text.Length) lines.Add(text[start..]);
        return lines;
    }

    private static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\u001c' and <= '\u001f';

    private static string Strip(string text)
    {
        int start = 0, end = text.Length;
        while (start < end && IsSpace(text[start])) start++;
        while (end > start && IsSpace(text[end - 1])) end--;
        return text[start..end];
    }

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var space = i == text.Length || IsSpace(text[i]);
            if (space && start >= 0) { words.Add(text[start..i]); start = -1; }
            else if (!space && start < 0) start = i;
        }
        return words;
    }

    internal static string Head(string text, int count)
    {
        var i = 0;
        for (var seen = 0; seen < count && i < text.Length; seen++)
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
        return text[..i];
    }

    private static string Flat(string text)
    {
        var output = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(text, i);
                output.Append(category <= UnicodeCategory.OtherLetter || category is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber ? '\uA000' : '\uE000');
                i++;
            }
            else output.Append(c switch { '\u017F' => 's', '\u212A' => 'k', '\u0131' or '\u0130' => 'i', _ => c });
        }
        return output.ToString();
    }

    private static bool Search(Regex pattern, string text) => pattern.IsMatch(Flat(text));

    internal static string TranslatePattern(string pattern)
    {
        var output = new StringBuilder();
        var inClass = false;
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length)
            {
                var next = pattern[++i];
                output.Append((next, inClass) switch
                {
                    ('b', false) => Edge,
                    ('w', false) => Word,
                    ('s', false) => Space,
                    ('S', false) => NotSpace,
                    ('w', true) => @"\p{L}\p{N}_",
                    ('s', true) => @"\s\x1c-\x1f",
                    _ => $"\\{next}"
                });
                continue;
            }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            output.Append(c);
        }
        return output.ToString();
    }

    private static List<(string Key, Regex Pattern)> Clusters() =>
        O(Load(Path.Combine(Home, "clusters.json"))).Select(item => (item.Key, new Regex(TranslatePattern(S(item.Value)), Ignore))).ToList();

    private static BigInteger PyInt(string digits)
    {
        if (digits.Length > 4300) throw new BotExit(1, "integer string too long");
        var value = BigInteger.Zero;
        foreach (var rune in digits.EnumerateRunes()) value = value * 10 + (int)Rune.GetNumericValue(rune);
        return value;
    }

    private static bool AllDigits(Match match, params int[] groups) =>
        groups.All(group => !match.Groups[group].Success || match.Groups[group].Value.EnumerateRunes().All(rune => Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber));

    private static object Compact(BigInteger value) => value >= long.MinValue && value <= long.MaxValue ? (object)(long)value : value;

    private static BigInteger Big(object? value) => value is BigInteger big ? big : N(value);

    internal static double Round2(double value)
    {
        var eighths = value * 8;
        return eighths == Math.Floor(eighths)
            ? Math.Round(value * 100, MidpointRounding.ToEven) / 100
            : double.Parse(value.ToString("F2", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    private static DateOnly IsoDate(string text) => DateOnly.ParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static DateOnly TodayOf(string? today) => string.IsNullOrEmpty(today) ? DateOnly.FromDateTime(DateTime.UtcNow) : IsoDate(today);

    private static IEnumerable<string> Entries(string directory)
    {
        try { return Directory.EnumerateFileSystemEntries(directory).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool Hidden(string path) => Path.GetFileName(path).StartsWith('.');

    private static bool Wild(string path, string prefix, string suffix)
    {
        var name = Path.GetFileName(path);
        return name.Length >= prefix.Length + suffix.Length && name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(suffix, StringComparison.Ordinal);
    }

    private static List<string> GlobIn(string directory, string prefix, string suffix) =>
        Entries(directory).Where(entry => (prefix.StartsWith('.') || !Hidden(entry)) && Wild(entry, prefix, suffix)).ToList();

    private static List<string> GlobTree(string root, string suffix)
    {
        var found = new List<string>();
        void Walk(string directory)
        {
            var entries = Entries(directory).Where(entry => !Hidden(entry)).ToList();
            found.AddRange(entries.Where(entry => Wild(entry, "", suffix)));
            foreach (var entry in entries.Where(Directory.Exists)) Walk(entry);
        }
        Walk(root);
        return found;
    }

    private static async Task<string> Gh(string[] args, CancellationToken token, bool check = true)
    {
        var (code, output, error) = await Exec(["gh", .. args], false, token);
        if (check && code != 0) throw new BotExit(1, $"gh {string.Join(' ', args.Take(3))} failed: {Cut(error.Trim(), 300)}");
        return output;
    }

    private static string Cut(string text, int length) => Head(text, length);

    internal static async Task<object?> RealApi(string[] args, CancellationToken token)
    {
        string[] argv = ["gh", "api", .. args];
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (code, output, _) = await Exec(argv, false, token);
            if (code == 0) return Loads(output);
            if (output.StartsWith("{\"data\":{", StringComparison.Ordinal))
            {
                var data = O(Loads(output));
                if (Truthy(G(data, "errors"))) Console.Error.WriteLine($"collect: partial GraphQL response: {Cut(Dumps(data["errors"]), 300)}");
                return data;
            }
            await Delay(TimeSpan.FromSeconds(20 * (attempt + 1)), token);
        }
        throw new BotExit(1, $"collect: GitHub API failed: {string.Join(' ', args.Take(3))}");
    }

    private static string Login(Obj node) => G(node, "author") is Obj author && Truthy(G(author, "login")) ? S(author["login"]) : "ghost";
    internal static bool IsBot(string name) => Bots.Contains(name) || name.EndsWith("[bot]", StringComparison.Ordinal);

    internal static Obj Slim(Obj comment)
    {
        var user = G(comment, "user") as Obj ?? [];
        var url = S(comment["pull_request_url"]);
        var line = G(comment, "line");
        return D(("id", comment["id"]), ("pr", (long)PyInt(url[(url.LastIndexOf('/') + 1)..])),
            ("author", Truthy(G(user, "login")) ? user["login"] : "ghost"), ("bot", Equals(G(user, "type"), "Bot")),
            ("association", G(comment, "author_association")), ("reply_to", G(comment, "in_reply_to_id")), ("path", comment["path"]),
            ("line", Truthy(line) ? line : G(comment, "original_line")), ("created", comment["created_at"]), ("body", comment["body"]), ("url", comment["html_url"]));
    }

    internal static OrderedDictionary<long, Obj> InWindow(IEnumerable<object?> comments, string since)
    {
        var output = new OrderedDictionary<long, Obj>();
        foreach (var comment in comments.Select(O))
            if (CodePointOrder.Instance.Compare(Head(S(comment["created_at"]), 10), since) >= 0)
                output.TryAdd(N(comment["id"]), Slim(comment));
        return output;
    }

    private static List<long> MatchedPrs(IEnumerable<Obj> comments, List<Regex> patterns) =>
        comments.Where(c => !Truthy(c["reply_to"]) && patterns.Any(rx => Search(rx, Head(S(c["body"]), 400)))).Select(c => N(c["pr"])).Distinct().Order().ToList();

    internal static async Task Collect(string data, int days, string? today, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var since = TodayOf(today).AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var comments = new OrderedDictionary<long, Obj>();
        var page = 0;
        while (true)
        {
            page++;
            var batch = A(await Api(["-X", "GET", $"repos/{Repo}/pulls/comments", "-f", $"since={since}T00:00:00Z", "-f", "sort=created", "-f", "direction=asc", "-f", $"per_page={Page}", "-f", $"page={page}"], token));
            foreach (var (key, comment) in InWindow(batch, since)) comments.TryAdd(key, comment);
            if (batch.Count < Page) break;
        }
        var numbers = MatchedPrs(comments.Values, Clusters().Select(cluster => cluster.Pattern).ToList());
        var prs = new Obj();
        var queries = 0;
        var parts = Repo.Split('/');
        var (owner, name) = (parts[0], parts[1]);
        foreach (var chunk in numbers.Chunk(25))
        {
            var query = string.Join(' ', chunk.Select(n => $"p{n}: pullRequest(number:{n}) {{ {ThreadFields} }}"));
            var result = O(await Api(["graphql", "-f", $"query=query {{ repository(owner:\"{owner}\", name:\"{name}\") {{ {query} }} }}"], token));
            queries++;
            foreach (var (key, value) in O(O(result["data"])["repository"]))
            {
                if (!Truthy(value)) continue;
                var pr = O(value);
                var threads = new Obj();
                var connection = O(pr["reviewThreads"]);
                while (true)
                {
                    foreach (var thread in A(connection["nodes"]).Select(O))
                        foreach (var node in A(O(thread["comments"])["nodes"]).Select(O))
                            threads[Text(node["databaseId"])] = L([thread["isResolved"], thread["isOutdated"]]);
                    var info = O(connection["pageInfo"]);
                    if (!Truthy(info["hasNextPage"])) break;
                    var more = O(await Api(["graphql", "-f", $"query=query {{ repository(owner:\"{owner}\", name:\"{name}\") {{ pullRequest(number:{key[1..]}) {{ reviewThreads(first:100, after:\"{Text(info["endCursor"])}\") {{ {ThreadNodes} }} }} }} }}"], token));
                    queries++;
                    var followUp = O(O(more["data"])["repository"])["pullRequest"];
                    if (!Truthy(followUp)) throw new BotExit(1, $"collect: review threads of PR {key[1..]} missing in the follow-up page");
                    connection = O(O(followUp)["reviewThreads"]);
                }
                prs[key[1..]] = D(("author", Login(pr)), ("threads", threads));
            }
        }
        Save(Path.Combine(data, "comments.json"), D(("since", since), ("comments", L(comments.OrderBy(item => item.Key).Select(item => (object?)item.Value)))));
        Save(Path.Combine(data, "prs.json"), prs);
        Console.WriteLine($"collect: {comments.Count} inline review comments since {since}, {page} REST pages, {numbers.Count} PRs with a matching comment in {queries} GraphQL queries, {Math.Round(started.Elapsed.TotalSeconds, MidpointRounding.ToEven):0}s");
    }

    internal static string Classify(Obj thread, string prAuthor)
    {
        var comments = A(O(thread["comments"])["nodes"]).Select(O).ToList();
        var replies = string.Join(" \n", comments.Skip(1).Where(c => Login(c) == prAuthor).Select(c => S(c["body"])));
        var agreed = Search(Agree(), replies);
        if (Search(Rebut(), replies) && !Search(Agree(), Head(replies, 60))) return "invalid";
        if (Truthy(thread["isOutdated"]) || agreed) return "valid";
        return Truthy(thread["isResolved"]) ? "likely" : "unknown";
    }

    internal static IEnumerable<Obj> FindingsFrom(IReadOnlyList<Obj> comments, Obj prs)
    {
        var replies = new Dictionary<string, List<Obj>>();
        foreach (var comment in comments.OrderBy(c => S(c["created"]), CodePointOrder.Instance))
        {
            if (!Truthy(comment["reply_to"])) continue;
            var key = Text(comment["reply_to"]);
            if (!replies.TryGetValue(key, out var list)) replies[key] = list = [];
            list.Add(comment);
        }
        foreach (var root in comments)
        {
            if (Truthy(root["reply_to"]) || !prs.TryGetValue(Text(root["pr"]), out var value) || !Truthy(value)) continue;
            var pr = O(value);
            if (Equals(root["author"], pr["author"])) continue;
            var state = O(pr["threads"]).TryGetValue(Text(root["id"]), out var found) ? A(found) : [false, false];
            var chain = new List<Obj> { root };
            if (replies.TryGetValue(Text(root["id"]), out var answers)) chain.AddRange(answers);
            var nodes = L(chain.Select(c => (object?)D(("author", D(("login", c["author"]))), ("body", c["body"]))));
            var thread = D(("isResolved", state[0]), ("isOutdated", state[1]), ("comments", D(("nodes", nodes))));
            yield return D(("pr", root["pr"]), ("author", root["author"]), ("bot", Truthy(root["bot"]) || IsBot(S(root["author"]))), ("path", root["path"]), ("line", root["line"]),
                ("created", root["created"]), ("body", root["body"]), ("url", root["url"]), ("association", root["association"]), ("valid", Classify(thread, S(pr["author"]))));
        }
    }

    private static void Findings(string data)
    {
        var comments = O(Load(Path.Combine(data, "comments.json")));
        var rows = FindingsFrom(A(comments["comments"]).Select(O).ToList(), O(Load(Path.Combine(data, "prs.json")))).ToList();
        WriteText(Path.Combine(data, "findings.jsonl"), string.Concat(rows.Select(row => Dumps(row) + "\n")));
        Console.WriteLine($"findings: {rows.Count} review threads opened since {Text(comments["since"])} on PRs with a matching comment");
    }

    private static bool CountedValid(Obj row) => Truthy(row["bot"]) ? Equals(row["valid"], "valid") : row["valid"] is "valid" or "likely";

    internal static Obj GateCluster(IReadOnlyList<Obj> rows)
    {
        var st = D(("n", (long)rows.Count), ("prs", (long)rows.Select(r => N(r["pr"])).Distinct().Count()), ("commenters", (long)rows.Select(r => S(r["author"])).Distinct().Count()),
            ("human", (long)rows.Count(r => !Truthy(r["bot"]))), ("valid", (long)rows.Count(CountedValid)));
        st["validity"] = rows.Count > 0 ? Round2((double)N(st["valid"]) / rows.Count) : 0.0;
        var fails = GateMinimums.Where(gate => Convert.ToDouble(st[gate.Gate], CultureInfo.InvariantCulture) < gate.Min).Select(gate => (object?)gate.Gate).ToList();
        st["fails"] = fails;
        st["pass"] = fails.Count == 0;
        return st;
    }

    internal static List<object?> TrustedExamples(IEnumerable<Obj> rows, int count = 5) =>
        L(rows.Where(r => CountedValid(r) && !Truthy(r["bot"]) && G(r, "association") is string association && TrustedAssociations.Contains(association)).Take(count)
            .Select(r => (object?)D(("url", r["url"]), ("author", r["author"]), ("path", r["path"]), ("line", r["line"]), ("body", Head(S(r["body"]), 500)))));

    private static void Gates(string data)
    {
        var path = Path.Combine(data, "findings.jsonl");
        var rows = File.Exists(path) ? FileLines(ReadText(path)).Select(line => O(Loads(line))).ToList() : [];
        var output = new Obj();
        foreach (var (key, pattern) in Clusters())
        {
            var mine = rows.Where(r => Search(pattern, Head(S(r["body"]), 400))).ToList();
            var gate = GateCluster(mine);
            gate["examples"] = TrustedExamples(mine);
            output[key] = gate;
        }
        Save(Path.Combine(data, "gates.json"), output);
        foreach (var (key, value) in output.OrderByDescending(item => N(O(item.Value)["prs"])))
        {
            var s = O(value);
            var verdict = Truthy(s["pass"]) ? "PASS" : "FAIL " + string.Join(',', Strings(s["fails"]));
            Console.WriteLine($"gates: {key,-11} PRs {N(s["prs"]),4} commenters {N(s["commenters"]),3} human {N(s["human"]),4} validity {((double)s["validity"]!).ToString("F2", CultureInfo.InvariantCulture)} {verdict}");
        }
    }

    internal static List<Obj> EditorConfigRules(string text)
    {
        string? section = null;
        var rows = new List<Obj>();
        var number = 0;
        foreach (var line in SplitLines(text))
        {
            number++;
            var stripped = Strip(line);
            if (stripped.StartsWith('[') && stripped.EndsWith(']'))
            {
                section = stripped.Length >= 2 ? stripped[1..^1] : "";
                continue;
            }
            var match = Severity().Match(line);
            if (match.Success && match.Index == 0) rows.Add(D(("id", match.Groups[1].Value), ("severity", match.Groups[2].Value), ("section", section), ("line", (long)number)));
        }
        return rows;
    }

    internal static bool EnforcedOnMaster(string ruleId, IReadOnlyList<Obj> rows, IReadOnlyDictionary<string, string> sdkDefaults)
    {
        var general = rows.Where(r => Equals(r["id"], ruleId) && r["section"] is "*" or "*.cs" or "*.{cs,vb}" && Equals(r["id"], ruleId)).ToList();
        if (general.Count > 0) return general[^1]["severity"] is "warning" or "error";
        return sdkDefaults.TryGetValue(ruleId, out var severity) && severity is "warning" or "error";
    }

    private static void Catalog(string repo, string sdk, string data)
    {
        var rows = EditorConfigRules(ReadText(Path.Combine(repo, ".editorconfig")));
        var sdkDefaults = new Dictionary<string, string>();
        foreach (var file in GlobIn(Path.Combine(sdk, "analyzers/build/config"), "analysislevel_", "_default.globalconfig"))
            foreach (Match match in Severity().Matches(ReadText(file))) sdkDefaults[match.Groups[1].Value] = match.Groups[2].Value;
        foreach (var file in GlobTree(repo, ".globalconfig"))
            foreach (Match match in Severity().Matches(ReadText(file))) rows.Add(D(("id", match.Groups[1].Value), ("severity", match.Groups[2].Value), ("section", "*"), ("line", 0L)));
        var routes = O(Load(Path.Combine(Home, "routes.json")));
        var ids = routes.Values.SelectMany(route => Strings(O(route)["ids"])).Distinct().Order(CodePointOrder.Instance).ToList();
        var output = new Obj();
        foreach (var id in ids) output[id] = D(("enforced", EnforcedOnMaster(id, rows, sdkDefaults)));
        Save(Path.Combine(data, "catalog.json"), output);
        var enforced = string.Join(' ', ids.Where(id => Truthy(O(output[id])["enforced"])));
        Console.WriteLine($"catalog: already enforced on master: {(enforced.Length > 0 ? enforced : "none")}");
    }

    private static HashSet<BigInteger> NethIds(string repo)
    {
        var ids = new HashSet<BigInteger>();
        foreach (var file in GlobIn(Path.Combine(repo, "src/Nethermind/Nethermind.Analyzers"), "AnalyzerReleases.", ".md"))
            foreach (Match match in ReleaseId().Matches(ReadText(file))) ids.Add(PyInt(match.Groups[1].Value));
        return ids;
    }

    internal static string NextNethId(IEnumerable<BigInteger> taken, IEnumerable<BigInteger> reserved) =>
        $"NETH{taken.Concat(reserved).Append(BigInteger.Zero).Max() + 1:D3}";

    private static int DaysSince(DateOnly today, object? stamp) => today.DayNumber - IsoDate(Head(S(stamp), 10)).DayNumber;

    internal static HashSet<string> RecentlyTried(IEnumerable<Obj> botPrs, IEnumerable<string> clusters, DateOnly today)
    {
        var keys = clusters.ToList();
        return botPrs.Where(pr => DaysSince(today, pr["createdAt"]) < CooldownDays)
            .SelectMany(pr => keys.Where(k => S(pr["headRefName"]).StartsWith($"rule-bot/{k}-", StringComparison.Ordinal))).ToHashSet();
    }

    internal static Obj RejectedIds(IEnumerable<Obj> botPrs, Obj routes, DateOnly today)
    {
        var output = new Obj();
        foreach (var pr in botPrs)
        {
            if (!Equals(pr["state"], "CLOSED") || DaysSince(today, pr["closedAt"]) >= CooldownDays) continue;
            foreach (var (key, value) in routes)
            {
                var route = O(value);
                var prefix = $"rule-bot/{key}-";
                var head = S(pr["headRefName"]);
                if (!Equals(route["route"], "A") || !head.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var tokens = head[prefix.Length..].Split('-').ToHashSet();
                foreach (var id in Strings(route["ids"]))
                    if (tokens.Contains(id.ToLowerInvariant())) output[id] = pr["number"];
            }
        }
        return output;
    }

    internal static List<string> Rotate(List<string> queue, DateOnly today)
    {
        var k = queue.Count > 0 ? (today.DayNumber + 1) / 7 % queue.Count : 0;
        return [.. queue.Skip(k), .. queue.Take(k)];
    }

    internal static async Task Plan(string repo, string data, string? todayText, bool offline, string? githubOutput, CancellationToken token)
    {
        var gates = O(Load(Path.Combine(data, "gates.json")));
        var catalog = O(Load(Path.Combine(data, "catalog.json")));
        var routes = O(Load(Path.Combine(Home, "routes.json")));
        var today = TodayOf(todayText);
        var botPrs = offline ? [] : A(Loads(await Gh(["pr", "list", "-R", Repo, "--state", "all", "--label", BotLabel, "--json", "number,state,headRefName,createdAt,closedAt", "--limit", "100"], token))).Select(O).ToList();
        var tried = RecentlyTried(botPrs, routes.Keys, today);
        var rejected = RejectedIds(botPrs, routes, today);
        var measure = new List<string>();
        var queue = new List<string>();
        foreach (var (key, value) in gates.OrderByDescending(item => N(O(item.Value)["prs"])))
        {
            var route = routes.TryGetValue(key, out var found) ? O(found) : D(("route", "?"), ("ids", new List<object?>()));
            if (!Truthy(O(value)["pass"])) continue;
            if (Equals(route["route"], "A"))
            {
                foreach (var id in Strings(route["ids"]).ToList())
                    if (!measure.Contains(id) && !rejected.ContainsKey(id) && !Truthy(G(Child(catalog, id), "enforced"))) measure.Add(id);
            }
            else if (Equals(route["route"], "B") && !tried.Contains(key)) queue.Add(key);
        }
        var botOpen = botPrs.Where(pr => Equals(pr["state"], "OPEN")).ToList();
        if (botOpen.Count > 0)
        {
            Console.WriteLine($"plan: no analyzer attempted, bot PR already open: #{Text(botOpen[0]["number"])}");
            queue = [];
        }
        queue = Rotate(queue, today).Take(RouteBTries).ToList();
        var routeB = queue.FirstOrDefault();
        var reserved = new HashSet<BigInteger>();
        if (!offline)
            foreach (var pr in A(Loads(await Gh(["pr", "list", "-R", Repo, "--state", "open", "--search", "NETH0 in:title", "--json", "title", "--limit", "50"], token))).Select(O))
                foreach (Match match in TitleId().Matches(S(pr["title"]))) reserved.Add(PyInt(match.Groups[1].Value));
        var routeBId = routeB is null ? null : NextNethId(NethIds(repo), reserved);
        var output = D(("measure_ids", L(measure)), ("rejected_ids", rejected), ("route_b", routeB), ("route_b_id", routeBId),
            ("route_b_queue", L(queue.Select(k => (object?)D(("cluster", k), ("why", O(routes[k])["why"]))))));
        Save(Path.Combine(data, "plan.json"), output);
        WriteText(Path.Combine(data, "measure_ids.txt"), string.Join('\n', measure) + "\n");
        Console.WriteLine($"plan: route A ids to measure: {(measure.Count > 0 ? string.Join(' ', measure) : "none")}");
        if (rejected.Count > 0)
            Console.WriteLine($"plan: route A ids cooling down after a closed rule-bot PR: {string.Join(' ', rejected.OrderBy(item => item.Key, CodePointOrder.Instance).Select(item => $"{item.Key} (#{Text(item.Value)})"))}");
        Console.WriteLine($"plan: route B clusters to try: {(queue.Count > 0 ? string.Join(' ', queue) : "none")} {routeBId ?? ""}");
        if (!string.IsNullOrEmpty(githubOutput))
            File.AppendAllText(githubOutput, $"route_b={routeB ?? ""}\nroute_b_id={routeBId ?? ""}\nskip_measure={(botOpen.Count > 0 || !(measure.Count > 0 || routeB is not null) ? "true" : "false")}\n", StrictUtf8);
    }

    internal static string Overlay(IEnumerable<string> ids, bool excludeTests = false)
    {
        var list = ids.ToList();
        var output = new StringBuilder("\n[*.cs]\n");
        foreach (var id in list) output.Append($"dotnet_diagnostic.{id}.severity = warning\n");
        if (excludeTests)
            foreach (var section in new[] { "src/Nethermind/{*Test*,*Benchmark*}/**.cs", "tools/*/{*Test*,*Benchmark*}/**.cs" })
            {
                output.Append($"\n[{section}]\n");
                foreach (var id in list) output.Append($"dotnet_diagnostic.{id}.severity = none\n");
            }
        return output.ToString();
    }

    private static void ApplyOverlay(string repo, string idsPath, IReadOnlyList<string> extra, bool excludeTests)
    {
        var ids = Words(ReadText(idsPath)).Concat(extra).ToList();
        File.AppendAllText(Path.Combine(repo, ".editorconfig"), Overlay(ids, excludeTests), StrictUtf8);
        Console.WriteLine($"overlay: {string.Join(' ', ids)} at warning{(excludeTests ? " (tests and benchmarks excluded)" : "")}");
    }

    internal static string Scope(string relative)
    {
        var parts = relative.Split('/');
        if (parts.Contains("obj") || parts.Contains("artifacts")) return "generated";
        var project = parts[0] is "src" or "tools" && parts.Length > 3 ? parts[2] : "";
        return project.Contains("Test", StringComparison.Ordinal) || project.Contains("Benchmark", StringComparison.Ordinal) ? "test" : "prod";
    }

    private static int CompareHits(Obj a, Obj b)
    {
        var order = CodePointOrder.Instance.Compare(S(a["id"]), S(b["id"]));
        if (order == 0) order = CodePointOrder.Instance.Compare(S(a["file"]), S(b["file"]));
        if (order == 0) order = Big(a["line"]).CompareTo(Big(b["line"]));
        return order != 0 ? order : Big(a["col"]).CompareTo(Big(b["col"]));
    }

    private static List<Obj> SortHits(IEnumerable<Obj> hits) => hits.Order(Comparer<Obj>.Create(CompareHits)).ToList();

    internal static List<Obj> ParseDiagnostics(IEnumerable<string> lines, string root, IReadOnlySet<string> ids)
    {
        var prefix = root.TrimEnd('/') + "/";
        var seen = new OrderedDictionary<string, Obj>();
        foreach (var raw in lines)
        {
            var match = Diagnostic().Match(raw.TrimEnd('\n'));
            if (!match.Success || !ids.Contains(match.Groups["id"].Value) || !AllDigits(match, Diagnostic().GroupNumberFromName("line"), Diagnostic().GroupNumberFromName("col"))) continue;
            var path = match.Groups["path"].Value.Replace('\\', '/');
            var relative = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
            if (relative.StartsWith('/')) continue;
            var line = PyInt(match.Groups["line"].Value);
            var col = PyInt(match.Groups["col"].Value);
            seen.TryAdd($"{relative}\0{line}\0{col}\0{match.Groups["id"].Value}", D(("id", match.Groups["id"].Value), ("file", relative), ("line", Compact(line)), ("col", Compact(col)), ("scope", Scope(relative)), ("msg", match.Groups["msg"].Value)));
        }
        return SortHits(seen.Values);
    }

    internal static string SolutionOf(string logName)
    {
        var name = logName[(logName.LastIndexOf('/') + 1)..];
        return name[..^4].Replace("__", "/");
    }

    internal static string LogNameOf(string solution) => solution.Replace("/", "__") + ".log";

    internal static List<string> CiSolutions(string workflows)
    {
        var output = new List<string>();
        var matrix = SolutionMatrix().Match(ReadText(Path.Combine(workflows, "build-solutions.yml")));
        if (matrix.Success) output.AddRange(matrix.Groups[1].Value.Split(',').Select(s => $"src/Nethermind/{Strip(s)}.slnx"));
        output.AddRange(ToolSolution().Matches(ReadText(Path.Combine(workflows, "build-tools.yml"))).Select(m => $"tools/{m.Groups[1].Value}"));
        return output;
    }

    private static void SolutionsCommand(string workflows, string? githubOutput)
    {
        var solutions = Dumps(L(CiSolutions(workflows)));
        Console.WriteLine(solutions);
        if (!string.IsNullOrEmpty(githubOutput)) File.AppendAllText(githubOutput, $"solutions={solutions}\n", StrictUtf8);
    }

    internal static string FixDenyTools() => string.Join(',', FixDeniedPrefixes.SelectMany(prefix => new[] { $"Edit({prefix}**)", $"Write({prefix}**)" }));

    private static void Hits(string logs, string root, string idList, string outPath, string? generatedLogs = null, string? generatedId = null)
    {
        if (generatedLogs is null != generatedId is null) throw new BotExit(2, "rule-bot hits: --generated-logs and --generated-id must be used together");
        var ids = idList.Split(',').ToHashSet();
        if (generatedId is not null && !ids.Contains(generatedId)) throw new BotExit(2, $"rule-bot hits: --generated-id {generatedId} is not in --ids");
        var files = GlobTree(logs, ".log").Order(CodePointOrder.Instance).ToList();
        var generatedFiles = generatedLogs is null ? [] : GlobTree(generatedLogs, ".log").Order(CodePointOrder.Instance).ToList();
        var unique = new OrderedDictionary<string, Obj>();
        void Read(IEnumerable<string> sources, IReadOnlySet<string> accepted)
        {
            foreach (var file in sources)
                foreach (var hit in ParseDiagnostics(FileLines(ReadText(file, replace: true)), root, accepted))
                {
                    hit["solution"] = SolutionOf(file);
                    var key = $"{S(hit["file"])}\0{Text(hit["line"])}\0{Text(hit["col"])}\0{S(hit["id"])}";
                    if (!unique.TryGetValue(key, out var kept))
                    {
                        kept = new Obj(hit) { ["solutions"] = new List<object?>() };
                        unique[key] = kept;
                    }
                    A(kept["solutions"]).Add(hit["solution"]);
                }
        }
        Read(files, generatedId is null ? ids : ids.Where(id => id != generatedId).ToHashSet());
        if (generatedId is not null) Read(generatedFiles, new HashSet<string> { generatedId });
        if (generatedId is not null) files = files.Concat(generatedFiles).DistinctBy(SolutionOf).OrderBy(file => file, CodePointOrder.Instance).ToList();
        var found = SortHits(unique.Values);
        var counts = new Obj();
        foreach (var id in ids.Order(CodePointOrder.Instance)) counts[id] = D(("prod", 0L), ("test", 0L), ("generated", 0L));
        foreach (var hit in found)
        {
            var bucket = O(counts[S(hit["id"])]);
            bucket[S(hit["scope"])] = N(bucket[S(hit["scope"])]) + 1;
        }
        Save(outPath, D(("counts", counts), ("hits", L(found)), ("solutions", L(files.Select(SolutionOf)))));
        Console.WriteLine($"hits: {files.Count} build logs: {string.Join(' ', files.Select(SolutionOf))}");
        foreach (var (id, value) in counts)
            Console.WriteLine($"hits: {id,-8} prod {N(O(value)["prod"]),6} test {N(O(value)["test"]),6}");
    }

    internal static List<string> RebuildSet(Obj hitsData, IReadOnlyCollection<string> ids) =>
        A(hitsData["hits"]).Select(O).Where(h => ids.Contains(S(h["id"])) && Equals(h["scope"], "prod")).SelectMany(h => Strings(h["solutions"])).Distinct().Order(CodePointOrder.Instance).ToList();

    private static void PackageCommand(string data, string? githubOutput)
    {
        var chosen = O(O(Load(Path.Combine(data, "candidates.json")))["chosen"] ?? throw new BotExit(1, "package: no chosen candidate"));
        var hits = O(Load(Path.Combine(data, "hits.json")));
        var ids = Strings(chosen["ids"]).ToList();
        var mine = A(hits["hits"]).Select(O).Where(x => ids.Contains(S(x["id"])) && Equals(x["scope"], "prod")).ToList();
        WriteText(Path.Combine(data, "fix-hits.txt"), string.Concat(mine.Select(x => $"{Text(x["id"])}\t{Text(x["file"])}:{Text(x["line"])}:{Text(x["col"])}\t{Text(x["msg"])}\n")));
        WriteText(Path.Combine(data, "package-ids.txt"), string.Join('\n', ids) + "\n");
        var rebuild = RebuildSet(hits, ids);
        Console.WriteLine($"package: {Text(chosen["slug"])} {mine.Count} prod hits to fix, rebuild {string.Join(' ', rebuild)}");
        if (!string.IsNullOrEmpty(githubOutput))
            File.AppendAllText(githubOutput, $"rebuild={string.Join(' ', rebuild)}\nids={string.Join(',', ids)}\n", StrictUtf8);
    }

    internal static string Verdict(long prod, bool enforced)
    {
        if (enforced) return "skip: already enforced on master";
        if (prod < ValueMin) return $"skip: {prod} prod hits < {ValueMin}";
        return prod > SizeMax ? $"defer: {prod} prod hits > {SizeMax}" : "propose";
    }

    internal static List<List<string>> GroupByCap(IEnumerable<string> ids, IReadOnlyDictionary<string, long> counts)
    {
        var groups = new List<List<string>>();
        var group = new List<string>();
        long total = 0;
        foreach (var id in ids.OrderBy(i => counts[i]))
        {
            if (total + counts[id] > SizeMax && group.Count > 0)
            {
                groups.Add(group);
                group = [];
                total = 0;
            }
            group.Add(id);
            total += counts[id];
        }
        if (group.Count > 0) groups.Add(group);
        return groups;
    }

    private static async Task<List<long>> OpenPrsFor(string ruleId, CancellationToken token)
    {
        var output = await Gh(["pr", "list", "-R", Repo, "--state", "open", "--search", $"{ruleId} in:title,body", "--json", "number", "--limit", "20"], token, check: false);
        return A(Loads(output.Length > 0 ? output : "[]")).Select(x => N(O(x)["number"])).Order().ToList();
    }

    private static long CountOf(Obj counts, string id, string key) => G(Child(counts, id), key) is { } value ? N(value) : 0;

    internal static async Task Propose(string data, bool offline, string? githubOutput, CancellationToken token)
    {
        var gates = O(Load(Path.Combine(data, "gates.json")));
        var catalog = O(Load(Path.Combine(data, "catalog.json")));
        var routes = O(Load(Path.Combine(Home, "routes.json")));
        var plan = O(Load(Path.Combine(data, "plan.json")));
        var counts = O(O(Load(Path.Combine(data, "hits.json")))["counts"]);
        var routeB = O(Load(Path.Combine(data, "route_b.json"), new Obj()));
        var decisions = new List<Obj>();
        var packages = new List<Obj>();
        foreach (var (key, value) in gates.OrderByDescending(item => N(O(item.Value)["prs"])))
        {
            var route = Child(routes, key);
            if (!Truthy(O(value)["pass"]) || !Equals(G(route, "route"), "A")) continue;
            var accepted = new List<string>();
            var rejected = Child(plan, "rejected_ids");
            foreach (var id in Strings(route["ids"]))
            {
                var prod = CountOf(counts, id, "prod");
                var verdict = Verdict(prod, Truthy(G(Child(catalog, id), "enforced")));
                if (rejected.TryGetValue(id, out var number)) verdict = $"skip: rule-bot PR #{Text(number)} closed without merge in the last {CooldownDays} days";
                decisions.Add(D(("cluster", key), ("id", id), ("route", "A"), ("prod", prod), ("test", CountOf(counts, id, "test")), ("verdict", verdict)));
                if (verdict == "propose") accepted.Add(id);
            }
            foreach (var group in GroupByCap(accepted, accepted.ToDictionary(id => id, id => N(O(counts[id])["prod"]))))
                packages.Add(D(("route", "A"), ("cluster", key), ("ids", L(group)), ("prod", group.Sum(id => N(O(counts[id])["prod"]))), ("test", group.Sum(id => N(O(counts[id])["test"])))));
        }
        if (Truthy(G(plan, "route_b")))
        {
            var id = S(plan["route_b_id"]);
            var cluster = routeB.TryGetValue("cluster", out var named) ? named : plan["route_b"];
            var prod = CountOf(counts, id, "prod");
            var fails = new (string Name, object? Ok)[] { ("analyzer tests", G(routeB, "tests_ok")), ("red control", G(routeB, "red_ok")), ("path guard", G(routeB, "guard_ok")), ("precision sample", G(Child(routeB, "precision"), "ok")) }
                .Where(check => !Truthy(check.Ok)).Select(check => check.Name).ToList();
            var verdict = Truthy(G(routeB, "analyzer")) ? Verdict(prod, false) : "propose";
            if (verdict == "propose" && fails.Count > 0)
                verdict = "skip: " + (Truthy(G(routeB, "skip")) ? Text(routeB["skip"]) : string.Join(", ", fails) + " failed");
            decisions.Add(D(("cluster", cluster), ("id", id), ("route", "B"), ("prod", prod), ("test", CountOf(counts, id, "test")), ("verdict", verdict)));
            if (verdict == "propose")
                packages.Add(D(("route", "B"), ("cluster", cluster), ("ids", L([id])), ("prod", prod), ("test", CountOf(counts, id, "test")), ("analyzer", G(routeB, "analyzer"))));
        }
        foreach (var package in packages)
        {
            package["slug"] = $"{Text(package["cluster"])}-{string.Join('-', Strings(package["ids"]).Select(i => i.ToLowerInvariant()))}";
            var open = new SortedSet<long>();
            if (!offline && !Equals(package["route"], "B"))
                foreach (var id in Strings(package["ids"])) open.UnionWith(await OpenPrsFor(id, token));
            package["open_prs"] = L(open.Select(n => (object?)n));
        }
        var botOpen = offline ? [] : A(Loads(await Gh(["pr", "list", "-R", Repo, "--state", "open", "--label", BotLabel, "--json", "number"], token))).Select(O).ToList();
        var chosen = packages.FirstOrDefault(p => !Truthy(p["open_prs"]) && Equals(p["route"], "B")) ?? packages.FirstOrDefault(p => !Truthy(p["open_prs"]));
        if (botOpen.Count > 0) chosen = null;
        Save(Path.Combine(data, "candidates.json"), D(("decisions", L(decisions)), ("packages", L(packages)), ("bot_open_prs", L(botOpen.Select(x => x["number"]))), ("chosen", chosen)));
        foreach (var d in decisions)
            Console.WriteLine($"propose: route {Text(d["route"])} {Text(d["cluster"]),-11} {Text(d["id"]),-8} prod {N(d["prod"]),6} -> {Text(d["verdict"])}");
        foreach (var p in packages)
        {
            var duplicate = Truthy(p["open_prs"]) ? $" duplicate of open #{string.Join(", #", A(p["open_prs"]).Select(Text))}" : "";
            Console.WriteLine($"package: route {Text(p["route"])} {Text(p["slug"])} prod {Text(p["prod"])} test {Text(p["test"])}{duplicate}");
        }
        if (botOpen.Count > 0) Console.WriteLine($"package: none opened, bot PR already open: #{Text(botOpen[0]["number"])}");
        Console.WriteLine($"package: chosen {(chosen is null ? "none" : Text(chosen["slug"]))}");
        if (!string.IsNullOrEmpty(githubOutput))
            File.AppendAllText(githubOutput, $"slug={(chosen is null ? "" : Text(chosen["slug"]))}\nroute={(chosen is null ? "" : Text(chosen["route"]))}\n", StrictUtf8);
    }

    internal static bool OverlayOnly(IEnumerable<string> diff, IReadOnlyCollection<string> ids)
    {
        string? section = null;
        foreach (var line in diff)
        {
            if (line.StartsWith('-')) return false;
            var text = Strip(line.Length > 0 ? line[1..] : "");
            if (text.Length == 0) continue;
            if (text.StartsWith('['))
            {
                if (!OverlaySections.ContainsKey(text)) return false;
                section = text;
            }
            else
            {
                var match = OverlayRow().Match(text);
                if (!match.Success || section is null || !ids.Contains(match.Groups[1].Value) || match.Groups[2].Value != OverlaySections[section]) return false;
            }
        }
        return true;
    }

    private static bool StartsWithAny(string text, IEnumerable<string> prefixes) => prefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal));

    internal static bool FixAllowed(string path) => StartsWithAny(path, FixRoots) && !StartsWithAny(path, FixDeniedPrefixes) && Scope(path) == "prod";

    internal static List<string> CheckPaths(IEnumerable<(string Status, string Path)> changes, IReadOnlyList<string> unshippedDiff, string? newId,
        IReadOnlyCollection<string>? fixFiles = null, IReadOnlyList<string>? editorConfigDiff = null, IReadOnlyCollection<string>? ids = null)
    {
        fixFiles ??= [];
        editorConfigDiff ??= [];
        ids ??= [];
        var bad = new List<string>();
        var hasNewId = !string.IsNullOrEmpty(newId);
        foreach (var (status, path) in changes)
        {
            if (path == Unshipped)
            {
                var removed = unshippedDiff.Where(l => l.StartsWith('-') && !l.StartsWith("---", StringComparison.Ordinal)).ToList();
                var added = unshippedDiff.Where(l => l.StartsWith('+') && !l.StartsWith("+++", StringComparison.Ordinal)).ToList();
                if (status != "M" || removed.Count > 0 || !hasNewId || added.Count != 1 || !added[0][1..].StartsWith($"{newId} |", StringComparison.Ordinal))
                    bad.Add($"{path}: only one appended row for {(hasNewId ? newId : "the new rule")} is allowed");
            }
            else if (path == EditorConfig)
            {
                if (status != "M" || !OverlayOnly(editorConfigDiff, ids)) bad.Add($"{path}: only the rule overlay may be appended");
            }
            else if (!path.EndsWith(".cs", StringComparison.Ordinal) || StartsWithAny(path, GuardedPrefixes))
                bad.Add($"{path}: outside the paths the bot may change");
            else if (!(hasNewId && status == "A" && StartsWithAny(path, AnalyzerDirs)) && !(status == "M" && fixFiles.Contains(path) && FixAllowed(path)))
                bad.Add($"{path}: not a file with a listed hit{(hasNewId ? " or a new file under the analyzer projects" : "")}");
        }
        return bad;
    }

    private static bool TagIn(string line, string set) => line.Length == 0 || set.Contains(line[0]);

    internal static (List<(string Status, string Path)> Changes, List<string> Unshipped, List<string> Edits) PatchChanges(string text)
    {
        var changes = new List<(string, string)>();
        var unshipped = new List<string>();
        var edits = new List<string>();
        string? path = null;
        var status = "";
        bool inUnshipped = false, inEditorConfig = false;
        BigInteger oldLeft = 0, newLeft = 0;
        foreach (var rawLine in FileLines(text))
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (oldLeft > 0 || newLeft > 0)
            {
                if (TagIn(line, " -")) oldLeft--;
                if (TagIn(line, " +")) newLeft--;
                if (inUnshipped && TagIn(line, "+-")) unshipped.Add(line);
                if (inEditorConfig && TagIn(line, "+-")) edits.Add(line);
                continue;
            }
            var match = HunkHeader().Match(line);
            if (match.Success && AllDigits(match, 1, 2, 3, 4))
            {
                oldLeft = match.Groups[2].Success ? PyInt(match.Groups[2].Value) : 1;
                newLeft = match.Groups[4].Success ? PyInt(match.Groups[4].Value) : 1;
                continue;
            }
            match = DiffHeader().Match(line);
            if (match.Success)
            {
                if (path is not null) changes.Add((status, path));
                path = match.Groups[2].Value;
                status = match.Groups[1].Value == path ? "M" : "R";
                inUnshipped = path == Unshipped;
                inEditorConfig = path == EditorConfig;
                continue;
            }
            match = FileHeader().Match(line);
            if (match.Success && match.Groups[1].Value != "/dev/null" && match.Groups[1].Value != path)
            {
                changes.Add(("X", match.Groups[1].Value));
                continue;
            }
            if (path is null) continue;
            if (line.StartsWith("new file mode", StringComparison.Ordinal)) status = line == "new file mode 100644" ? "A" : "X";
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal)) status = "D";
            else if (StartsWithAny(line, ["old mode", "new mode", "rename ", "copy ", "similarity index", "GIT binary patch", "Binary files"])) status = "X";
            else if (IndexMode().IsMatch(line)) status = "X";
        }
        if (path is not null) changes.Add((status, path));
        return (changes, unshipped, edits);
    }

    private static string FixFileOf(string line)
    {
        var field = line.Split('\t')[1];
        var last = field.LastIndexOf(':');
        if (last < 0) return field;
        var previous = last == 0 ? -1 : field.LastIndexOf(':', last - 1);
        return previous < 0 ? field[..last] : field[..previous];
    }

    internal static HashSet<string> FixFilesOf(string? path) =>
        string.IsNullOrEmpty(path) ? [] : FileLines(ReadText(path)).Where(l => l.Count(c => c == '\t') >= 2).Select(FixFileOf).ToHashSet();

    private static async Task<string> Git(string[] args, bool check, CancellationToken token)
    {
        var (code, output, error) = await Exec(["git", .. args], false, token);
        if (check && code != 0) throw new BotExit(1, $"git {string.Join(' ', args)} failed: {Cut(error.Trim(), 300)}");
        if (output.Contains('\uFFFD')) throw new BotExit(1, $"git {string.Join(' ', args.Take(2))}: output is not valid UTF-8");
        return Universal(output);
    }

    internal static IEnumerable<string> NumstatPaths(string output)
    {
        var fields = output.Split('\0');
        for (var i = 0; i < fields.Length - 1; i++)
        {
            var path = fields[i].Split('\t', 3)[2];
            if (path.Length > 0) yield return path;
            else
            {
                yield return fields[++i];
                yield return fields[++i];
            }
        }
    }

    private static async Task<List<string>> PathsOnlyGitSees(string patch, List<(string Status, string Path)> changes, CancellationToken token)
    {
        if (new FileInfo(patch).Length == 0) return [];
        var (code, output, _) = await Exec(["git", "apply", "--numstat", "-z", patch], false, token);
        if (code != 0 || output.Contains('\uFFFD')) return ["patch: git cannot read it"];
        var seen = changes.Select(change => change.Path).ToHashSet();
        return NumstatPaths(output).Where(path => !seen.Contains(path)).Select(path => $"{path}: git applies it but the guard did not parse it").ToList();
    }

    private static async Task<List<string>?> AppendedEditorConfigLines(string baseRef, string? patch, CancellationToken token)
    {
        var directory = Directory.CreateTempSubdirectory("rule-bot-guard-").FullName;
        try
        {
            var copy = Path.Combine(directory, EditorConfig);
            if ((await Exec(["bash", "-c", "git cat-file blob \"$1\" > \"$2\"", "guard", $"{baseRef}:{EditorConfig}", copy], false, token)).Code != 0) return null;
            var before = File.ReadAllBytes(copy);
            if (patch is not null && (await Exec(["git", "-C", directory, "apply", $"--include={EditorConfig}", Path.GetFullPath(patch)], false, token)).Code != 0) return null;
            var result = patch is null ? EditorConfig : copy;
            if (!File.Exists(result)) return null;
            var after = File.ReadAllBytes(result);
            if (after.Length <= before.Length || !after.AsSpan().StartsWith(before) || (before.Length > 0 && before[^1] != (byte)'\n')) return null;
            return FileLines(LenientUtf8.GetString(after, before.Length, after.Length - before.Length)).Select(line => "+" + line).ToList();
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static async Task<int> Guard(string baseRef, string? patch, string? newId, string? files, string? idList, CancellationToken token)
    {
        List<(string Status, string Path)> changes;
        List<string> unshipped, edits, gitBad = [];
        if (!string.IsNullOrEmpty(patch))
        {
            (changes, unshipped, edits) = PatchChanges(LenientUtf8.GetString(File.ReadAllBytes(patch)));
            gitBad = await PathsOnlyGitSees(patch, changes, token);
        }
        else
        {
            changes = [];
            foreach (var line in SplitLines(await Git(["diff", "--name-status", "--no-renames", baseRef], true, token)).Where(l => l.Length > 0))
            {
                var tab = line.IndexOf('\t');
                if (tab < 0) throw new BotExit(1, $"guard: unparsable git status line: {line}");
                changes.Add((line[..tab], line[(tab + 1)..]));
            }
            unshipped = SplitLines(await Git(["diff", "-U0", baseRef, "--", Unshipped], false, token));
            edits = SplitLines(await Git(["diff", "-U0", baseRef, "--", EditorConfig], false, token))
                .Where(l => TagIn(l, "+-") && !l.StartsWith("+++", StringComparison.Ordinal) && !l.StartsWith("---", StringComparison.Ordinal)).ToList();
            foreach (var line in SplitLines(await Git(["diff", "--summary", baseRef], true, token)))
                if (RiskyMode().IsMatch(line)) changes.Add(("X", Words(line)[^1]));
        }
        if (changes.Contains(("M", EditorConfig)))
            edits = await AppendedEditorConfigLines(baseRef, string.IsNullOrEmpty(patch) ? null : patch, token) ?? ["-"];
        var ids = (idList ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var bad = CheckPaths(changes, unshipped, newId, FixFilesOf(files), edits, ids);
        bad.AddRange(changes.Where(change => change.Status == "X").Select(change => $"{change.Path}: symlink, mode, rename, binary or unlisted header change"));
        bad.AddRange(gitBad);
        foreach (var line in bad) Console.WriteLine($"guard: FAIL {line}");
        Console.WriteLine($"guard: {(bad.Count == 0 ? "PASS" : "FAIL")} ({changes.Count} files)");
        return bad.Count == 0 ? 0 : 1;
    }

    private static void SampleCommand(string hitsPath, string id, string repo, BigInteger seed, string outPath)
    {
        var hits = A(O(Load(hitsPath))["hits"]).Select(O).Where(x => Equals(x["id"], id) && Equals(x["scope"], "prod")).ToList();
        var picked = new PyRandom(seed).Sample(hits, Math.Min(SampleSize, hits.Count));
        foreach (var hit in picked)
        {
            var lines = SplitLines(ReadText(Path.Combine(repo, S(hit["file"])), replace: true));
            var line = Big(hit["line"]);
            var low = (int)BigInteger.Max(0, line - 8);
            var high = (int)BigInteger.Min(lines.Count, line + 7);
            hit["context"] = string.Join('\n', Enumerable.Range(low, Math.Max(0, high - low)).Select(n => $"{n + 1,5}{(n + 1 == line ? '>' : ' ')} {lines[n]}"));
        }
        Save(outPath, L(picked));
        Console.WriteLine($"sample: {picked.Count} of {hits.Count} prod hits of {id}, seed {seed}");
    }

    internal static (int TruePositive, int Need, bool Ok) Precision(IReadOnlyList<Obj> labels, int hitCount)
    {
        var need = Math.Min(SampleSize, hitCount);
        var truePositive = labels.Count(l => Equals(G(l, "label"), "true_positive"));
        var ok = need > 0 && labels.Count == need && (need == SampleSize ? truePositive >= SampleMinTrue : truePositive == need);
        return (truePositive, need, ok);
    }

    private static int PrecisionCommand(string labelsPath, string hitsPath, string id, string routeBPath)
    {
        var labels = A(Load(labelsPath, new List<object?>())).Select(O).ToList();
        var count = A(O(Load(hitsPath))["hits"]).Select(O).Count(x => Equals(x["id"], id) && Equals(x["scope"], "prod"));
        var (truePositive, need, ok) = Precision(labels, count);
        var routeB = O(Load(routeBPath, new Obj()));
        routeB["precision"] = D(("true_positive", (long)truePositive), ("labelled", (long)labels.Count), ("needed", (long)need), ("ok", ok));
        Save(routeBPath, routeB);
        Console.WriteLine($"precision: {truePositive}/{need} true positive -> {(ok ? "PASS" : "FAIL")} (gate {SampleMinTrue}/{SampleSize})");
        return ok ? 0 : 1;
    }

    internal static string? StripInitialize(string source)
    {
        var match = InitializeMethod().Match(source);
        if (!match.Success) return null;
        var context = match.Groups[1].Value;
        var stub = $"{{ {context}.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None); {context}.EnableConcurrentExecution(); }}";
        var body = match.Groups[2];
        if (body.Value.StartsWith("=>", StringComparison.Ordinal)) return source[..body.Index] + stub + source[(body.Index + body.Length)..];
        var end = match.Index + match.Length;
        int depth = 1, i = end;
        while (depth > 0 && i < source.Length)
        {
            depth += source[i] == '{' ? 1 : source[i] == '}' ? -1 : 0;
            i++;
        }
        return source[..(end - 1)] + stub + source[i..];
    }

    internal static string[] TestArgv(string? testClass, bool warningsAsErrors = true)
    {
        if (!TestClassName().IsMatch(testClass ?? "")) throw new BotExit(1, $"invalid test class name: '{testClass}'");
        string[] start = ["dotnet", "test", "--project", "src/Nethermind/Nethermind.Analyzers.Test/Nethermind.Analyzers.Test.csproj", "-c", "Release"];
        string[] warnings = warningsAsErrors ? [] : ["-p:TreatWarningsAsErrors=false"];
        return [.. start, .. warnings, "--", "--filter", $"FullyQualifiedName~{testClass}"];
    }

    private static async Task<int> Red(string analyzer, string testClass, CancellationToken token)
    {
        var source = ReadText(analyzer);
        var stub = StripInitialize(source) ?? throw new BotExit(1, "red: Initialize(AnalysisContext) not found");
        WriteText(analyzer, stub);
        (int Code, string Output, string Error) result;
        try { result = await Exec(TestArgv(testClass, warningsAsErrors: false), false, token); }
        finally { WriteText(analyzer, source); }
        var output = Universal(result.Output + result.Error);
        var failed = FailedCount().Match(output);
        var count = failed.Success ? PyInt(failed.Groups[1].Value) : 0;
        var ok = result.Code != 0 && count > 0;
        if (!ok) Console.WriteLine(string.Join('\n', SplitLines(output).Where(l => TestSummaryLine().IsMatch(l)).Take(10)));
        Console.WriteLine($"red: tests with a no-op analyzer exit {result.Code}, failed {count} -> {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    private static string JqText(object? value) => value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        _ => Text(value).TrimEnd('\n')
    };

    private static string? Given(Obj? result, string key) =>
        result is not null && G(result, key) is { } value && !Equals(value, false) ? JqText(value) : null;

    private static Obj? LoadResult(string path)
    {
        try { return Load(path) as Obj; }
        catch (Exception failure) when (failure is not OperationCanceledException) { return null; }
    }

    private static string LastLine(string path, Func<string, bool> matches) =>
        LenientUtf8.GetString(File.ReadAllBytes(path)).Split('\n').LastOrDefault(matches) ?? "";

    private static async Task<int> Logged(string log, string[] argv, CancellationToken token) =>
        (await Exec(["bash", "-c", "log=$1; shift; exec \"$@\" > \"$log\" 2>&1", "route-b", log, .. argv], false, token)).Code;

    private static void AddAttempt(Obj state, string cluster, string result) =>
        ((List<object?>)state["attempts"]!).Add(D(("cluster", cluster), ("result", result)));

    private static async Task<bool> RouteBAttempt(string data, string id, string cluster, string why, Obj gates, Obj state, string statePath, CancellationToken token)
    {
        var resultPath = Path.Combine(data, "analyzer-result.json");
        var examplesPath = Path.Combine(data, "route-b-examples.json");
        var examples = G(gates, cluster) is Obj gate && G(gate, "examples") is { } listed && !Equals(listed, false) ? listed : new List<object?>();
        Save(examplesPath, examples);
        File.Delete(resultPath);
        var tools = $"Read,Glob,Grep,Edit(src/Nethermind/Nethermind.Analyzers/**),Write(src/Nethermind/Nethermind.Analyzers/**),Edit(src/Nethermind/Nethermind.Analyzers.Test/**),Write(src/Nethermind/Nethermind.Analyzers.Test/**),Write({resultPath}),Bash(dotnet build:*),Bash(dotnet test:*)";
        var generation = await Logged(Path.Combine(data, $"analyzer-llm-{cluster}.log"),
            [Path.Combine(Home, "llm.sh"), Path.Combine(Home, "prompts", "analyzer.txt"), tools, $"ID={id}", $"CLUSTER={cluster}", $"WHY={why}", $"EXAMPLES={examplesPath}", $"RESULT={resultPath}"], token);
        Console.WriteLine($"route-b: {cluster} generation exit {generation}");

        var result = LoadResult(resultPath);
        var testClass = Given(result, "test_class") ?? "";
        var analyzer = Given(result, "analyzer") ?? "";
        const string AnalyzerPrefix = "src/Nethermind/Nethermind.Analyzers/";
        if (!TestClassName().IsMatch(testClass) || !analyzer.StartsWith(AnalyzerPrefix, StringComparison.Ordinal) || !analyzer.AsSpan(AnalyzerPrefix.Length).EndsWith(".cs", StringComparison.Ordinal) || !File.Exists(analyzer) || (await Exec(["test", "-f", analyzer], false, token)).Code != 0)
        {
            var skip = Given(result, "skip") ?? "no analyzer produced";
            Console.WriteLine($"route-b: {cluster} skipped: {skip}");
            AddAttempt(state, cluster, $"skipped by the generator: {skip}");
            Save(statePath, state);
            return false;
        }

        await Git(["add", "-N", "."], false, token);
        var self = Path.Combine(Home, "rule-bot");
        string[] withoutToken = ["env", "-u", "CLAUDE_CODE_OAUTH_TOKEN", self];
        var guardOk = (await Exec([self, "guard", "--new-id", id], true, token)).Code == 0;
        var testsLog = Path.Combine(data, "analyzer-tests.log");
        var testsOk = await Logged(testsLog, [.. withoutToken, "test", "--test-class", testClass], token) == 0;
        var summary = LastLine(testsLog, line => TestRunSummary().IsMatch(line));
        var redLog = Path.Combine(data, "analyzer-red.log");
        var redOk = await Logged(redLog, [.. withoutToken, "red", "--analyzer", analyzer, "--test-class", testClass], token) == 0;
        var redSummary = LastLine(redLog, line => line.StartsWith("red:", StringComparison.Ordinal));
        static string Flag(bool value) => value ? "true" : "false";
        Console.WriteLine($"route-b: {id} ({cluster}) guard={Flag(guardOk)} tests={Flag(testsOk)} red={Flag(redOk)} :: {summary}");
        AddAttempt(state, cluster, $"guard {Flag(guardOk)}, tests {Flag(testsOk)}, red control {Flag(redOk)}");
        foreach (var (key, value) in new (string, object?)[] { ("cluster", cluster), ("why", why), ("analyzer", analyzer), ("summary", Given(result, "summary") ?? ""),
            ("guard_ok", guardOk), ("tests_ok", testsOk), ("red_ok", redOk), ("tests_summary", summary), ("red_summary", redSummary) })
            state[key] = value;
        Save(statePath, state);
        return guardOk && testsOk && redOk;
    }

    internal static async Task RouteB(string data, CancellationToken token)
    {
        var plan = O(Load(Path.Combine(data, "plan.json")));
        var gates = O(Load(Path.Combine(data, "gates.json")));
        var queue = A(plan["route_b_queue"]).Select(O).ToList();
        var id = JqText(G(plan, "route_b_id"));
        var statePath = Path.Combine(data, "route_b.json");
        var state = D(("id", id), ("cluster", JqText(queue.Count > 0 ? G(queue[0], "cluster") : null)), ("attempts", new List<object?>()));
        Save(statePath, state);
        foreach (var entry in queue)
        {
            var cluster = JqText(G(entry, "cluster"));
            if (await RouteBAttempt(data, id, cluster, JqText(G(entry, "why")), gates, state, statePath, token))
            {
                await Git(["diff", $"--output={Path.Combine(data, "analyzer.patch")}", "HEAD"], false, token);
                return;
            }
            await Git(["diff", $"--output={Path.Combine(data, $"analyzer-{cluster}.patch")}", "HEAD"], false, token);
            await Git(["reset", "-q", "--hard", "HEAD"], false, token);
            await Git(["clean", "-qfd", "-e", "/rule-bot"], false, token);
        }
        state["skip"] = string.Join("; ", ((List<object?>)state["attempts"]!).Select(O).Select(a => $"{S(a["cluster"])}: {S(a["result"])}"));
        Save(statePath, state);
        Console.WriteLine("route-b: no cluster produced an analyzer that passed the gates");
    }

    internal static List<(string Url, string Author)> Examples(Obj gate, int count = 3) =>
        (G(gate, "examples") is List<object?> list ? list : []).Select(O).Take(count).Select(e => (S(e["url"]), S(e["author"]))).ToList();

    private static List<string> HitsSolutions(string data) =>
        G(O(Load(Path.Combine(data, "hits.json"), new Obj())), "solutions") is List<object?> list ? list.Select(S).ToList() : [];

    private static string RoutesWhy(string cluster) =>
        G(Child(O(Load(Path.Combine(Home, "routes.json"))), cluster), "why") is { } why ? Text(why) : cluster;

    private static void Body(string data, string? headRef, string outPath)
    {
        var chosen = O(O(Load(Path.Combine(data, "candidates.json")))["chosen"] ?? throw new BotExit(1, "body: no chosen candidate"));
        var gate = O(O(Load(Path.Combine(data, "gates.json")))[S(chosen["cluster"])]);
        var routeB = O(Load(Path.Combine(data, "route_b.json"), new Obj()));
        var head = string.IsNullOrEmpty(headRef) ? "master" : headRef;
        var ids = Strings(chosen["ids"]).ToList();
        var idText = string.Join(", ", ids.Select(i => $"`{i}`"));
        var lines = new List<string>();
        if (Equals(chosen["route"], "A")) lines.AddRange([$"Enables {idText} as a build warning for production code and fixes the existing hits.", ""]);
        else lines.AddRange([$"Adds analyzer `{ids[0]}` ({RoutesWhy(S(chosen["cluster"]))}) and fixes the existing hits. No code fix provider is shipped.", ""]);
        var validity = (long)Math.Round((double)gate["validity"]! * 100, MidpointRounding.ToEven);
        lines.AddRange(["## Why", "", $"Reviewers keep asking for this by hand: in the last {WindowDays} days {Text(gate["n"])} review comments on {Text(gate["prs"])} PRs from {Text(gate["commenters"])} people ({Text(gate["human"])} by humans) match this pattern; {validity}% were accepted as valid. Examples:", ""]);
        lines.AddRange(Examples(gate).Select(e => $"- {e.Url} ({e.Author})"));
        lines.AddRange(["", "## Hits", "", $"Full Release build of every CI solution on `{head}` with the rule at warning ({string.Join(", ", HitsSolutions(data).Select(s => $"`{s}`"))}):", ""]);
        lines.Add($"- Production code: {Text(chosen["prod"])}, all fixed in this PR.");
        lines.Add(Truthy(chosen["test"]) ? $"- Tests and benchmarks: {Text(chosen["test"])}, excluded through `.editorconfig` sections." : "- Tests and benchmarks: 0.");
        if (Equals(chosen["route"], "B"))
        {
            var precision = Child(routeB, "precision");
            lines.AddRange(["", "## Analyzer checks", "",
                $"- Tests: {(routeB.TryGetValue("tests_summary", out var tests) ? Text(tests) : "pass")}. With `Initialize` replaced by a no-op the tests fail ({(routeB.TryGetValue("red_summary", out var red) ? Text(red) : "red control")}).",
                $"- Precision: {Text(G(precision, "true_positive"))}/{Text(G(precision, "needed"))} random production hits labelled true positive by a separate review pass (gate {SampleMinTrue}/{SampleSize}).",
                $"- `{Unshipped}` gets one appended row, so a `*.md` code owner has to approve.", "",
                "<details><summary>Precision sample</summary>", ""]);
            foreach (var label in A(Load(Path.Combine(data, "labels.json"), new List<object?>())).Select(O))
                lines.Add($"- `{Text(G(label, "file"))}:{Text(G(label, "line"))}` {Text(G(label, "label"))}: {(label.TryGetValue("reason", out var reason) ? Text(reason) : "")}");
            lines.AddRange(["", "</details>"]);
        }
        lines.AddRange(["", "## Revert", "", "Revert this PR, or set the rule to `none` in `.editorconfig`:", "", "```", string.Join('\n', ids.Select(i => $"dotnet_diagnostic.{i}.severity = none")), "```", "",
            $"Opened by the review rule bot (`.github/workflows/rule-bot.yml`). Label `{BotLabel}`; the bot keeps at most one such PR open."]);
        WriteText(outPath, string.Join('\n', lines) + "\n");
        Console.WriteLine($"body: {outPath}");
    }

    internal static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        TemplateKey().Replace(template, match => match.Groups[1].Value.EnumerateRunes().All(IsWordRune) && values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);

    private static bool IsWordRune(Rune rune) => rune.Value == '_' || Rune.IsLetter(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber;

    private static void RenderCommand(string[] args)
    {
        if (args.Length < 1) throw new BotExit(2, "render: template path required");
        var values = new Dictionary<string, string>();
        foreach (var item in args[1..])
        {
            var split = item.IndexOf('=');
            if (split < 0) throw new BotExit(2, $"render: expected key=value: {item}");
            var value = item[(split + 1)..];
            values[item[..split]] = File.Exists(value) ? ReadText(value) : value;
        }
        Console.Out.Write(Render(ReadText(args[0]), values));
    }

    private sealed record Spec(string[] Required, string[] Optional, string[] Flags);

    private static readonly Dictionary<string, Spec> Commands = new()
    {
        ["collect"] = new(["data"], ["days", "today"], []),
        ["findings"] = new(["data"], [], []),
        ["gates"] = new(["data"], [], []),
        ["catalog"] = new(["sdk", "data"], ["repo"], []),
        ["plan"] = new(["data"], ["repo", "today", "github-output"], ["offline"]),
        ["overlay"] = new(["ids"], ["repo", "extra"], ["exclude-tests"]),
        ["hits"] = new(["logs", "root", "ids", "out"], ["generated-logs", "generated-id"], []),
        ["propose"] = new(["data"], ["github-output"], ["offline"]),
        ["guard"] = new([], ["base", "patch", "new-id", "files", "ids"], []),
        ["sample"] = new(["hits", "id", "out"], ["repo", "seed"], []),
        ["precision"] = new(["labels", "hits", "id", "route-b"], [], []),
        ["red"] = new(["analyzer", "test-class"], [], []),
        ["test"] = new(["test-class"], [], []),
        ["body"] = new(["data", "out"], ["head"], []),
        ["solutions"] = new([], ["workflows", "github-output"], []),
        ["package"] = new(["data"], ["github-output"], []),
        ["deny-tools"] = new([], [], []),
        ["route-b"] = new(["data"], [], [])
    };

    private static bool LooksLikeOption(string token) => token.Length > 1 && token[0] == '-' && !NegativeNumber().IsMatch(token);

    private static (Dictionary<string, string> Values, HashSet<string> Flags, List<string> Extra) ParseArguments(string command, Spec spec, string[] args)
    {
        var values = new Dictionary<string, string>();
        var flags = new HashSet<string>();
        var extra = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal)) throw new BotExit(2, $"rule-bot {command}: unrecognized arguments: {token}");
            var split = token.IndexOf('=');
            var name = split < 0 ? token[2..] : token[2..split];
            if (spec.Flags.Contains(name))
            {
                if (split >= 0) throw new BotExit(2, $"rule-bot {command}: argument --{name}: ignored explicit argument");
                flags.Add(name);
            }
            else if (name == "extra" && spec.Optional.Contains(name))
            {
                extra = split >= 0 ? [token[(split + 1)..]] : [];
                while (split < 0 && i + 1 < args.Length && !LooksLikeOption(args[i + 1])) extra.Add(args[++i]);
            }
            else if (spec.Required.Contains(name) || spec.Optional.Contains(name))
            {
                if (split >= 0) values[name] = token[(split + 1)..];
                else if (i + 1 < args.Length && !LooksLikeOption(args[i + 1])) values[name] = args[++i];
                else throw new BotExit(2, $"rule-bot {command}: argument --{name}: expected one argument");
            }
            else throw new BotExit(2, $"rule-bot {command}: unrecognized arguments: {token}");
        }
        var missing = spec.Required.Where(name => !values.ContainsKey(name)).Select(name => $"--{name}").ToList();
        if (missing.Count > 0) throw new BotExit(2, $"rule-bot {command}: the following arguments are required: {string.Join(", ", missing)}");
        return (values, flags, extra);
    }

    private static T IntOption<T>(Dictionary<string, string> values, string name, string command, T fallback) where T : IParsable<T>
    {
        if (!values.TryGetValue(name, out var text)) return fallback;
        var invalid = new BotExit(2, $"rule-bot {command}: argument --{name}: invalid int value: '{text}'");
        var digits = PyIntText(text) ?? throw invalid;
        return T.TryParse(digits, CultureInfo.InvariantCulture, out var value) ? value : throw new BotExit(1, $"rule-bot {command}: argument --{name}: value out of range: '{text}'");
    }

    private static string? PyIntText(string text)
    {
        var trimmed = text.AsSpan().Trim(PyWhitespace);
        var result = new StringBuilder();
        if (trimmed.Length > 0 && trimmed[0] is '+' or '-')
        {
            if (trimmed[0] == '-') result.Append('-');
            trimmed = trimmed[1..];
        }
        var count = 0;
        var afterDigit = false;
        foreach (var rune in trimmed.EnumerateRunes())
        {
            if (rune.Value == '_')
            {
                if (!afterDigit) return null;
                afterDigit = false;
                continue;
            }
            if (Rune.GetUnicodeCategory(rune) != UnicodeCategory.DecimalDigitNumber) return null;
            result.Append((int)Rune.GetNumericValue(rune));
            afterDigit = true;
            count++;
        }
        return afterDigit && count <= 4300 ? result.ToString() : null;
    }

    private static readonly char[] PyWhitespace = [.. Enumerable.Range(0, char.MaxValue + 1).Select(c => (char)c).Where(c => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f')];

    internal static async Task<int> Run(string[] args, CancellationToken token)
    {
        try
        {
            if (args.Length == 0) throw new BotExit(2, "rule-bot: a subcommand is required");
            if (args[0] == "render") { RenderCommand(args[1..]); return 0; }
            if (!Commands.TryGetValue(args[0], out var spec)) throw new BotExit(2, $"rule-bot: invalid choice: '{args[0]}'");
            var (values, flags, extra) = ParseArguments(args[0], spec, args[1..]);
            string? Opt(string name) => values.GetValueOrDefault(name);
            var repo = Opt("repo") ?? ".";
            switch (args[0])
            {
                case "collect": await Collect(values["data"], IntOption(values, "days", args[0], WindowDays), Opt("today"), token); return 0;
                case "findings": Findings(values["data"]); return 0;
                case "gates": Gates(values["data"]); return 0;
                case "catalog": Catalog(repo, values["sdk"], values["data"]); return 0;
                case "plan": await Plan(repo, values["data"], Opt("today"), flags.Contains("offline"), Opt("github-output"), token); return 0;
                case "overlay": ApplyOverlay(repo, values["ids"], extra, flags.Contains("exclude-tests")); return 0;
                case "hits": Hits(values["logs"], values["root"], values["ids"], values["out"], Opt("generated-logs"), Opt("generated-id")); return 0;
                case "propose": await Propose(values["data"], flags.Contains("offline"), Opt("github-output"), token); return 0;
                case "guard": return await Guard(Opt("base") ?? "HEAD", Opt("patch"), Opt("new-id"), Opt("files"), Opt("ids"), token);
                case "sample": SampleCommand(values["hits"], values["id"], repo, IntOption(values, "seed", args[0], BigInteger.Zero), values["out"]); return 0;
                case "precision": return PrecisionCommand(values["labels"], values["hits"], values["id"], values["route-b"]);
                case "red": return await Red(values["analyzer"], values["test-class"], token);
                case "test": return (await Exec(TestArgv(values["test-class"]), true, token)).Code;
                case "body": Body(values["data"], Opt("head"), values["out"]); return 0;
                case "solutions": SolutionsCommand(Opt("workflows") ?? ".github/workflows", Opt("github-output")); return 0;
                case "package": PackageCommand(values["data"], Opt("github-output")); return 0;
                case "route-b": await RouteB(values["data"], token); return 0;
                default: Console.WriteLine(FixDenyTools()); return 0;
            }
        }
        catch (BotExit exit)
        {
            if (exit.Message.Length > 0 && exit.Message != new BotExit(0).Message) Console.Error.WriteLine(exit.Message);
            return exit.Code;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return 130;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"rule-bot {args[0]}: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }
}
