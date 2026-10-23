// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal sealed class SequentialDriver(Func<string, string?> environment)
{
    internal const int Limit = 4096;
    internal const int MaxDiagnosticLines = 20;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex Ansi = new(@"\x1b\[[0-?]*[ -/]*[@-~]");
    private static readonly Regex Sse = new(@"\[payload-server\]\s+client_metric\s+block_number=(\d+)\s+processing_ms=(\d+(?:\.\d+)?)");
    private static readonly Regex K6 = new(@"^\s*\|\s*(\d+)\s*\|\s*(\d+)\s*\|\s*(\d+(?:\.\d+)?)\s*\|\s*$");
    private static readonly Regex WarmOk = new(@"\[payload-server\]\s+warmup\s+block=(\d+)\s+ok\b", RegexOptions.IgnoreCase);
    private static readonly Regex WarmBad = new(@"\[payload-server\]\s+warmup\s+block=(\d+)\s+FAILED\b", RegexOptions.IgnoreCase);
    private static readonly Regex Severe = new(@"\b(?:Unhandled|Fatal|ERROR)\b", RegexOptions.IgnoreCase);
    private static readonly (string Key, string Environment)[] CpuOverrides = [("cpu", "DOCKER_CPU"), ("cpuset", "DOCKER_CPUSET"), ("infra_cpuset", "DOCKER_INFRA_CPUSET")];
    internal string Get(string name, string fallback = "") => environment(name) ?? fallback;
    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ", CultureInfo.InvariantCulture);
    private static string Architecture => RuntimeInformation.OSArchitecture switch { System.Runtime.InteropServices.Architecture.X64 => "x86_64", System.Runtime.InteropServices.Architecture.Arm64 => "aarch64", _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant() };
    internal static string Text(JsonNode? value, string fallback = "") => value?.ToString() ?? fallback;
    private static JsonArray Array(IEnumerable<string> values) => [.. values.Select(value => (JsonNode?)JsonValue.Create(value))];
    private static JsonArray Array(IEnumerable<long> values) => [.. values.Select(value => (JsonNode?)JsonValue.Create(value))];
    private static void Save(string path, JsonNode value) => File.WriteAllText(path, value.ToJsonString(JsonOptions) + "\n");

    internal static List<string> ParseFlags(string value)
    {
        var result = new List<string>();
        foreach (var item in Regex.Split(value, "[\\r\\n,]").Select(item => item.Trim()).Where(item => item.Length > 0))
        {
            if (!item.StartsWith("--", StringComparison.Ordinal) && result.Count > 0 && result[^1].StartsWith("--", StringComparison.Ordinal))
                result[^1] += $"={item}";
            else
            {
                var parts = Regex.Split(item, @"\s+", RegexOptions.None, TimeSpan.FromSeconds(1));
                var boundary = Regex.Match(item, @"\s+");
                result.Add(parts.Length == 1 || parts[0].Contains('=') ? item : $"{parts[0]}={item[(boundary.Index + boundary.Length)..]}");
            }
        }
        return result;
    }

    internal static Dictionary<string, string> ParsePairs(string value)
    {
        var result = new Dictionary<string, string>();
        foreach (var raw in Regex.Split(value, "[\\r\\n,]"))
        {
            var item = raw.Trim();
            if (item.Length == 0) continue;
            var split = item.IndexOf('=');
            if (split < 0 || !Regex.IsMatch(item[..split], @"\A[A-Za-z_][A-Za-z0-9_]*\z"))
                throw new ArgumentException($"environment entry must use a valid KEY=VALUE pair: '{item}'");
            result[item[..split]] = item[(split + 1)..];
        }
        return result;
    }

    internal static long? ParseAmount(string value)
    {
        if (value.Length == 0) return null;
        if (!Regex.IsMatch(value, @"\A[1-9][0-9]*\z") || !long.TryParse(value, CultureInfo.InvariantCulture, out var amount))
            throw new ArgumentException($"AMOUNT must be a positive integer, got '{value}'");
        return amount;
    }

    internal static List<int> ExpandCpus(string value)
    {
        var result = new List<int>();
        foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = part.Split('-', 2);
            var first = int.Parse(parts[0], CultureInfo.InvariantCulture);
            var last = int.Parse(parts.Length == 1 || parts[1].Length == 0 ? parts[0] : parts[1], CultureInfo.InvariantCulture);
            for (var cpu = first; cpu <= last; cpu++) result.Add(cpu);
        }
        return result;
    }

    internal bool CpuOverridesActive() => CpuOverrides.Any(pair => Get(pair.Environment).Trim().Length > 0);

    internal void ApplyCpuOverrides(JsonObject resources)
    {
        foreach (var (key, variable) in CpuOverrides)
        {
            var value = Get(variable).Trim().ToLowerInvariant();
            if (value.Length == 0) continue;
            if (key == "cpu")
            {
                if (value != "all" && !Regex.IsMatch(value, @"\A[0-9]+\z"))
                    throw new ArgumentException($"{variable} must be a whole number of CPUs or 'all', got '{value}'");
                resources[key] = value == "all" ? 0 : int.Parse(value, CultureInfo.InvariantCulture);
            }
            else if (value == "all") resources.Remove(key);
            else if (Regex.IsMatch(value, @"\A[0-9]+(?:-[0-9]+)?(?:,[0-9]+(?:-[0-9]+)?)*\z")) resources[key] = value;
            else throw new ArgumentException($"{variable} must be a Docker cpuset list or 'all', got '{value}'");
        }
    }

    internal void PinClientCpus(JsonObject resources)
    {
        var countNode = resources["cpu"];
        var count = 0;
        if (countNode is not null && (countNode is not JsonValue value || !value.TryGetValue<int>(out count) || count < 0))
            throw new ArgumentException($"resources.cpu must be a whole number of CPUs, got {countNode}");
        if (count != 0)
        {
            var cpuset = Text(resources["cpuset"]);
            if (cpuset.Length == 0) throw new ArgumentException($"resources.cpu={count} needs a resources.cpuset to pin the client to");
            var allowed = ExpandCpus(cpuset);
            if (count < allowed.Count)
            {
                var taken = new HashSet<int>();
                foreach (var cpu in allowed)
                {
                    if (taken.Contains(cpu)) continue;
                    var core = ExpandCpus(File.ReadAllText(Path.Combine(Get("CPU_TOPOLOGY_DIR", "/sys/devices/system/cpu"), $"cpu{cpu}", "topology", "thread_siblings_list")).Trim())
                        .Where(allowed.Contains).ToArray();
                    if (taken.Count + core.Length <= count) taken.UnionWith(core);
                }
                if (taken.Count != count) throw new ArgumentException($"cannot pick {count} CPUs as whole cores from cpuset {cpuset}");
                resources["cpuset"] = string.Join(',', taken.Order());
            }
        }
        // Zero disables the quota; omission restores EXPB's default quota.
        resources["cpu"] = 0;
    }

    internal (JsonObject Config, string Name) Render(JsonObject original, JsonObject image, int run)
    {
        var config = original.DeepClone().AsObject();
        var amount = ParseAmount(Get("AMOUNT"));
        foreach (var (oldValue, newValue) in new[] { ("<<DELAY>>", Get("DELAY_SECONDS", "0")), ("<<AMOUNT>>", Get("AMOUNT")), ("/mnt/sda/expb-data", Get("EXPB_DATA_DIR")), ("/mnt/sda/nethermind-flat-snapshot", Get("FLAT_SNAPSHOT_DIR")), ("/mnt/sda/nethermind-flat-25490000", Get("FLAT_SNAPSHOT_BLOCK_DIR")) })
            ReplaceStrings(config, oldValue, newValue);
        var resources = (config["resources"] ??= new JsonObject()).AsObject();
        ApplyCpuOverrides(resources);
        PinClientCpus(resources);
        if (config["scenarios"] is not JsonObject scenarios || scenarios["nethermind"] is not JsonObject template)
            throw new ArgumentException("config has no scenarios.nethermind mapping");
        var client = Get("CLIENT", "nethermind");
        if (client is not ("nethermind" or "reth" or "geth")) throw new ArgumentException($"CLIENT must be one of nethermind, reth, geth, got '{client}'");
        var name = $"{client}-{image["id"]}-run{run}";
        var scenario = template.DeepClone().AsObject();
        config["scenarios"] = new JsonObject { [name] = scenario };
        scenario["client"] = client;
        scenario["image"] = image["image"]?.DeepClone();
        if (amount is not null) scenario["amount"] = amount.Value;
        if (client != "nethermind")
        {
            if (Get("MEASUREMENT_MODE", "standard") == "compute-warm" || Get("TRACE_BLOCKS").Length > 0 || ParsePairs(Get("CLIENT_ENV")).Count > 0)
                throw new ArgumentException($"compute-warm, TRACE_BLOCKS and CLIENT_ENV are Nethermind-only and cannot be used with client={client}");
            if (Get("CLIENT_SNAPSHOT_DIR").Length == 0 || Get("SNAPSHOT_MOUNT_PATH").Length == 0)
                throw new ArgumentException($"client={client} needs CLIENT_SNAPSHOT_DIR and SNAPSHOT_MOUNT_PATH");
            scenario["snapshot_source"] = Get("CLIENT_SNAPSHOT_DIR");
            scenario["snapshot_backend"] = "overlay";
            scenario["extra_flags"] = new JsonArray();
            scenario["extra_env"] = new JsonObject();
            scenario["extra_commands"] = new JsonArray();
            scenario["extra_volumes"] = new JsonObject();
            scenario["snapshot_mount_path"] = Get("SNAPSHOT_MOUNT_PATH");
            scenario["startup_wait"] = 600;
        }
        var extra = ParseFlags(Get("ADDITIONAL_EXTRA_FLAGS"));
        if (Get("MEASUREMENT_MODE", "standard") == "compute-warm")
        {
            if (extra.Any(item => item.Contains("JsonRpc.GasCap", StringComparison.Ordinal))) throw new ArgumentException("compute-warm conflicts with GasCap override");
            if (ParsePairs(Get("EXPB_ENV_PASSTHROUGH")).ContainsKey("EXPB_EVM_WARMUP")) throw new ArgumentException("compute-warm conflicts with EVM warmup override");
            extra.Add("--JsonRpc.GasCap=1000000000000");
        }
        var flags = (scenario["extra_flags"] ??= new JsonArray()).AsArray();
        foreach (var flag in extra) flags.Add((JsonNode?)JsonValue.Create(flag));
        var extraEnvironment = (scenario["extra_env"] ??= new JsonObject()).AsObject();
        foreach (var (key, value) in ParsePairs(Get("CLIENT_ENV"))) extraEnvironment[key] = value;
        if (Get("TRACE_BLOCKS").Length > 0) extraEnvironment["NETHERMIND_PROFILE_BLOCKS"] = Get("TRACE_BLOCKS");
        return (config, name);
    }

    private static void ReplaceStrings(JsonNode node, string oldValue, string newValue)
    {
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj.ToArray())
            {
                if (value is JsonValue literal && literal.TryGetValue<string>(out var text)) obj[key] = text.Replace(oldValue, newValue, StringComparison.Ordinal);
                else if (value is not null) ReplaceStrings(value, oldValue, newValue);
                if (key.Contains(oldValue, StringComparison.Ordinal))
                {
                    var replacement = obj[key];
                    obj.Remove(key);
                    obj[key.Replace(oldValue, newValue, StringComparison.Ordinal)] = replacement;
                }
            }
        }
        else if (node is JsonArray array)
            for (var index = 0; index < array.Count; index++)
                if (array[index] is JsonValue value && value.TryGetValue<string>(out var text)) array[index] = text.Replace(oldValue, newValue, StringComparison.Ordinal);
                else if (array[index] is { } child) ReplaceStrings(child, oldValue, newValue);
    }

    internal static string Bounded(string line)
    {
        var trimmed = line.TrimEnd('\r', '\n');
        return trimmed.Length <= Limit ? trimmed : trimmed[..Limit] + " ... [truncated in console; full line retained in combined log]";
    }

    internal static JsonObject MetricStats(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        double? Percentile(int rank) => ordered.Length == 0 ? null : ordered[Math.Max(0, (int)Math.Ceiling(rank * ordered.Length / 100d) - 1)];
        return new JsonObject
        {
            ["count"] = ordered.Length, ["avg"] = ordered.Length == 0 ? null : ordered.Average(),
            ["median"] = ordered.Length == 0 ? null : (ordered[(ordered.Length - 1) / 2] + ordered[ordered.Length / 2]) / 2,
            ["p90"] = Percentile(90), ["p95"] = Percentile(95), ["p99"] = Percentile(99),
            ["min"] = ordered.Length == 0 ? null : ordered[0], ["max"] = ordered.Length == 0 ? null : ordered[^1]
        };
    }

    internal static (JsonObject Metrics, Dictionary<string, List<string>> Diagnostics) CollectMetrics(string path, bool useSse = true)
    {
        var sse = new List<(long Block, double Time)>();
        var rows = new List<(long Index, long Gas, double Time)>();
        var diagnostics = new Dictionary<string, List<string>> { ["exceptions"] = [], ["invalid"] = [], ["severe"] = [] };
        var counts = new Dictionary<string, int> { ["exceptions"] = 0, ["invalid"] = 0, ["severe"] = 0 };
        var warmOk = new HashSet<long>();
        var warmBad = new HashSet<long>();
        var shutdown = false;
        var cleanup = false;
        foreach (var raw in File.ReadLines(path))
        {
            var line = Ansi.Replace(raw, "");
            var metric = Sse.Match(line);
            if (useSse && metric.Success) sse.Add((Number(metric, 1), Timing(metric, 2)));
            var row = K6.Match(line);
            if (row.Success) rows.Add((Number(row, 1), Number(row, 2), Timing(row, 3)));
            var ok = WarmOk.Match(line);
            if (ok.Success) warmOk.Add(Number(ok, 1));
            var bad = WarmBad.Match(line);
            if (bad.Success) warmBad.Add(Number(bad, 1));
            foreach (var key in new[] { "exceptions", "invalid", "severe" })
            {
                var matches = key switch
                {
                    "exceptions" => line.Contains("Exception", StringComparison.Ordinal),
                    "invalid" => Regex.IsMatch(line, @"invalid[\s_-]*blocks?", RegexOptions.IgnoreCase),
                    _ => Severe.IsMatch(line)
                };
                if (!matches) continue;
                counts[key]++;
                if (diagnostics[key].Count < MaxDiagnosticLines) diagnostics[key].Add(Bounded(line));
            }
            shutdown |= line.Contains("Nethermind is shut down", StringComparison.Ordinal);
            cleanup |= line.Contains("Cleanup completed", StringComparison.Ordinal);
        }
        var processingValues = sse.Count > 0 ? sse.Select(value => value.Time).ToArray() : rows.Select(value => value.Time).ToArray();
        // The feed logs N when N+1 is fetched, so only a missing final record can be paired by position.
        var paired = sse.Count > 0 && (sse.Count == rows.Count || sse.Count == rows.Count - 1) ? sse.Count : 0;
        var primary = MetricStats(processingValues);
        var processingTotal = sse.Sum(value => value.Time);
        var requestTotal = rows.Sum(value => value.Time);
        var metrics = new JsonObject
        {
            ["source"] = processingValues.Length == 0 ? "none" : sse.Count > 0 ? "SSE" : "TTFB",
            ["count"] = processingValues.Length, ["avg"] = primary["avg"]?.DeepClone(),
            ["ids"] = Array(sse.Count > 0 ? sse.Select(value => value.Block) : rows.Select(value => value.Index)),
            ["payload_indices"] = Array(rows.Select(value => value.Index)), ["delivered"] = rows.Count, ["sse_count"] = sse.Count,
            ["primary"] = primary, ["processing"] = MetricStats(sse.Select(value => value.Time)),
            ["request"] = MetricStats(rows.Select(value => value.Time)),
            ["outside"] = MetricStats(Enumerable.Range(0, paired).Select(index => rows[index].Time - sse[index].Time)),
            ["mgas_s"] = paired > 0 && processingTotal > 0 ? rows.Take(paired).Sum(value => (double)value.Gas) / 1_000_000 / (processingTotal / 1_000) : null,
            ["request_mgas_s"] = requestTotal > 0 ? rows.Sum(value => (double)value.Gas) / 1_000_000 / (requestTotal / 1_000) : null,
            ["warm_ok"] = Array(warmOk.Order()), ["warm_bad"] = Array(warmBad.Order()),
            ["shutdown"] = shutdown, ["cleanup"] = cleanup,
            ["exception_count"] = counts["exceptions"], ["invalid_count"] = counts["invalid"], ["severe_count"] = counts["severe"]
        };
        return (metrics, diagnostics);
    }

    private static long Number(Match match, int group) => long.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);
    private static double Timing(Match match, int group) => double.Parse(match.Groups[group].Value, CultureInfo.InvariantCulture);

    internal static async Task<string> Capture(string command, string[] arguments, CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo(command) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true } };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
            var text = await stdout;
            var errors = await stderr;
            if (process.ExitCode != 0) throw new IOException($"{command} exited with code {process.ExitCode}: {errors}");
            return text;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }
    }

    internal async Task VerifyClean(JsonObject config, CancellationToken cancellationToken)
    {
        var root = ResolvePath(Path.GetFullPath(Get("EXPB_DATA_DIR", ".")));
        var docker = Get("DOCKER_BIN", "docker");
        await Capture(docker, ["container", "prune", "--force", "--filter", "label=expb"], cancellationToken);
        foreach (var (label, arguments) in new[] { ("containers", new[] { "container", "ps", "-q" }), ("networks", new[] { "network", "ls", "-q" }) })
        {
            var remaining = (await Capture(docker, [.. arguments, "--filter", "label=expb"], cancellationToken)).Trim();
            if (remaining.Length > 0) throw new IOException($"benchmark {label} remain: {remaining}");
        }
        VerifyScratch(config, root, File.ReadLines("/proc/self/mounts"));
    }

    internal void VerifyScratch(JsonObject config, string root, IEnumerable<string> mounts)
    {
        root = ResolvePath(root);
        foreach (var line in mounts)
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3 && parts[2] == "overlay")
            {
                var mount = ResolvePath(parts[1].Replace(@"\040", " ", StringComparison.Ordinal));
                if (Within(root, mount)) throw new IOException($"overlay remains below data directory: {mount}");
            }
        }
        var node = (config["paths"] as JsonObject)?["work"];
        var work = node is null ? "work" : node is JsonValue value && value.TryGetValue<string>(out var text) ? text : throw new ArgumentException("paths.work is invalid");
        if (work.Length == 0) throw new ArgumentException("paths.work is invalid");
        work = work.Replace("/mnt/sda/expb-data", Get("EXPB_DATA_DIR"), StringComparison.Ordinal);
        if (work == "~" || work.StartsWith("~/", StringComparison.Ordinal)) work = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), work.Length > 1 ? work[2..] : "");
        var scratch = ResolvePath(Path.GetFullPath(work, root));
        if (!Within(root, scratch)) throw new ArgumentException($"paths.work escapes data directory: {scratch}");
        foreach (var leaf in new[] { "work", "upper", "merged" })
        {
            var path = Path.Combine(scratch, leaf);
            if (new FileInfo(path).LinkTarget is not null) throw new IOException($"scratch is a symlink: {path}");
            if (File.Exists(path) || (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())) throw new IOException($"scratch is not empty: {path}");
        }
    }

    private static bool Within(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string ResolvePath(string path)
    {
        var absolute = Path.GetFullPath(path);
        var resolved = Path.GetPathRoot(absolute)!;
        foreach (var part in absolute[resolved.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, part);
            var info = new DirectoryInfo(resolved);
            if (info.LinkTarget is not null) resolved = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolved;
        }
        return resolved;
    }

    internal async Task ReadCgroupLimits(List<string> report, CancellationToken cancellationToken)
    {
        async Task<string> Docker(string[] arguments)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try { return (await Capture(Get("DOCKER_BIN", "docker"), arguments, timeout.Token)).Trim(); }
            catch (Exception exception) when (exception is IOException or System.ComponentModel.Win32Exception or OperationCanceledException) { return ""; }
        }
        try
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var names = await Docker(["ps", "--filter", "label=expb", "--format", "{{.Names}}"]);
                if (names.Split('\n', StringSplitOptions.RemoveEmptyEntries).Any(name => !Regex.IsMatch(name, @"-(?:k6|alloy|payload-server)$"))) break;
            }
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            foreach (var container in (await Docker(["ps", "-q", "--no-trunc", "--filter", "label=expb"])).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var limits = (await Docker(["inspect", "-f", "{{.Name}} NanoCpus={{.HostConfig.NanoCpus}} CpusetCpus={{.HostConfig.CpusetCpus}}", container])).TrimStart('/');
                var cgroup = "cgroup dir not found";
                foreach (var directory in new[] { $"/sys/fs/cgroup/system.slice/docker-{container}.scope", $"/sys/fs/cgroup/docker/{container}" })
                {
                    if (!Directory.Exists(directory)) continue;
                    string Read(string leaf)
                    {
                        try { return File.ReadAllText(Path.Combine(directory, leaf)).Trim(); }
                        catch (IOException) { return ""; }
                        catch (UnauthorizedAccessException) { return ""; }
                    }
                    cgroup = $"cpu.max='{Read("cpu.max")}' cpuset.cpus.effective='{Read("cpuset.cpus.effective")}'";
                    break;
                }
                report.Add($"{(limits.Length == 0 ? container : limits)} {cgroup}");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    internal async Task<int> ExecuteLogged(string[] command, string directory, Dictionary<string, string> childEnvironment, string logPath, CancellationToken cancellationToken)
    {
        var linux = OperatingSystem.IsLinux();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(linux ? "setsid" : command[0])
            {
                WorkingDirectory = directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
            }
        };
        foreach (var argument in linux ? command : command.Skip(1)) process.StartInfo.ArgumentList.Add(argument);
        foreach (var (key, value) in childEnvironment) process.StartInfo.Environment[key] = value;
        using var file = new StreamWriter(logPath, append: false);
        var writer = TextWriter.Synchronized(file);
        process.Start();
        var stdout = Drain(process.StandardOutput, writer, CancellationToken.None);
        var stderr = Drain(process.StandardError, writer, CancellationToken.None);
        try
        {
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                if (!process.HasExited && linux)
                {
                    try { await Capture("/bin/kill", ["-TERM", "--", $"-{process.Id}"], CancellationToken.None); }
                    catch (IOException) when (process.HasExited) { }
                    var grace = int.Parse(Get("CLEANUP_GRACE_SECONDS", "90"), CultureInfo.InvariantCulture);
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(grace));
                    try { await process.WaitForExitAsync(cleanup.Token); }
                    catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                    {
                        try { await Capture("/bin/kill", ["-KILL", "--", $"-{process.Id}"], CancellationToken.None); }
                        catch (IOException) when (process.HasExited) { }
                    }
                }
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            await Task.WhenAll(stdout, stderr);
            return process.ExitCode;
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            await Task.WhenAll(stdout, stderr);
        }
    }

    private static async Task Drain(StreamReader reader, TextWriter writer, CancellationToken cancellationToken)
    {
        while (await reader.ReadLineAsync(cancellationToken) is { } line) await writer.WriteLineAsync(line);
    }

    internal async Task<JsonObject> RunSample(JsonObject original, JsonObject image, int run, string root, CancellationToken cancellationToken,
        Func<JsonObject, CancellationToken, Task>? verify = null)
    {
        var sampleId = $"{image["id"]}-run{run}";
        var directory = Path.Combine(root, sampleId);
        Directory.CreateDirectory(directory);
        var started = Now();
        var logPath = Path.Combine(directory, $"combined-{started.Replace(":", "").Replace("-", "")}.log");
        var configPath = Path.Combine(directory, "config.json");
        var result = new JsonObject
        {
            ["sample_id"] = sampleId, ["image_id"] = image["id"]?.DeepClone(), ["image"] = image["image"]?.DeepClone(), ["run"] = run,
            ["started_at"] = started, ["architecture"] = Architecture, ["runner_hostname"] = Environment.MachineName,
            ["log"] = logPath, ["config"] = configPath, ["expb_source"] = Get("EXPB_SOURCE", "unknown"), ["expb_env"] = Get("EXPB_ENV_PASSTHROUGH"),
            ["measurement_mode"] = Get("MEASUREMENT_MODE", "standard"), ["client"] = Get("CLIENT", "nethermind"),
            ["measurement_source"] = Get("MEASUREMENT_SOURCE", "auto"), ["status"] = "failed"
        };
        var config = original;
        var code = 125;
        try
        {
            (config, var scenario) = Render(original, image, run);
            // Export credentials belong only in the private runtime copy, never in uploaded artifacts.
            var artifactConfig = config.DeepClone().AsObject();
            artifactConfig.Remove("export");
            Save(configPath, artifactConfig);
            var temporary = Path.Combine(Get("RUNNER_TEMP") is { Length: > 0 } runnerTemp ? runnerTemp : Path.GetTempPath(), $".expb-runtime-{Guid.NewGuid():N}");
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(temporary);
            else Directory.CreateDirectory(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            try
            {
                var runtimeConfig = Path.Combine(temporary, "config.json");
                Save(runtimeConfig, config);
                result["expected_amount"] = ParseAmount(Get("AMOUNT")) ?? long.Parse(Text(config["scenarios"]![scenario]!["amount"], "0"), CultureInfo.InvariantCulture);
                var command = new List<string> { Get("EXPB_BIN", "expb"), "execute-scenarios", "--config-file", runtimeConfig, "--per-payload-metrics", "--per-payload-metrics-logs", "--print-logs" };
                if (Get("DOTTRACE", "false") == "true") command.AddRange(["--dottrace", "--dottrace-mode", Get("DOTTRACE_MODE", "sampling"), "--dotnet-trace"]);
                if (Get("PERF", "false") == "true") command.Add("--perf");
                if (Get("MEASUREMENT_SOURCE", "auto") == "engine-api") command.Add("--no-client-metrics");
                result["command"] = Array(command);
                var childEnvironment = ParsePairs(Get("EXPB_ENV_PASSTHROUGH"));
                if (Get("MEASUREMENT_MODE", "standard") == "compute-warm") childEnvironment["EXPB_EVM_WARMUP"] = "1";
                using var readoutDone = new CancellationTokenSource();
                var readout = new List<string>();
                var reader = CpuOverridesActive() ? ReadCgroupLimits(readout, readoutDone.Token) : null;
                try { code = await ExecuteLogged(command.ToArray(), Get("EXPB_DATA_DIR"), childEnvironment, logPath, cancellationToken); }
                finally
                {
                    readoutDone.Cancel();
                    if (reader is not null)
                    {
                        await reader;
                        result["cgroup_limits"] = Array(readout);
                        Console.WriteLine($"{sampleId}: container CPU limits as enforced by the host cgroup:");
                        foreach (var line in readout.Count == 0 ? ["(no readout captured)"] : readout) Console.WriteLine($"  {line}");
                    }
                }
            }
            finally { Directory.Delete(temporary, recursive: true); }
        }
        catch (Exception exception)
        {
            code = 125;
            result["error"] = exception.Message;
            File.AppendAllText(logPath, $"driver error: {exception.Message}\n");
        }
        var (metrics, diagnostics) = CollectMetrics(logPath, Get("MEASUREMENT_SOURCE", "auto") != "engine-api");
        var cleanError = "";
        try { await (verify ?? VerifyClean)(config, CancellationToken.None); }
        catch (Exception exception) { cleanError = exception.Message; }
        var reasons = FailureReasons(metrics, result["expected_amount"]?.GetValue<long>(), code, cleanError, cancellationToken.IsCancellationRequested);
        result["finished_at"] = Now();
        result["exit_code"] = code;
        result["metrics"] = metrics;
        result["sse_block_ids"] = Text(metrics["source"]) == "SSE" ? metrics["ids"]!.DeepClone() : new JsonArray();
        result["cleanup_verified"] = cleanError.Length == 0;
        result["cleanup_problems"] = Array(cleanError.Length == 0 ? [] : [cleanError]);
        foreach (var key in new[] { "exception_count", "invalid_count", "severe_count" }) result[key] = metrics[key]?.DeepClone();
        result["failure_reasons"] = Array(reasons);
        result["status"] = reasons.Count == 0 ? "success" : "failed";
        Save(Path.Combine(directory, "metadata.json"), result);
        Console.WriteLine($"{sampleId}: {result["status"]} AVG={metrics["avg"]}");
        if (metrics["severe_count"]!.GetValue<int>() > 0) Console.WriteLine($"::warning::severe runtime signal in {sampleId}; see combined log");
        foreach (var line in diagnostics.Values.SelectMany(lines => lines).Take(20)) Console.Error.WriteLine(line);
        return result;
    }

    internal List<string> FailureReasons(JsonObject metrics, long? expected, int code, string cleanError, bool cancelled)
    {
        var delivered = metrics["delivered"]!.GetValue<int>();
        var indices = metrics["payload_indices"]!.AsArray().Select(value => value!.GetValue<long>()).ToArray();
        var ids = metrics["ids"]!.AsArray().Select(value => value!.GetValue<long>()).ToArray();
        var sseCount = metrics["sse_count"]!.GetValue<int>();
        var reasons = new List<string>();
        if (code != 0) reasons.Add($"execution exited with code {code}");
        if (expected is null || delivered != expected || indices.Distinct().Count() != delivered) reasons.Add($"delivery count/IDs expected {expected}, got {delivered}");
        if (Text(metrics["source"]) == "SSE" && (delivered < 1 || (sseCount != delivered && sseCount != delivered - 1) || ids.Distinct().Count() != sseCount)) reasons.Add($"SSE coverage/IDs are {sseCount} for {delivered} delivered");
        if (metrics["avg"] is null) reasons.Add("processing metrics are missing");
        if (Get("CLIENT", "nethermind") == "nethermind" && !metrics["shutdown"]!.GetValue<bool>()) reasons.Add("normal shutdown marker is missing");
        if (!metrics["cleanup"]!.GetValue<bool>()) reasons.Add("cleanup completed marker is missing");
        if (metrics["exception_count"]!.GetValue<int>() > 0) reasons.Add($"{metrics["exception_count"]} exception line(s) detected");
        if (metrics["invalid_count"]!.GetValue<int>() > 0) reasons.Add($"{metrics["invalid_count"]} invalid block line(s) detected");
        if (cleanError.Length > 0) reasons.Add("cleanup verification failed: " + cleanError);
        if (Get("MEASUREMENT_MODE", "standard") == "compute-warm" && (metrics["warm_bad"]!.AsArray().Count > 0 || indices.Except(metrics["warm_ok"]!.AsArray().Select(value => value!.GetValue<long>())).Any())) reasons.Add("compute-warm warmup is missing or failed");
        if (cancelled) reasons.Add("campaign cancellation requested");
        return reasons;
    }

    internal static void SaveCampaign(string root, string started, JsonArray images, int runCount, JsonArray samples, List<string>? failureReasons = null)
    {
        var record = new JsonObject
        {
            ["started_at"] = started, ["finished_at"] = Now(), ["images"] = images.DeepClone(), ["run_count"] = runCount,
            ["architecture"] = Architecture, ["runner_hostname"] = Environment.MachineName, ["samples"] = samples.DeepClone()
        };
        if (failureReasons is { Count: > 0 }) record["failure_reasons"] = Array(failureReasons);
        Save(Path.Combine(root, "campaign.json"), record);
    }

    internal static string SampleSignature(JsonObject sample)
    {
        var metrics = sample["metrics"]!;
        var ids = sample["sse_block_ids"] is JsonArray { Count: > 0 } blocks ? blocks : metrics["ids"];
        return $"{metrics["source"]}|{metrics["count"]}|{metrics["delivered"]}|{ids?.ToJsonString()}";
    }

    internal static (double? Mean, double? Cv) ImageStats(JsonObject[] samples)
    {
        if (samples.Select(SampleSignature).Distinct().Count() != 1) return (null, null);
        var values = samples.Select(sample => sample["metrics"]!["avg"]!.GetValue<double>()).ToArray();
        var mean = values.Average();
        return (mean, values.Length > 1 && mean != 0 ? Math.Sqrt(values.Sum(value => Math.Pow(value - mean, 2)) / (values.Length - 1)) / mean * 100 : null);
    }

    internal static double? ComparisonDelta(JsonObject[] baseline, JsonObject[] candidate, int runCount)
    {
        if (baseline.Length != runCount || candidate.Length != runCount) return null;
        var signatures = baseline.Select(SampleSignature).ToHashSet();
        if (signatures.Count != 1 || !signatures.SetEquals(candidate.Select(SampleSignature))) return null;
        var baselineMean = ImageStats(baseline).Mean;
        var candidateMean = ImageStats(candidate).Mean;
        return baselineMean is null or 0 || candidateMean is null ? null : (candidateMean - baselineMean) / baselineMean * 100;
    }

    internal static string Format(double? value, int decimals = 4) => value?.ToString($"F{decimals}", CultureInfo.InvariantCulture) ?? "n/a";

    internal void WriteSummary(string root, JsonArray images, int runCount, JsonArray samples)
    {
        var successful = images.ToDictionary(image => Text(image!["id"]), image => samples.Where(sample => Text(sample!["status"]) == "success" && Text(sample["image_id"]) == Text(image!["id"])).Select(sample => sample!.AsObject()).ToArray());
        var baselineId = Text(images[0]!["id"]);
        var lines = new List<string> { "## EXPB Campaign", "", $"Runner: {Environment.MachineName} ({Architecture})", $"Client: {Get("CLIENT", "nethermind")} (measurement_source={Get("MEASUREMENT_SOURCE", "auto")})", $"Samples: {samples.Count}", "", "**Images:**" };
        foreach (var image in images) lines.Add($"- `{image!["id"]}` → `{image["image"]}`");
        lines.AddRange(["", "| Sample | Status | Source | Count | Processing AVG ms | Request AVG ms | Outside AVG ms | MGas/s | Request MGas/s |", "|---|---|---|---:|---:|---:|---:|---:|---:|"]);
        foreach (var sample in samples)
        {
            var metrics = sample!["metrics"]!;
            var source = Text(metrics["source"], "n/a");
            var processingAvg = source == "SSE" ? Format(metrics["avg"]?.GetValue<double>()) : "n/a";
            lines.Add($"| {sample["sample_id"]} | {sample["status"]} | {source} | {Text(metrics["count"], "0")} | {processingAvg} | {Format(metrics["request"]?["avg"]?.GetValue<double>())} | {Format(metrics["outside"]?["avg"]?.GetValue<double>())} | {Format(metrics["mgas_s"]?.GetValue<double>(), 2)} | {Format(metrics["request_mgas_s"]?.GetValue<double>(), 2)} |");
        }
        lines.Add("");
        foreach (var image in images)
        {
            var id = Text(image!["id"]);
            var (mean, cv) = ImageStats(successful[id]);
            var delta = id == baselineId ? null : ComparisonDelta(successful[baselineId], successful[id], runCount);
            var deltaText = delta is null ? "n/a" : delta.Value.ToString("+0.00;-0.00;+0.00", CultureInfo.InvariantCulture) + "%";
            lines.Add($"Image {id}: mean AVG={Format(mean)} ms; CV={(cv is null ? "unavailable" : Format(cv, 2) + "%")}; delta vs {baselineId}={deltaText}");
        }
        File.WriteAllText(Path.Combine(root, "summary.md"), string.Join('\n', lines) + "\n");
    }

    internal async Task<int> Run(CancellationToken cancellationToken,
        Func<CancellationToken, Task<JsonObject>>? load = null,
        Func<JsonObject, CancellationToken, Task>? verify = null,
        Func<JsonObject, JsonObject, int, string, CancellationToken, Task<JsonObject>>? sample = null,
        Action<string, JsonArray, int, JsonArray>? summary = null,
        Action<string, string, JsonArray, int, JsonArray, List<string>?>? save = null)
    {
        var root = Get("EXPB_CAMPAIGN_DIR");
        if (root.Length == 0) root = ".";
        Directory.CreateDirectory(root);
        var started = Now();
        JsonObject config;
        JsonArray images;
        int runCount;
        verify ??= VerifyClean;
        sample ??= (config, image, run, directory, token) => RunSample(config, image, run, directory, token);
        summary ??= WriteSummary;
        save ??= SaveCampaign;
        try
        {
            config = load is not null ? await load(cancellationToken) : JsonNode.Parse(await Capture(Get("YQ_BIN", "yq"), ["-o=json", ".", Get("EXPB_CONFIG_TEMPLATE")], cancellationToken))!.AsObject();
            if (JsonNode.Parse(Get("EXPB_IMAGES_JSON")) is not JsonArray { Count: > 0 } raw) throw new ArgumentException("images JSON must be a non-empty array");
            images = [];
            foreach (var image in raw) images.Add((JsonNode)new JsonObject { ["id"] = Text(image!["id"]), ["image"] = image["image"]!.DeepClone() });
            var ids = images.Select(image => Text(image!["id"])).ToArray();
            if (ids.Distinct().Count() != ids.Length || ids.Any(id => id is "." or ".." || !Regex.IsMatch(id, @"\A[A-Za-z0-9_.-]+\z"))) throw new ArgumentException("image IDs must be unique and shell-safe");
            runCount = int.Parse(Get("RUN_COUNT", "1"), CultureInfo.InvariantCulture);
            if (runCount < 1) throw new ArgumentException("RUN_COUNT must be positive");
            Render(config, images[0]!.AsObject(), 1);
            ParsePairs(Get("EXPB_ENV_PASSTHROUGH"));
            await verify(config, cancellationToken);
        }
        catch (Exception exception)
        {
            Save(Path.Combine(root, "campaign.json"), new JsonObject { ["status"] = "failed", ["failure_reasons"] = new JsonArray(exception.Message) });
            return 1;
        }
        var samples = new JsonArray();
        var failFast = Get("CAMPAIGN_FAIL_FAST", "true") != "false";
        var stopped = false;
        foreach (var image in images)
        {
            for (var run = 1; run <= runCount; run++)
            {
                if (cancellationToken.IsCancellationRequested) break;
                var item = await sample(config, image!.AsObject(), run, root, cancellationToken);
                samples.Add((JsonNode)item);
                save(root, started, images, runCount, samples, null);
                if (Text(item["status"]) != "success")
                {
                    // Unverified cleanup invalidates every later measurement, even in a retrospective sweep.
                    stopped = failFast || !(item["cleanup_verified"]?.GetValue<bool>() ?? true);
                    break;
                }
            }
            if (stopped || cancellationToken.IsCancellationRequested) break;
        }
        var failureReasons = new List<string>();
        try { summary(root, images, runCount, samples); }
        catch (Exception exception) { failureReasons.Add($"summary could not be written: {exception.Message}"); }
        try { save(root, started, images, runCount, samples, failureReasons); }
        catch (Exception exception)
        {
            var recordPath = Path.Combine(root, "campaign.json");
            JsonObject record;
            try { record = JsonNode.Parse(File.ReadAllText(recordPath)) as JsonObject ?? []; }
            catch (Exception readError) when (readError is IOException or JsonException) { record = []; }
            record["status"] = "failed";
            record["failure_reasons"] = Array([.. failureReasons, $"campaign record could not be written: {exception.Message}"]);
            Save(recordPath, record);
            return 1;
        }
        return !stopped && !cancellationToken.IsCancellationRequested && failureReasons.Count == 0 && samples.Count == images.Count * runCount && samples.All(item => Text(item!["status"]) == "success") ? 0 : 1;
    }
}
