// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

sealed class BotExit(int code, string? message = null) : Exception(message)
{
    internal int Code { get; } = code;
}

sealed record ApiUser(string? Login, string? Type);
sealed record ApiComment(long Id, string PullRequestUrl, ApiUser? User, string? AuthorAssociation, long? InReplyToId, string Path, long? Line, long? OriginalLine, string CreatedAt, string Body, string HtmlUrl);
sealed record Comment([property: JsonRequired] long Id, [property: JsonRequired] long Pr, [property: JsonRequired] string Author, bool Bot, string? Association, long? ReplyTo, string Path, long? Line, [property: JsonRequired] string Created, string Body, string Url);
sealed record CommentFile(string Since, List<Comment> Comments);
sealed record PrThreads(string Author, Dictionary<string, bool[]> Threads);
sealed record ReviewThread(bool IsResolved, bool IsOutdated, List<(string Author, string Body)> Comments);
sealed record Finding(long Pr, string Author, bool Bot, string Path, long? Line, string Created, string Body, string Url, string? Association, string Valid);
sealed record Example(string Url, string Author, string Path, long? Line, string Body);
sealed record Gate(long N, [property: JsonRequired] long Prs, long Commenters, long Human, long Valid, double Validity, List<string>? Fails, [property: JsonRequired] bool Pass, List<Example>? Examples = null);
sealed record RouteConfig(string Route, List<string> Ids, string? Why);
sealed record CatalogEntry(bool Enforced);
sealed record EditorRule(string Id, string Severity, string? Section, long Line);
sealed record BotPr(long Number, string State, string HeadRefName, string? CreatedAt, string? ClosedAt);
sealed record TitledPr(string Title);
sealed record NumberedPr(long Number);
sealed record QueuedCluster(string Cluster, string? Why);
sealed record PlanFile(List<string> MeasureIds, Dictionary<string, long>? RejectedIds, string? RouteB, string? RouteBId, List<QueuedCluster>? RouteBQueue);
sealed record HitCounts(long Prod, long Test, long Generated);
sealed record HitsFile(Dictionary<string, HitCounts> Counts, List<Hit> Hits, List<string>? Solutions);
sealed record Decision(string Cluster, string Id, string Route, long Prod, long Test, string Verdict);
sealed record Package(string Route, string Cluster, List<string> Ids, long Prod, long Test, string? Analyzer = null, string Slug = "", List<long>? OpenPrs = null);
sealed record CandidatesFile(List<Decision> Decisions, List<Package> Packages, List<long>? BotOpenPrs, Package? Chosen);
sealed record Label(string? LabelName, string? File, long? Line, string? Reason, bool? MatchesWhy = null)
{
    [JsonPropertyName("label")] public string? LabelName { get; init; } = LabelName;
}
sealed record PrecisionResult(long TruePositive, long Labelled, long Needed, bool Ok);
sealed record Attempt(string Cluster, string Result);
sealed record GraphQlAuthor(string? Login);
sealed record GraphQlPageInfo(bool HasNextPage, string? EndCursor);
sealed record GraphQlId(long DatabaseId);
sealed record GraphQlComments(List<GraphQlId> Nodes);
sealed record GraphQlThread(bool IsResolved, bool IsOutdated, GraphQlComments Comments);
sealed record GraphQlThreads(GraphQlPageInfo PageInfo, List<GraphQlThread> Nodes);
sealed record GraphQlPr(GraphQlAuthor? Author, GraphQlThreads ReviewThreads);
sealed record GraphQlData(Dictionary<string, GraphQlPr?> Repository);
sealed record GraphQlResponse(GraphQlData Data, JsonElement? Errors);

sealed record Hit([property: JsonRequired] string Id, string File, long Line, long Col, [property: JsonRequired] string Scope, string Msg)
{
    public string? Solution { get; set; }
    public List<string>? Solutions { get; set; }
    public string? Context { get; set; }
}

sealed class RouteBState
{
    public string? Id { get; set; }
    public string? Cluster { get; set => field = value ?? throw new JsonException("route_b cluster is null"); }
    public List<Attempt>? Attempts { get; set; }
    public string? Why { get; set; }
    public string? Analyzer { get; set; }
    public string? Summary { get; set; }
    public bool? GuardOk { get; set; }
    public bool? TestsOk { get; set; }
    public bool? RedOk { get; set; }
    public string? TestsSummary { get; set; }
    public string? RedSummary { get; set; }
    public string? Skip { get; set; }
    public PrecisionResult? Precision { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? Other { get; set; }
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
    private const string UnknownPatch = "?";
    internal const int WindowDays = 180;
    private const int Page = 100;
    private const string ThreadNodes = "pageInfo { hasNextPage endCursor } nodes { isResolved isOutdated comments(first:1) { nodes { databaseId } } }";
    private const string ThreadFields = "author { login } reviewThreads(first:100) { " + ThreadNodes + " }";

    private const string NotSpace = @"[^\s\x1c-\x1f]";
    private const string NotSpaceNoParen = @"[^\s\x1c-\x1f(]";
    private const RegexOptions Ignore = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly char[] Blank = [.. Enumerable.Range(0, 0x3001).Select(c => (char)c).Where(c => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f')];
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
    private static readonly JsonSerializerOptions FileJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = null,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
        IndentSize = 1,
        MaxDepth = 512
    };
    private static readonly JsonSerializerOptions LineJson = new(FileJson) { WriteIndented = false };
    private static readonly JsonSerializerOptions GitHubJson = new(JsonSerializerDefaults.Web) { MaxDepth = 512 };

    [GeneratedRegex(@"\b(fixed|done|addressed|good catch|nice catch|agreed|agree|updated|changed|resolved|thanks|applied|removed|reverted|will fix|yep|yes)\b", Ignore)]
    private static partial Regex Agree();

    [GeneratedRegex(@"\b(not (an issue|needed|applicable|relevant|true|correct)|false positive|intended|intentional|by design|won'?t fix|no need|disagree|not necessary|this is fine|irrelevant|incorrect|wrong)\b", Ignore)]
    private static partial Regex Rebut();

    [GeneratedRegex(@"\r\n|[\n\r\v\f\x1c-\x1e\x85  ]")]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"\A(?<path>" + NotSpaceNoParen + @"[^(]*)\((?<line>[0-9]+),(?<col>[0-9]+)\): (?<sev>warning|error) (?<id>[A-Z]+\d+): (?<msg>.*?)(?: \[(?<proj>[^\]]+)\])?$")]
    private static partial Regex Diagnostic();

    [GeneratedRegex(@"^\s*dotnet_diagnostic\.([A-Z]+\d+)\.severity\s*=\s*(\w+)", RegexOptions.Multiline)]
    private static partial Regex Severity();

    [GeneratedRegex(@"^NETH([0-9]{3})\b", RegexOptions.Multiline)]
    private static partial Regex ReleaseId();

    [GeneratedRegex("NETH([0-9]{3})")]
    private static partial Regex TitleId();

    [GeneratedRegex(@"^\s*solution:\s*\[([^\]]*)\]", RegexOptions.Multiline)]
    private static partial Regex SolutionMatrix();

    [GeneratedRegex(@"^\s+-\s+((?:\w|[./-])+\.slnx)\s*$", RegexOptions.Multiline)]
    private static partial Regex ToolSolution();

    [GeneratedRegex(@"\Adotnet_diagnostic\.([A-Z]+\d+)\.severity = (\w+)\z")]
    private static partial Regex OverlayRow();

    [GeneratedRegex(@"\A\+\s*dotnet_diagnostic\.([A-Z]+\d+)\.severity\s*=\s*(?:warning|error)\b")]
    private static partial Regex EnabledRuleRow();

    [GeneratedRegex(@"\A@@ -([0-9]+)(?:,([0-9]+))? \+([0-9]+)(?:,([0-9]+))? @@")]
    private static partial Regex HunkHeader();

    [GeneratedRegex(@"\Adiff --git a/(" + NotSpace + "+) b/(" + NotSpace + "+)$")]
    private static partial Regex DiffHeader();

    [GeneratedRegex(@"\A(?:---|\+\+\+) (?:[ab]/)?(" + NotSpace + "+)")]
    private static partial Regex FileHeader();

    [GeneratedRegex(@"\Aindex [0-9a-f]+\.\.[0-9a-f]+ (?!100644)")]
    private static partial Regex IndexMode();

    [GeneratedRegex("mode (120000|100755)|mode change")]
    private static partial Regex RiskyMode();

    [GeneratedRegex(@"public\s+override\s+void\s+Initialize\s*\(\s*AnalysisContext\s+(\w+)\s*\)\s*(=>[^;]*;|\{)")]
    private static partial Regex InitializeMethod();

    [GeneratedRegex(@"\A[A-Za-z_][A-Za-z0-9_]*\z")]
    private static partial Regex TestClassName();

    [GeneratedRegex(@"[Ff]ailed:\s*([0-9]+)")]
    private static partial Regex FailedCount();

    [GeneratedRegex(": error |failed:|total:")]
    private static partial Regex TestSummaryLine();

    [GeneratedRegex("total: [0-9]+|Passed!|Failed!")]
    private static partial Regex TestRunSummary();

    [GeneratedRegex(@"\{\{(\w+)\}\}")]
    private static partial Regex TemplateKey();

    [GeneratedRegex(@"\A-\d+\z|\A-\d*\.\d+\z")]
    private static partial Regex NegativeNumber();

    internal static string Home = AppContext.BaseDirectory;
    internal static Func<string[], bool, CancellationToken, Task<(int Code, string Output, string Error)>> Exec =
        (argv, echo, token) => ScriptProcess.Run(argv[0], argv[1..], token, echo, capture: !echo);
    internal static Func<string[], CancellationToken, Task<string>> Api = RealApi;
    internal static Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;

    private static string Universal(string text) => text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;
    internal static string ReadText(string path, bool replace = false) => Universal((replace ? LenientUtf8 : StrictUtf8).GetString(File.ReadAllBytes(path)));
    private static void WriteText(string path, string text) => File.WriteAllText(path, text, StrictUtf8);

    private static List<string> FileLines(string text)
    {
        var lines = text.Split('\n').ToList();
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    internal static List<string> Lines(string text)
    {
        var lines = LineBreak().Split(text).ToList();
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    internal static T Parse<T>(string json) => JsonSerializer.Deserialize<T>(json, FileJson)!;
    private static T ParseGitHub<T>(string json) => JsonSerializer.Deserialize<T>(json, GitHubJson)!;
    internal static T Load<T>(string path) => Parse<T>(ReadText(path));
    private static T LoadOr<T>(string path) where T : new() => File.Exists(path) ? Load<T>(path) : new T();
    internal static string Dumps<T>(T value) => JsonSerializer.Serialize(value, LineJson);

    internal static void Save<T>(string path, T data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        WriteText(path, JsonSerializer.Serialize(data, FileJson));
    }

    private static string Cut(string text, int length)
    {
        var end = 0;
        for (var count = 0; count < length && end < text.Length; count++) end += char.IsSurrogatePair(text, end) ? 2 : 1;
        return text[..end];
    }

    private static string Show(object? value) => value?.ToString() ?? "None";
    private static long Number(string digits) => long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : long.MaxValue;
    private static List<(string Key, Regex Pattern)> Clusters() =>
        Load<Dictionary<string, string>>(Path.Combine(Home, "clusters.json")).Select(item => (item.Key, new Regex(item.Value, Ignore))).ToList();
    internal static Dictionary<string, RouteConfig> Routes() => Load<Dictionary<string, RouteConfig>>(Path.Combine(Home, "routes.json"));

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

    internal static async Task<string> RealApi(string[] args, CancellationToken token)
    {
        string[] argv = ["gh", "api", .. args];
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var (code, output, _) = await Exec(argv, false, token);
            if (code == 0) return output;
            if (output.StartsWith("{\"data\":{", StringComparison.Ordinal))
            {
                if (ParseGitHub<GraphQlResponse>(output).Errors is { ValueKind: JsonValueKind.Array } errors && errors.GetArrayLength() > 0)
                    Console.Error.WriteLine($"collect: partial GraphQL response: {Cut(errors.GetRawText(), 300)}");
                return output;
            }
            await Delay(TimeSpan.FromSeconds(20 * (attempt + 1)), token);
        }
        throw new BotExit(1, $"collect: GitHub API failed: {string.Join(' ', args.Take(3))}");
    }

    internal static bool IsBot(string name) => Bots.Contains(name) || name.EndsWith("[bot]", StringComparison.Ordinal);

    internal static Comment Slim(ApiComment comment) => new(comment.Id, long.Parse(comment.PullRequestUrl[(comment.PullRequestUrl.LastIndexOf('/') + 1)..], CultureInfo.InvariantCulture),
        string.IsNullOrEmpty(comment.User?.Login) ? "ghost" : comment.User.Login, comment.User?.Type == "Bot", comment.AuthorAssociation, comment.InReplyToId, comment.Path,
        comment.Line is null or 0 ? comment.OriginalLine : comment.Line, comment.CreatedAt, comment.Body, comment.HtmlUrl);

    internal static Dictionary<long, Comment> InWindow(IEnumerable<ApiComment> comments, string since)
    {
        var output = new Dictionary<long, Comment>();
        foreach (var comment in comments)
            if (string.CompareOrdinal(Cut(comment.CreatedAt, 10), since) >= 0) output.TryAdd(comment.Id, Slim(comment));
        return output;
    }

    private static List<long> MatchedPrs(IEnumerable<Comment> comments, List<Regex> patterns) =>
        comments.Where(c => c.ReplyTo is null or 0 && patterns.Any(pattern => pattern.IsMatch(Cut(c.Body, 400)))).Select(c => c.Pr).Distinct().Order().ToList();

    internal static async Task Collect(string data, int days, string? today, CancellationToken token)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        var since = TodayOf(today).AddDays(-days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var comments = new Dictionary<long, Comment>();
        var page = 0;
        while (true)
        {
            page++;
            var batch = Parse<List<ApiComment>>(await Api(["-X", "GET", $"repos/{Repo}/pulls/comments", "-f", $"since={since}T00:00:00Z", "-f", "sort=created", "-f", "direction=asc", "-f", $"per_page={Page}", "-f", $"page={page}"], token));
            foreach (var (key, comment) in InWindow(batch, since)) comments.TryAdd(key, comment);
            if (batch.Count < Page) break;
        }
        var numbers = MatchedPrs(comments.Values, Clusters().Select(cluster => cluster.Pattern).ToList());
        var prs = new Dictionary<string, PrThreads>();
        var queries = 0;
        var parts = Repo.Split('/');
        var (owner, name) = (parts[0], parts[1]);
        foreach (var chunk in numbers.Chunk(25))
        {
            var query = string.Join(' ', chunk.Select(n => $"p{n}: pullRequest(number:{n}) {{ {ThreadFields} }}"));
            var result = ParseGitHub<GraphQlResponse>(await Api(["graphql", "-f", $"query=query {{ repository(owner:\"{owner}\", name:\"{name}\") {{ {query} }} }}"], token));
            queries++;
            foreach (var (key, pr) in result.Data.Repository)
            {
                if (pr is null) continue;
                var threads = new Dictionary<string, bool[]>();
                var connection = pr.ReviewThreads;
                while (true)
                {
                    foreach (var thread in connection.Nodes)
                        foreach (var node in thread.Comments.Nodes)
                            threads[node.DatabaseId.ToString(CultureInfo.InvariantCulture)] = [thread.IsResolved, thread.IsOutdated];
                    if (!connection.PageInfo.HasNextPage) break;
                    var more = ParseGitHub<GraphQlResponse>(await Api(["graphql", "-f", $"query=query {{ repository(owner:\"{owner}\", name:\"{name}\") {{ pullRequest(number:{key[1..]}) {{ reviewThreads(first:100, after:\"{connection.PageInfo.EndCursor}\") {{ {ThreadNodes} }} }} }} }}"], token));
                    queries++;
                    connection = more.Data.Repository.GetValueOrDefault("pullRequest")?.ReviewThreads ?? throw new BotExit(1, $"collect: review threads of PR {key[1..]} missing in the follow-up page");
                }
                prs[key[1..]] = new(string.IsNullOrEmpty(pr.Author?.Login) ? "ghost" : pr.Author.Login, threads);
            }
        }
        Save(Path.Combine(data, "comments.json"), new CommentFile(since, comments.OrderBy(item => item.Key).Select(item => item.Value).ToList()));
        Save(Path.Combine(data, "prs.json"), prs);
        Console.WriteLine($"collect: {comments.Count} inline review comments since {since}, {page} REST pages, {numbers.Count} PRs with a matching comment in {queries} GraphQL queries, {Math.Round(started.Elapsed.TotalSeconds, MidpointRounding.ToEven):0}s");
    }

    internal static string Classify(ReviewThread thread, string prAuthor)
    {
        var replies = string.Join(" \n", thread.Comments.Skip(1).Where(c => c.Author == prAuthor).Select(c => c.Body));
        var agreed = Agree().IsMatch(replies);
        if (Rebut().IsMatch(replies) && !Agree().IsMatch(Cut(replies, 60))) return "invalid";
        if (thread.IsOutdated || agreed) return "valid";
        return thread.IsResolved ? "likely" : "unknown";
    }

    internal static IEnumerable<Finding> FindingsFrom(IReadOnlyList<Comment> comments, IReadOnlyDictionary<string, PrThreads> prs)
    {
        var replies = comments.Where(c => c.ReplyTo is not (null or 0)).OrderBy(c => c.Created, StringComparer.Ordinal).ToLookup(c => c.ReplyTo);
        foreach (var root in comments)
        {
            if (root.ReplyTo is not (null or 0) || !prs.TryGetValue(root.Pr.ToString(CultureInfo.InvariantCulture), out var pr) || root.Author == pr.Author) continue;
            var state = pr.Threads.TryGetValue(root.Id.ToString(CultureInfo.InvariantCulture), out var known) ? known : [false, false];
            var chain = replies[root.Id].Prepend(root).Select(c => (c.Author, c.Body)).ToList();
            yield return new(root.Pr, root.Author, root.Bot || IsBot(root.Author), root.Path, root.Line, root.Created, root.Body, root.Url, root.Association, Classify(new(state[0], state[1], chain), pr.Author));
        }
    }

    private static void Findings(string data)
    {
        var comments = Load<CommentFile>(Path.Combine(data, "comments.json"));
        var rows = FindingsFrom(comments.Comments, Load<Dictionary<string, PrThreads>>(Path.Combine(data, "prs.json"))).ToList();
        WriteText(Path.Combine(data, "findings.jsonl"), string.Concat(rows.Select(row => Dumps(row) + "\n")));
        Console.WriteLine($"findings: {rows.Count} review threads opened since {comments.Since} on PRs with a matching comment");
    }

    private static bool CountedValid(Finding row) => row.Bot ? row.Valid == "valid" : row.Valid is "valid" or "likely";

    internal static Gate GateCluster(IReadOnlyList<Finding> rows)
    {
        long prs = rows.Select(r => r.Pr).Distinct().Count(), commenters = rows.Select(r => r.Author).Distinct().Count(), human = rows.Count(r => !r.Bot), valid = rows.Count(CountedValid);
        var validity = rows.Count > 0 ? Round2((double)valid / rows.Count) : 0.0;
        var fails = new (string Name, bool Ok)[] { ("prs", prs >= 10), ("commenters", commenters >= 3), ("human", human >= 5), ("validity", validity >= 0.6) }
            .Where(gate => !gate.Ok).Select(gate => gate.Name).ToList();
        return new(rows.Count, prs, commenters, human, valid, validity, fails, fails.Count == 0);
    }

    internal static List<Example> TrustedExamples(IEnumerable<Finding> rows, int count = 5) =>
        rows.Where(r => CountedValid(r) && !r.Bot && r.Association is not null && TrustedAssociations.Contains(r.Association)).Take(count)
            .Select(r => new Example(r.Url, r.Author, r.Path, r.Line, Cut(r.Body, 500))).ToList();

    private static void Gates(string data)
    {
        var path = Path.Combine(data, "findings.jsonl");
        var rows = File.Exists(path) ? FileLines(ReadText(path)).Select(Parse<Finding>).ToList() : [];
        var output = new Dictionary<string, Gate>();
        foreach (var (key, pattern) in Clusters())
        {
            var mine = rows.Where(r => pattern.IsMatch(Cut(r.Body, 400))).ToList();
            output[key] = GateCluster(mine) with { Examples = TrustedExamples(mine) };
        }
        Save(Path.Combine(data, "gates.json"), output);
        foreach (var (key, gate) in output.OrderByDescending(item => item.Value.Prs))
        {
            var verdict = gate.Pass ? "PASS" : "FAIL " + string.Join(',', gate.Fails ?? []);
            Console.WriteLine($"gates: {key,-11} PRs {gate.Prs,4} commenters {gate.Commenters,3} human {gate.Human,4} validity {gate.Validity.ToString("F2", CultureInfo.InvariantCulture)} {verdict}");
        }
    }

    internal static List<EditorRule> EditorConfigRules(string text)
    {
        string? section = null;
        var rows = new List<EditorRule>();
        var number = 0;
        foreach (var line in Lines(text))
        {
            number++;
            var stripped = line.Trim(Blank);
            if (stripped.StartsWith('[') && stripped.EndsWith(']'))
            {
                section = stripped.Length >= 2 ? stripped[1..^1] : "";
                continue;
            }
            var match = Severity().Match(line);
            if (match.Success && match.Index == 0) rows.Add(new(match.Groups[1].Value, match.Groups[2].Value, section, number));
        }
        return rows;
    }

    internal static bool EnforcedOnMaster(string ruleId, IReadOnlyList<EditorRule> rows, IReadOnlyDictionary<string, string> sdkDefaults)
    {
        var general = rows.Where(r => r.Id == ruleId && r.Section is "*" or "*.cs" or "*.{cs,vb}" && r.Id == ruleId).ToList();
        if (general.Count > 0) return general[^1].Severity is "warning" or "error";
        return sdkDefaults.TryGetValue(ruleId, out var severity) && severity is "warning" or "error";
    }

    private static void Catalog(string repo, string sdk, string data)
    {
        var rows = EditorConfigRules(ReadText(Path.Combine(repo, ".editorconfig")));
        var sdkDefaults = new Dictionary<string, string>();
        foreach (var file in GlobIn(Path.Combine(sdk, "analyzers/build/config"), "analysislevel_", "_default.globalconfig"))
            foreach (Match match in Severity().Matches(ReadText(file))) sdkDefaults[match.Groups[1].Value] = match.Groups[2].Value;
        foreach (var file in GlobTree(repo, ".globalconfig"))
            foreach (Match match in Severity().Matches(ReadText(file))) rows.Add(new(match.Groups[1].Value, match.Groups[2].Value, "*", 0));
        var ids = Routes().Values.SelectMany(route => route.Ids).Distinct().Order(StringComparer.Ordinal).ToList();
        var output = ids.ToDictionary(id => id, id => new CatalogEntry(EnforcedOnMaster(id, rows, sdkDefaults)));
        Save(Path.Combine(data, "catalog.json"), output);
        var enforced = string.Join(' ', ids.Where(id => output[id].Enforced));
        Console.WriteLine($"catalog: already enforced on master: {(enforced.Length > 0 ? enforced : "none")}");
    }

    private static HashSet<int> NethIds(string repo)
    {
        var ids = new HashSet<int>();
        foreach (var file in GlobIn(Path.Combine(repo, "src/Nethermind/Nethermind.Analyzers"), "AnalyzerReleases.", ".md"))
            foreach (Match match in ReleaseId().Matches(ReadText(file))) ids.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        return ids;
    }

    internal static string NextNethId(IEnumerable<int> taken, IEnumerable<int> reserved) => $"NETH{taken.Concat(reserved).Append(0).Max() + 1:D3}";

    private static int DaysSince(DateOnly today, string? stamp) => today.DayNumber - IsoDate(Cut(stamp!, 10)).DayNumber;

    internal static HashSet<string> RecentlyTried(IEnumerable<BotPr> botPrs, IEnumerable<string> clusters, DateOnly today)
    {
        var keys = clusters.ToList();
        return botPrs.Where(pr => DaysSince(today, pr.CreatedAt) < CooldownDays)
            .SelectMany(pr => keys.Where(k => pr.HeadRefName.StartsWith($"rule-bot/{k}-", StringComparison.Ordinal))).ToHashSet();
    }

    internal static Dictionary<string, long> RejectedIds(IEnumerable<BotPr> botPrs, IReadOnlyDictionary<string, RouteConfig> routes, DateOnly today)
    {
        var output = new Dictionary<string, long>();
        foreach (var pr in botPrs)
        {
            if (pr.State != "CLOSED" || DaysSince(today, pr.ClosedAt) >= CooldownDays) continue;
            foreach (var (key, route) in routes)
            {
                var prefix = $"rule-bot/{key}-";
                if (route.Route != "A" || !pr.HeadRefName.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var tokens = pr.HeadRefName[prefix.Length..].Split('-').ToHashSet();
                foreach (var id in route.Ids)
                    if (tokens.Contains(id.ToLowerInvariant())) output[id] = pr.Number;
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
        var gates = Load<Dictionary<string, Gate>>(Path.Combine(data, "gates.json"));
        var catalog = Load<Dictionary<string, CatalogEntry>>(Path.Combine(data, "catalog.json"));
        var routes = Routes();
        var today = TodayOf(todayText);
        var botPrs = offline ? [] : ParseGitHub<List<BotPr>>(await Gh(["pr", "list", "-R", Repo, "--state", "all", "--label", BotLabel, "--json", "number,state,headRefName,createdAt,closedAt", "--limit", "100"], token));
        var tried = RecentlyTried(botPrs, routes.Keys, today);
        var rejected = RejectedIds(botPrs, routes, today);
        var measure = new List<string>();
        var queue = new List<string>();
        foreach (var (key, gate) in gates.OrderByDescending(item => item.Value.Prs))
        {
            if (!gate.Pass || !routes.TryGetValue(key, out var route)) continue;
            if (route.Route == "A")
                measure.AddRange(route.Ids.Where(id => !measure.Contains(id) && !rejected.ContainsKey(id) && catalog.GetValueOrDefault(id)?.Enforced != true));
            else if (route.Route == "B" && !tried.Contains(key)) queue.Add(key);
        }
        var botOpen = botPrs.Where(pr => pr.State == "OPEN").ToList();
        if (botOpen.Count > 0)
        {
            Console.WriteLine($"plan: no analyzer attempted, bot PR already open: #{botOpen[0].Number}");
            queue = [];
        }
        queue = Rotate(queue, today).Take(RouteBTries).ToList();
        var routeB = queue.FirstOrDefault();
        var reserved = new HashSet<int>();
        if (!offline)
            foreach (var pr in ParseGitHub<List<TitledPr>>(await Gh(["pr", "list", "-R", Repo, "--state", "open", "--search", "NETH0 in:title", "--json", "title", "--limit", "50"], token)))
                foreach (Match match in TitleId().Matches(pr.Title)) reserved.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        var routeBId = routeB is null ? null : NextNethId(NethIds(repo), reserved);
        Save(Path.Combine(data, "plan.json"), new PlanFile(measure, rejected, routeB, routeBId, queue.Select(k => new QueuedCluster(k, routes[k].Why)).ToList()));
        WriteText(Path.Combine(data, "measure_ids.txt"), string.Join('\n', measure) + "\n");
        Console.WriteLine($"plan: route A ids to measure: {(measure.Count > 0 ? string.Join(' ', measure) : "none")}");
        if (rejected.Count > 0)
            Console.WriteLine($"plan: route A ids cooling down after a closed rule-bot PR: {string.Join(' ', rejected.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => $"{item.Key} (#{item.Value})"))}");
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
        var ids = ReadText(idsPath).Split(Blank, StringSplitOptions.RemoveEmptyEntries).Concat(extra).ToList();
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

    private static List<Hit> SortHits(IEnumerable<Hit> hits) => hits.OrderBy(h => h.Id, StringComparer.Ordinal).ThenBy(h => h.File, StringComparer.Ordinal).ThenBy(h => h.Line).ThenBy(h => h.Col).ToList();

    internal static List<Hit> ParseDiagnostics(IEnumerable<string> lines, string root, IReadOnlySet<string> ids)
    {
        var prefix = root.TrimEnd('/') + "/";
        var seen = new Dictionary<(string, long, long, string), Hit>();
        foreach (var raw in lines)
        {
            var match = Diagnostic().Match(raw.TrimEnd('\n'));
            if (!match.Success || !ids.Contains(match.Groups["id"].Value)) continue;
            var path = match.Groups["path"].Value.Replace('\\', '/');
            var relative = path.StartsWith(prefix, StringComparison.Ordinal) ? path[prefix.Length..] : path;
            if (relative.StartsWith('/')) continue;
            var (line, col) = (Number(match.Groups["line"].Value), Number(match.Groups["col"].Value));
            seen.TryAdd((relative, line, col, match.Groups["id"].Value), new(match.Groups["id"].Value, relative, line, col, Scope(relative), match.Groups["msg"].Value));
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
        if (matrix.Success) output.AddRange(matrix.Groups[1].Value.Split(',').Select(s => $"src/Nethermind/{s.Trim(Blank)}.slnx"));
        output.AddRange(ToolSolution().Matches(ReadText(Path.Combine(workflows, "build-tools.yml"))).Select(m => $"tools/{m.Groups[1].Value}"));
        return output;
    }

    private static void SolutionsCommand(string workflows, string? githubOutput)
    {
        var solutions = Dumps(CiSolutions(workflows));
        Console.WriteLine(solutions);
        if (!string.IsNullOrEmpty(githubOutput)) File.AppendAllText(githubOutput, $"solutions={solutions}\n", StrictUtf8);
    }

    internal static string FixDenyTools() => string.Join(',', FixDeniedPrefixes.Select(prefix => $"Edit({prefix}**)"));

    private static void Hits(string logs, string root, string idList, string outPath, string? generatedLogs = null, string? generatedId = null)
    {
        if (generatedLogs is null != generatedId is null) throw new BotExit(2, "rule-bot hits: --generated-logs and --generated-id must be used together");
        var ids = idList.Split(',').ToHashSet();
        if (generatedId is not null && !ids.Contains(generatedId)) throw new BotExit(2, $"rule-bot hits: --generated-id {generatedId} is not in --ids");
        var files = GlobTree(logs, ".log").Order(StringComparer.Ordinal).ToList();
        var generatedFiles = generatedLogs is null ? [] : GlobTree(generatedLogs, ".log").Order(StringComparer.Ordinal).ToList();
        var unique = new Dictionary<(string, long, long, string), Hit>();
        void Read(IEnumerable<string> sources, IReadOnlySet<string> accepted)
        {
            foreach (var file in sources)
                foreach (var hit in ParseDiagnostics(FileLines(ReadText(file, replace: true)), root, accepted))
                {
                    var solution = SolutionOf(file);
                    var key = (hit.File, hit.Line, hit.Col, hit.Id);
                    if (!unique.TryGetValue(key, out var kept)) unique[key] = kept = hit with { Solution = solution, Solutions = [] };
                    kept.Solutions!.Add(solution);
                }
        }
        Read(files, generatedId is null ? ids : ids.Where(id => id != generatedId).ToHashSet());
        if (generatedId is not null)
        {
            Read(generatedFiles, new HashSet<string> { generatedId });
            files = files.Concat(generatedFiles).DistinctBy(SolutionOf).Order(StringComparer.Ordinal).ToList();
        }
        var found = SortHits(unique.Values);
        var counts = ids.Order(StringComparer.Ordinal).ToDictionary(id => id, id => new HitCounts(
            found.Count(h => h.Id == id && h.Scope == "prod"), found.Count(h => h.Id == id && h.Scope == "test"), found.Count(h => h.Id == id && h.Scope == "generated")));
        Save(outPath, new HitsFile(counts, found, files.Select(SolutionOf).ToList()));
        Console.WriteLine($"hits: {files.Count} build logs: {string.Join(' ', files.Select(SolutionOf))}");
        foreach (var (id, count) in counts) Console.WriteLine($"hits: {id,-8} prod {count.Prod,6} test {count.Test,6}");
    }

    internal static List<string> RebuildSet(HitsFile hits, IReadOnlyCollection<string> ids) =>
        hits.Hits.Where(h => ids.Contains(h.Id) && h.Scope == "prod").SelectMany(h => h.Solutions ?? throw new BotExit(1, $"package: hit without solutions: {h.Id}")).Distinct().Order(StringComparer.Ordinal).ToList();

    private static void PackageCommand(string data, string? githubOutput)
    {
        var chosen = Load<CandidatesFile>(Path.Combine(data, "candidates.json")).Chosen ?? throw new BotExit(1, "package: no chosen candidate");
        var hits = Load<HitsFile>(Path.Combine(data, "hits.json"));
        var mine = hits.Hits.Where(x => chosen.Ids.Contains(x.Id) && x.Scope == "prod").ToList();
        WriteText(Path.Combine(data, "fix-hits.txt"), string.Concat(mine.Select(x => $"{x.Id}\t{x.File}:{x.Line}:{x.Col}\t{x.Msg}\n")));
        WriteText(Path.Combine(data, "package-ids.txt"), string.Join('\n', chosen.Ids) + "\n");
        var rebuild = RebuildSet(hits, chosen.Ids);
        Console.WriteLine($"package: {chosen.Slug} {mine.Count} prod hits to fix, rebuild {string.Join(' ', rebuild)}");
        if (!string.IsNullOrEmpty(githubOutput))
            File.AppendAllText(githubOutput, $"rebuild={string.Join(' ', rebuild)}\nids={string.Join(',', chosen.Ids)}\n", StrictUtf8);
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

    internal static bool EnablesRule(string patches, string ruleId) =>
        Lines(patches).Any(line => line == UnknownPatch || (EnabledRuleRow().Match(line) is { Success: true } row && row.Groups[1].Value == ruleId));

    internal static async Task<List<long>> OpenPrsFor(string ruleId, CancellationToken token)
    {
        var output = await Gh(["pr", "list", "-R", Repo, "--state", "open", "--search", $"{ruleId} in:title,body", "--json", "number", "--limit", "20"], token, check: false);
        var open = new List<long>();
        foreach (var number in ParseGitHub<List<NumberedPr>>(output.Length > 0 ? output : "[]").Select(x => x.Number).Order())
        {
            var (code, patches, _) = await Exec(["gh", "api", "-X", "GET", $"repos/{Repo}/pulls/{number}/files?per_page=100", "--paginate", "--jq",
                $".[] | select(.filename | endswith(\"{EditorConfig}\")) | .patch // \"{UnknownPatch}\""], false, token);
            if (code != 0 || EnablesRule(patches, ruleId)) open.Add(number);
        }
        return open;
    }

    internal static async Task Propose(string data, bool offline, string? githubOutput, CancellationToken token)
    {
        var gates = Load<Dictionary<string, Gate>>(Path.Combine(data, "gates.json"));
        var catalog = Load<Dictionary<string, CatalogEntry>>(Path.Combine(data, "catalog.json"));
        var routes = Routes();
        var plan = Load<PlanFile>(Path.Combine(data, "plan.json"));
        var counts = Load<HitsFile>(Path.Combine(data, "hits.json")).Counts;
        var routeB = LoadOr<RouteBState>(Path.Combine(data, "route_b.json"));
        long Prod(string id) => counts.GetValueOrDefault(id)?.Prod ?? 0;
        long Tests(string id) => counts.GetValueOrDefault(id)?.Test ?? 0;
        var decisions = new List<Decision>();
        var packages = new List<Package>();
        foreach (var (key, gate) in gates.OrderByDescending(item => item.Value.Prs))
        {
            if (!gate.Pass || !routes.TryGetValue(key, out var route) || route.Route != "A") continue;
            var accepted = new List<string>();
            foreach (var id in route.Ids)
            {
                var verdict = Verdict(Prod(id), catalog.GetValueOrDefault(id)?.Enforced == true);
                if (plan.RejectedIds?.TryGetValue(id, out var number) == true) verdict = $"skip: rule-bot PR #{number} closed without merge in the last {CooldownDays} days";
                decisions.Add(new(key, id, "A", Prod(id), Tests(id), verdict));
                if (verdict == "propose") accepted.Add(id);
            }
            foreach (var group in GroupByCap(accepted, accepted.ToDictionary(id => id, Prod)))
                packages.Add(new("A", key, group, group.Sum(Prod), group.Sum(Tests)));
        }
        if (!string.IsNullOrEmpty(plan.RouteB))
        {
            var id = plan.RouteBId!;
            var cluster = routeB.Cluster ?? plan.RouteB;
            var fails = new (string Name, bool? Ok)[] { ("analyzer tests", routeB.TestsOk), ("red control", routeB.RedOk), ("path guard", routeB.GuardOk), ("precision sample", routeB.Precision?.Ok) }
                .Where(check => check.Ok != true).Select(check => check.Name).ToList();
            var verdict = !string.IsNullOrEmpty(routeB.Analyzer) ? Verdict(Prod(id), false) : "propose";
            if (verdict == "propose" && fails.Count > 0)
                verdict = "skip: " + (!string.IsNullOrEmpty(routeB.Skip) ? routeB.Skip : string.Join(", ", fails) + " failed");
            decisions.Add(new(cluster, id, "B", Prod(id), Tests(id), verdict));
            if (verdict == "propose") packages.Add(new("B", cluster, [id], Prod(id), Tests(id), routeB.Analyzer));
        }
        for (var i = 0; i < packages.Count; i++)
        {
            var package = packages[i];
            var open = new SortedSet<long>();
            if (!offline && package.Route != "B")
                foreach (var id in package.Ids) open.UnionWith(await OpenPrsFor(id, token));
            packages[i] = package with { Slug = $"{package.Cluster}-{string.Join('-', package.Ids.Select(id => id.ToLowerInvariant()))}", OpenPrs = [.. open] };
        }
        var botOpen = offline ? [] : ParseGitHub<List<NumberedPr>>(await Gh(["pr", "list", "-R", Repo, "--state", "open", "--label", BotLabel, "--json", "number"], token));
        var chosen = packages.FirstOrDefault(p => p.OpenPrs!.Count == 0 && p.Route == "B") ?? packages.FirstOrDefault(p => p.OpenPrs!.Count == 0);
        if (botOpen.Count > 0) chosen = null;
        Save(Path.Combine(data, "candidates.json"), new CandidatesFile(decisions, packages, botOpen.Select(x => x.Number).ToList(), chosen));
        foreach (var d in decisions)
            Console.WriteLine($"propose: route {d.Route} {d.Cluster,-11} {d.Id,-8} prod {d.Prod,6} -> {d.Verdict}");
        foreach (var p in packages)
        {
            var duplicate = p.OpenPrs!.Count > 0 ? $" duplicate of open #{string.Join(", #", p.OpenPrs)}" : "";
            Console.WriteLine($"package: route {p.Route} {p.Slug} prod {p.Prod} test {p.Test}{duplicate}");
        }
        if (botOpen.Count > 0) Console.WriteLine($"package: none opened, bot PR already open: #{botOpen[0].Number}");
        Console.WriteLine($"package: chosen {chosen?.Slug ?? "none"}");
        if (!string.IsNullOrEmpty(githubOutput))
            File.AppendAllText(githubOutput, $"slug={chosen?.Slug ?? ""}\nroute={chosen?.Route ?? ""}\n", StrictUtf8);
    }

    internal static bool OverlayOnly(IEnumerable<string> diff, IReadOnlyCollection<string> ids)
    {
        string? section = null;
        foreach (var line in diff)
        {
            if (line.StartsWith('-')) return false;
            var text = (line.Length > 0 ? line[1..] : "").Trim(Blank);
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
        long oldLeft = 0, newLeft = 0;
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
            if (match.Success)
            {
                oldLeft = match.Groups[2].Success ? Number(match.Groups[2].Value) : 1;
                newLeft = match.Groups[4].Success ? Number(match.Groups[4].Value) : 1;
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
            foreach (var line in Lines(await Git(["diff", "--name-status", "--no-renames", baseRef], true, token)).Where(l => l.Length > 0))
            {
                var tab = line.IndexOf('\t');
                if (tab < 0) throw new BotExit(1, $"guard: unparsable git status line: {line}");
                changes.Add((line[..tab], line[(tab + 1)..]));
            }
            unshipped = Lines(await Git(["diff", "-U0", baseRef, "--", Unshipped], false, token));
            edits = Lines(await Git(["diff", "-U0", baseRef, "--", EditorConfig], false, token))
                .Where(l => TagIn(l, "+-") && !l.StartsWith("+++", StringComparison.Ordinal) && !l.StartsWith("---", StringComparison.Ordinal)).ToList();
            foreach (var line in Lines(await Git(["diff", "--summary", baseRef], true, token)))
                if (RiskyMode().IsMatch(line)) changes.Add(("X", line.Split(Blank, StringSplitOptions.RemoveEmptyEntries)[^1]));
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

    private static void SampleCommand(string hitsPath, string id, string repo, long seed, string outPath)
    {
        var hits = Load<HitsFile>(hitsPath).Hits.Where(x => x.Id == id && x.Scope == "prod").ToArray();
        new Random(seed.GetHashCode()).Shuffle(hits);
        var picked = hits.Take(SampleSize).ToList();
        foreach (var hit in picked)
        {
            var lines = Lines(ReadText(Path.Combine(repo, hit.File), replace: true));
            var low = (int)Math.Max(0, hit.Line - 8);
            var high = (int)Math.Min(lines.Count, hit.Line + 7);
            hit.Context = string.Join('\n', Enumerable.Range(low, Math.Max(0, high - low)).Select(n => $"{n + 1,5}{(n + 1 == hit.Line ? '>' : ' ')} {lines[n]}"));
        }
        Save(outPath, picked);
        Console.WriteLine($"sample: {picked.Count} of {hits.Length} prod hits of {id}, seed {seed}");
    }

    internal static (int TruePositive, int Need, bool Ok) Precision(IReadOnlyList<Label> labels, IReadOnlyList<Hit> sample, int hitCount)
    {
        var need = Math.Min(SampleSize, hitCount);
        var truePositive = labels.Count(l => l.LabelName == "true_positive" && l.MatchesWhy == true);
        var ok = need > 0 && sample.Count == need && SameHits(labels, sample) && (need == SampleSize ? truePositive >= SampleMinTrue : truePositive == need);
        return (truePositive, need, ok);
    }

    private static bool SameHits(IReadOnlyList<Label> labels, IReadOnlyList<Hit> sample) =>
        labels.Select(l => (l.File, l.Line)).Order().SequenceEqual(sample.Select(h => ((string?)h.File, (long?)h.Line)).Order());

    private static int PrecisionCommand(string labelsPath, string samplePath, string hitsPath, string id, string routeBPath)
    {
        var routeB = LoadOr<RouteBState>(routeBPath);
        if (routeB.Precision is not null)
        {
            routeB.Precision = null;
            Save(routeBPath, routeB);
        }
        var labels = LoadOr<List<Label>>(labelsPath);
        var count = Load<HitsFile>(hitsPath).Hits.Count(x => x.Id == id && x.Scope == "prod");
        var (truePositive, need, ok) = Precision(labels, LoadOr<List<Hit>>(samplePath), count);
        routeB.Precision = new(truePositive, labels.Count, need, ok);
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
        var count = failed.Success ? Number(failed.Groups[1].Value) : 0;
        var ok = result.Code != 0 && count > 0;
        if (!ok) Console.WriteLine(string.Join('\n', Lines(output).Where(l => TestSummaryLine().IsMatch(l)).Take(10)));
        Console.WriteLine($"red: tests with a no-op analyzer exit {result.Code}, failed {count} -> {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    internal static Dictionary<string, JsonElement>? LoadResult(string path)
    {
        try { return Load<Dictionary<string, JsonElement>>(path); }
        catch (Exception failure) when (failure is not OperationCanceledException) { return null; }
    }

    internal static string? Given(Dictionary<string, JsonElement>? result, string key) =>
        result is null || !result.TryGetValue(key, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.False ? null
        : (value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText()).TrimEnd('\n');

    private static string LastLine(string path, Func<string, bool> matches) =>
        LenientUtf8.GetString(File.ReadAllBytes(path)).Split('\n').LastOrDefault(matches) ?? "";

    private static async Task<int> Logged(string log, string[] argv, CancellationToken token) =>
        (await Exec(["bash", "-c", "log=$1; shift; exec \"$@\" > \"$log\" 2>&1", "route-b", log, .. argv], false, token)).Code;

    internal static string RouteBTools(string resultPath) =>
        $"Read,Glob,Grep,Edit(src/Nethermind/Nethermind.Analyzers/**),Edit(src/Nethermind/Nethermind.Analyzers.Test/**),Edit(/{resultPath}),Bash(dotnet build:*),Bash(dotnet test:*)";

    private static async Task<bool> RouteBAttempt(string data, string id, QueuedCluster entry, Dictionary<string, Gate> gates, RouteBState state, string statePath, CancellationToken token)
    {
        var cluster = entry.Cluster;
        var resultPath = Path.Combine(data, "analyzer-result.json");
        var examplesPath = Path.Combine(data, "route-b-examples.json");
        Save(examplesPath, gates.GetValueOrDefault(cluster)?.Examples ?? []);
        File.Delete(resultPath);
        var tools = RouteBTools(resultPath);
        var generation = await Logged(Path.Combine(data, $"analyzer-llm-{cluster}.log"),
            [Path.Combine(Home, "llm.sh"), Path.Combine(Home, "prompts", "analyzer.txt"), tools, $"ID={id}", $"CLUSTER={cluster}", $"WHY={entry.Why}", $"EXAMPLES={examplesPath}", $"RESULT={resultPath}"], token);
        Console.WriteLine($"route-b: {cluster} generation exit {generation}");

        var result = LoadResult(resultPath);
        var testClass = Given(result, "test_class") ?? "";
        var analyzer = Given(result, "analyzer") ?? "";
        const string AnalyzerPrefix = "src/Nethermind/Nethermind.Analyzers/";
        if (!TestClassName().IsMatch(testClass) || !analyzer.StartsWith(AnalyzerPrefix, StringComparison.Ordinal) || !analyzer.AsSpan(AnalyzerPrefix.Length).EndsWith(".cs", StringComparison.Ordinal) || !File.Exists(analyzer) || (await Exec(["test", "-f", analyzer], false, token)).Code != 0)
        {
            var skip = Given(result, "skip") ?? "no analyzer produced";
            Console.WriteLine($"route-b: {cluster} skipped: {skip}");
            state.Attempts!.Add(new(cluster, $"skipped by the generator: {skip}"));
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
        state.Attempts!.Add(new(cluster, $"guard {Flag(guardOk)}, tests {Flag(testsOk)}, red control {Flag(redOk)}"));
        (state.Cluster, state.Why, state.Analyzer, state.Summary, state.GuardOk, state.TestsOk, state.RedOk, state.TestsSummary, state.RedSummary) =
            (cluster, entry.Why, analyzer, Given(result, "summary") ?? "", guardOk, testsOk, redOk, summary, redSummary);
        Save(statePath, state);
        return guardOk && testsOk && redOk;
    }

    internal static async Task RouteB(string data, CancellationToken token)
    {
        var plan = Load<PlanFile>(Path.Combine(data, "plan.json"));
        var gates = Load<Dictionary<string, Gate>>(Path.Combine(data, "gates.json"));
        var queue = plan.RouteBQueue ?? throw new BotExit(1, "route-b: plan has no queue");
        var id = plan.RouteBId ?? "null";
        var statePath = Path.Combine(data, "route_b.json");
        var state = new RouteBState { Id = id, Cluster = queue.FirstOrDefault()?.Cluster ?? "null", Attempts = [] };
        Save(statePath, state);
        foreach (var entry in queue)
        {
            if (await RouteBAttempt(data, id, entry, gates, state, statePath, token))
            {
                await Git(["diff", $"--output={Path.Combine(data, "analyzer.patch")}", "HEAD"], false, token);
                return;
            }
            await Git(["diff", $"--output={Path.Combine(data, $"analyzer-{entry.Cluster}.patch")}", "HEAD"], false, token);
            await Git(["reset", "-q", "--hard", "HEAD"], false, token);
            await Git(["clean", "-qfd", "-e", "/rule-bot"], false, token);
        }
        state.Skip = string.Join("; ", state.Attempts!.Select(a => $"{a.Cluster}: {a.Result}"));
        Save(statePath, state);
        Console.WriteLine("route-b: no cluster produced an analyzer that passed the gates");
    }

    private static void Body(string data, string? headRef, string outPath)
    {
        var chosen = Load<CandidatesFile>(Path.Combine(data, "candidates.json")).Chosen ?? throw new BotExit(1, "body: no chosen candidate");
        var gate = Load<Dictionary<string, Gate>>(Path.Combine(data, "gates.json"))[chosen.Cluster];
        if (gate.Validity is not (>= 0 and <= 1)) throw new BotExit(1, "body: validity out of range");
        var routeB = LoadOr<RouteBState>(Path.Combine(data, "route_b.json"));
        var solutions = File.Exists(Path.Combine(data, "hits.json")) ? Load<HitsFile>(Path.Combine(data, "hits.json")).Solutions ?? [] : [];
        var head = string.IsNullOrEmpty(headRef) ? "master" : headRef;
        var ids = chosen.Ids;
        var idText = string.Join(", ", ids.Select(i => $"`{i}`"));
        var lines = new List<string>();
        if (chosen.Route == "A") lines.AddRange([$"Enables {idText} as a build warning for production code and fixes the existing hits.", ""]);
        else lines.AddRange([$"Adds analyzer `{ids[0]}` ({Routes().GetValueOrDefault(chosen.Cluster)?.Why ?? chosen.Cluster}) and fixes the existing hits. No code fix provider is shipped.", ""]);
        var validity = (long)Math.Round(gate.Validity * 100, MidpointRounding.ToEven);
        lines.AddRange(["## Why", "", $"Reviewers keep asking for this by hand: in the last {WindowDays} days {gate.N} review comments on {gate.Prs} PRs from {gate.Commenters} people ({gate.Human} by humans) match this pattern; {validity}% were accepted as valid. Examples:", ""]);
        lines.AddRange((gate.Examples ?? []).Take(3).Select(e => e.Url is null || e.Author is null ? throw new BotExit(1, "body: example without url or author") : $"- {e.Url} ({e.Author})"));
        lines.AddRange(["", "## Hits", "", $"Full Release build of every CI solution on `{head}` with the rule at warning ({string.Join(", ", solutions.Select(s => $"`{s}`"))}):", ""]);
        lines.Add($"- Production code: {chosen.Prod}, all fixed in this PR.");
        lines.Add(chosen.Test != 0 ? $"- Tests and benchmarks: {chosen.Test}, excluded through `.editorconfig` sections." : "- Tests and benchmarks: 0.");
        if (chosen.Route == "B")
        {
            lines.AddRange(["", "## Analyzer checks", "",
                $"- Tests: {routeB.TestsSummary ?? "pass"}. With `Initialize` replaced by a no-op the tests fail ({routeB.RedSummary ?? "red control"}).",
                $"- Precision: {Show(routeB.Precision?.TruePositive)}/{Show(routeB.Precision?.Needed)} random production hits labelled true positive for the stated reason by a separate review pass (gate {SampleMinTrue}/{SampleSize}).",
                $"- `{Unshipped}` gets one appended row, so a `*.md` code owner has to approve.", "",
                "<details><summary>Precision sample</summary>", ""]);
            foreach (var label in LoadOr<List<Label>>(Path.Combine(data, "labels.json")))
                lines.Add($"- `{Show(label.File)}:{Show(label.Line)}` {Show(label.LabelName)}: {label.Reason ?? ""}");
            lines.AddRange(["", "</details>"]);
        }
        lines.AddRange(["", "## Revert", "", "Revert this PR, or set the rule to `none` in `.editorconfig`:", "", "```", string.Join('\n', ids.Select(i => $"dotnet_diagnostic.{i}.severity = none")), "```", "",
            $"Opened by the review rule bot (`.github/workflows/rule-bot.yml`). Label `{BotLabel}`; the bot keeps at most one such PR open."]);
        WriteText(outPath, string.Join('\n', lines) + "\n");
        Console.WriteLine($"body: {outPath}");
    }

    internal static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        TemplateKey().Replace(template, match => values.TryGetValue(match.Groups[1].Value, out var value) ? value : match.Value);

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
        ["precision"] = new(["labels", "sample", "hits", "id", "route-b"], [], []),
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
        return T.TryParse(text, CultureInfo.InvariantCulture, out var value) ? value : throw new BotExit(2, $"rule-bot {command}: argument --{name}: invalid int value: '{text}'");
    }

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
                case "sample": SampleCommand(values["hits"], values["id"], repo, IntOption(values, "seed", args[0], 0L), values["out"]); return 0;
                case "precision": return PrecisionCommand(values["labels"], values["sample"], values["hits"], values["id"], values["route-b"]);
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
