#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property SaveDiskSpace=true
#:property TreatWarningsAsErrors=true
#:property Nullable=enable
#:property PublishAot=false
#:include SmokeChecks.cs

using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

return await DeploymentTests.RunAsync();

internal static partial class DeploymentTests
{
    private static readonly string Deployment = SmokeChecks.DeploymentDirectory();
    internal static readonly string DashboardPath = Path.Combine(Deployment, "observability/grafana/dashboards/light-client.json");

    internal static async Task<int> RunAsync()
    {
        Task<JsonNode> configuration = ServicesAsync();
        List<(string Name, Func<Task> Check)> checks =
        [
            ("Deployment assets follow the physical file-app entrypoint", CheckDeploymentDirectoryAsync),
            ("Missing file-app source metadata has a useful diagnostic", CheckMissingDeploymentDirectoryAsync),
            ("Docker base images match the repository SDK, runtime and digest pins", CheckBaseImagesAsync),
            ("Grafana requires an explicit password", async () =>
            {
                ProcessResult missing = await ComposeAsync(null);
                Require(missing.Code != 0 && missing.Error.Contains("GRAFANA_ADMIN_PASSWORD"), "Compose accepted a missing Grafana password");
                JsonNode environment = (await configuration)["grafana"]!["environment"]!;
                Require(environment["GF_AUTH_ANONYMOUS_ENABLED"]!.GetValue<string>() == "false", "Anonymous Grafana access is enabled");
                Require(environment["GF_USERS_ALLOW_SIGN_UP"]!.GetValue<string>() == "false", "Grafana sign-up is enabled");
            }),
            ("Only P2P ports are public", async () =>
            {
                HashSet<(string, int, int, string)> privatePorts = [], publicPorts = [];
                foreach ((string service, JsonNode? config) in (await configuration).AsObject())
                    foreach (JsonNode? port in config!["ports"]?.AsArray() ?? [])
                    {
                        (string, int, int, string) mapping = (service, int.Parse(port!["published"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                            port["target"]!.GetValue<int>(), port["protocol"]!.GetValue<string>());
                        (port["host_ip"]?.GetValue<string>() == "127.0.0.1" ? privatePorts : publicPorts).Add(mapping);
                    }
                Require(privatePorts.SetEquals([("light-client", 8545, 8545, "tcp"), ("grafana", 3000, 3000, "tcp")]), "Private ports changed");
                Require(publicPorts.SetEquals([("light-client", 30303, 30303, "tcp"), ("light-client", 30303, 30303, "udp"),
                    ("light-client", 9050, 9050, "tcp"), ("light-client", 9050, 9050, "udp"), ("light-client", 9051, 9051, "udp")]), "Public ports changed");
            }),
            ("Each service persists its owned data", async () =>
            {
                foreach ((string service, string target) in new[] { ("light-client", "/data"), ("prometheus", "/prometheus"), ("grafana", "/var/lib/grafana") })
                {
                    JsonNode config = (await configuration)[service]!;
                    JsonNode?[] mounts = [.. config["volumes"]!.AsArray().Where(mount => mount!["target"]!.GetValue<string>() == target)];
                    Require(mounts.Length == 1 && mounts[0]!["type"]!.GetValue<string>() == "volume"
                        && mounts[0]!["read_only"]?.GetValue<bool>() != true, $"{service} has no owned writable volume at {target}");
                    Require(config["restart"]!.GetValue<string>() == "unless-stopped", $"{service} restart policy changed");
                }
            }),
            ("Publish trimming preserves EVM dependencies and symbols", CheckTrimmingAsync),
            ("Dashboard queries scope job and network", () =>
            {
                HashSet<string> used = [];
                foreach (string expression in SmokeChecks.DashboardQueries(JsonNode.Parse(File.ReadAllText(DashboardPath))!))
                {
                    Require(expression.Contains("job=\"nethermind-light-client\""), $"Unscoped job: {expression}");
                    MatchCollection metrics = Regex.Matches(expression, @"\bnethermind_light_client_(\w+)");
                    if (metrics.Count > 0) Require(expression.Contains("network=\"$network\""), $"Unscoped network: {expression}");
                    foreach (Match match in metrics) used.Add(match.Groups[1].Value);
                }
                Require(new[] { "ready", "head_fresh", "peers", "rpc_requests_total", "rpc_duration_seconds_bucket" }.All(used.Contains), "Required dashboard metrics are missing");
                return Task.CompletedTask;
            }),
        ];
        checks.Add(("Smoke defaults to Ethereum mainnet", () =>
        {
            Require(SmokeOptions.Parse([]).Network == "mainnet", "The smoke default network changed");
            return Task.CompletedTask;
        }
        ));
        foreach ((string network, string chainId) in new[] { ("mainnet", "0x1"), ("sepolia", "0xaa36a7"), ("hoodi", "0x88bb0"), ("gnosis", "0x64") })
            checks.Add(($"Smoke verifies {network} chain ID and metric labels", async () =>
            {
                SmokeOptions options = SmokeOptions.Parse(["--network", network]);
                Require(options.Network == network, "The smoke network option was not retained");
                await RunSmokeAsync(network: options.Network, chainId: chainId);
            }
            ));
        checks.Add(("Smoke rejects an Ethereum endpoint selected as Gnosis", async () =>
            await ExpectFailureAsync(() => RunSmokeAsync(network: "gnosis"), "Unexpected chain: 0x1")));
        foreach (string? value in new string?[] { null, "NaN", "0" })
            checks.Add(($"Smoke accepts no-traffic rates: {value ?? "empty"}", async () => await RunSmokeAsync(rateValue: value)));
        foreach (int seconds in new[] { 20, 90 })
            checks.Add(($"Smoke permits a cold {seconds}s account proof", async () => await RunSmokeAsync(coldBalanceSeconds: seconds, waitSeconds: 900)));
        checks.Add(("The RPC probe deadline remains bounded at 95s", async () =>
            await ExpectFailureAsync(() => RunSmokeAsync(coldBalanceSeconds: 96, waitSeconds: 900), "95 second budget")));
        checks.Add(("An RPC error fails without retries", async () =>
        {
            SmokeResponder response = new(balanceError: true);
            await ExpectFailureAsync(() => RunSmokeAsync(response: response, waitSeconds: 900), "eth_getBalance failed");
            Require(response.StatusCalls == 1 && response.BalanceCalls == 1 && response.Sleeps == 0, "An unavailable RPC result was retried");
        }
        ));
        foreach (string mutation in new[] { "query_empty", "query_labels", "scrape_labels", "dashboard_missing", "dashboard_panels", "dashboard_query", "dashboard_public" })
            checks.Add(($"Smoke rejects {mutation}", async () => await ExpectFailureAsync(() => RunSmokeAsync(mutation: mutation))));
        checks.Add(("Smoke retries connection errors and the first ready scrape", async () =>
        {
            SmokeResponder response = await RunSmokeAsync(startupErrors: 2, readyScrapes: 1, waitSeconds: 900);
            Require(response.StatusCalls == 3 && response.ReadyQueries >= 2 && response.Sleeps == 3, "Startup and first-scrape retry behavior changed");
        }
        ));
        checks.Add(("Startup expiry retains the last connection failure", async () =>
            await ExpectFailureAsync(() => RunSmokeAsync(startupErrors: 1), "client restarting")));

        int failures = 0;
        foreach ((string name, Func<Task> check) in checks)
        {
            try { await check(); Console.WriteLine($"PASS {name}"); }
            catch (Exception error) { failures++; Console.Error.WriteLine($"FAIL {name}: {error}"); }
        }
        Console.WriteLine($"{checks.Count - failures}/{checks.Count} deployment checks passed");
        return failures == 0 ? 0 : 1;
    }

    [GeneratedRegex(@"(?m)^FROM(?: --platform=\S+)? (?<reference>mcr\.microsoft\.com/dotnet/(?<kind>sdk|aspnet):(?<version>\d+\.\d+\.\d+)-[a-z0-9-]+@sha256:[a-f0-9]{64})(?: AS \S+)?\r?$", RegexOptions.CultureInvariant)]
    private static partial Regex BaseImagePattern();

    private static Task CheckBaseImagesAsync()
    {
        string repository = Path.GetFullPath(Path.Combine(Deployment, "../../../.."));
        Version sdk = Version.Parse(JsonNode.Parse(File.ReadAllText(Path.Combine(repository, "global.json")))!["sdk"]!["version"]!.GetValue<string>());
        string framework = XDocument.Load(Path.Combine(repository, "Directory.Build.props")).Descendants("TargetFramework").Single().Value;
        Version runtime = Version.Parse(framework["net".Length..]);
        MatchCollection images = BaseImagePattern().Matches(File.ReadAllText(Path.Combine(Deployment, "Dockerfile")));
        MatchCollection fullNodeImages = BaseImagePattern().Matches(File.ReadAllText(Path.Combine(repository, "Dockerfile")));
        Match chiseledRuntime = BaseImagePattern().Matches(File.ReadAllText(Path.Combine(repository, "Dockerfile.chiseled")))
            .Single(image => image.Groups["kind"].Value == "aspnet");
        Match[] expectedImages = [.. fullNodeImages.Cast<Match>(), chiseledRuntime];
        Require(images.Count == expectedImages.Length, "Docker stages must pin the SDK, package installer and chiseled runtime");
        foreach ((Match image, Match expected) in images.Cast<Match>().Zip(expectedImages))
        {
            string kind = image.Groups["kind"].Value;
            Version version = Version.Parse(image.Groups["version"].Value);
            Require(kind == "sdk" ? version.Major == sdk.Major && version.Minor == sdk.Minor && version >= sdk
                : version.Major == runtime.Major && version.Minor == runtime.Minor,
                $"Docker {kind} version {version} does not match the repository SDK or target framework");
            Require(image.Groups["reference"].Value == expected.Groups["reference"].Value,
                $"Docker {kind} tag and digest do not match the repository's maintained image pin");
        }
        return Task.CompletedTask;
    }

    private static Task CheckDeploymentDirectoryAsync()
    {
        object? original = AppContext.GetData("EntryPointFileDirectoryPath");
        using ScratchDirectory scratch = new();
        try
        {
            AppContext.SetData("EntryPointFileDirectoryPath", Path.Combine(scratch.Path, "tests"));
            Require(SmokeChecks.DeploymentDirectory() == Path.GetFullPath(scratch.Path), "Deployment paths followed a compiler-mapped source or cached assembly instead of the physical entrypoint");
        }
        finally { AppContext.SetData("EntryPointFileDirectoryPath", original); }
        return Task.CompletedTask;
    }

    private static async Task CheckMissingDeploymentDirectoryAsync()
    {
        object? original = AppContext.GetData("EntryPointFileDirectoryPath");
        try
        {
            foreach (object? value in new object?[] { null, "", "relative/tests", 42 })
            {
                AppContext.SetData("EntryPointFileDirectoryPath", value);
                await ExpectFailureAsync(() =>
                {
                    SmokeChecks.DeploymentDirectory();
                    return Task.CompletedTask;
                }, "Run the .cs source with dotnet run");
            }
        }
        finally { AppContext.SetData("EntryPointFileDirectoryPath", original); }
    }

    private static async Task<JsonNode> ServicesAsync()
    {
        ProcessResult result = await ComposeAsync("deployment-test-password");
        Require(result.Code == 0, result.Error);
        return JsonNode.Parse(result.Output)!["services"]!;
    }

    private static Task<ProcessResult> ComposeAsync(string? password)
    {
        Dictionary<string, string?> environment = new()
        {
            ["GRAFANA_ADMIN_PASSWORD"] = password,
            ["LC_IMAGE"] = null,
            ["LC_NETWORK"] = null,
            ["LC_CHECKPOINT_URL"] = null,
        };
        return ProcessAsync("docker", ["compose", "--env-file", Path.Combine(Deployment, ".env.example"), "-f", Path.Combine(Deployment, "compose.yml"), "config", "--format", "json"], environment: environment);
    }

    private static async Task CheckTrimmingAsync()
    {
        using ScratchDirectory scratch = new();
        string[] retained = ["Nethermind.LightClient.dll", "Nethermind.LightClient.pdb", "ClearScript.Core.dll", "ClearScript.V8.dll", "runtimes/linux-x64/native/librocksdb.so", "kzg_trusted_setup.txt"];
        string[] removed = ["ClearScript.V8.ICUData.dll", "runtimes/linux-x64/native/ClearScriptV8.linux-x64.so", "runtimes/linux-arm64/native/ClearScriptV8.linux-arm64.so.pem", "Data/JSTracers/4byte.js", "Nethermind.JsonRpc.SourceGenerator.Build", "Nethermind.JsonRpc.SourceGenerator.Build.exe", "Nethermind.JsonRpc.SourceGenerator.Build.dll", "Nethermind.JsonRpc.SourceGenerator.Build.deps.json", "Nethermind.JsonRpc.SourceGenerator.Build.runtimeconfig.json"];
        foreach (string name in retained.Concat(removed))
        {
            string path = Path.Combine(scratch.Path, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "preserve contents");
        }
        ProcessResult result = await ProcessAsync(Shell(), [Path.Combine(Deployment, "trim-publish.sh").Replace('\\', '/'), scratch.Path.Replace('\\', '/')]);
        Require(result.Code == 0, result.Error);
        HashSet<string> files = [.. Directory.GetFiles(scratch.Path, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(scratch.Path, path).Replace('\\', '/'))];
        Require(files.SetEquals(retained), $"Incorrect trimmed file set: {string.Join(", ", files)}");
        foreach (string name in retained) Require(File.ReadAllText(Path.Combine(scratch.Path, name)) == "preserve contents", $"Trimming changed {name}");
    }

    private static async Task<SmokeResponder> RunSmokeAsync(string? mutation = null, int startupErrors = 0, int readyScrapes = 0,
        int waitSeconds = 0, string? rateValue = null, int coldBalanceSeconds = 0, SmokeResponder? response = null,
        string network = "mainnet", string chainId = "0x1")
    {
        using SmokeResponder responder = response ?? new(mutation, startupErrors, readyScrapes, rateValue, coldBalanceSeconds, network: network, chainId: chainId);
        using HttpClient client = new(responder, disposeHandler: false);
        SmokeChecks checks = new(client, responder.Clock, responder.Delay, TextWriter.Null);
        await checks.RunAsync(new(PrometheusUrl: "http://localhost:9090", Network: network, WaitSeconds: waitSeconds), "test-password", DashboardPath);
        return responder;
    }

    private static async Task ExpectFailureAsync(Func<Task> check, string? message = null)
    {
        try { await check(); }
        catch (Exception error) when (error is InvalidOperationException or HttpRequestException or TimeoutException)
        {
            if (message is not null) Require(error.Message.Contains(message), $"Wrong failure: {error.Message}");
            return;
        }
        throw new InvalidOperationException("The invalid deployment was accepted");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Shell() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "C:/Program Files", "Git/bin/bash.exe") : "/bin/sh";

    private static async Task<ProcessResult> ProcessAsync(string file, string[] arguments, string? directory = null, Dictionary<string, string?>? environment = null)
    {
        ProcessStartInfo start = new(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        if (directory is not null) start.WorkingDirectory = directory;
        if (environment is not null)
            foreach ((string key, string? value) in environment)
                if (value is null) start.Environment.Remove(key);
                else start.Environment[key] = value;
        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new(process.ExitCode, await output, await error);
    }

    private sealed record ProcessResult(int Code, string Output, string Error);
    private sealed class ScratchDirectory : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(OperatingSystem.IsWindows() ? "C:/Temp" : System.IO.Path.GetTempPath(), "lc-deployment-" + Guid.NewGuid());
        internal ScratchDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

internal sealed class SmokeResponder(string? mutation = null, int startupErrors = 0, int readyScrapes = 0,
    string? rateValue = null, int coldBalanceSeconds = 0, bool balanceError = false,
    string network = "mainnet", string chainId = "0x1") : HttpMessageHandler
{
    internal TestClock Clock { get; } = new();
    internal int StatusCalls { get; private set; }
    internal int BalanceCalls { get; private set; }
    internal int ReadyQueries { get; private set; }
    internal int Sleeps { get; private set; }
    private readonly List<(string Name, Dictionary<string, string> Labels)> _samples = Samples(network);

    internal Task Delay(TimeSpan duration, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Sleeps++;
        Clock.Advance(duration);
        return Task.CompletedTask;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri uri = request.RequestUri!;
        if (uri.AbsolutePath == "/status")
        {
            if (++StatusCalls <= startupErrors) throw new HttpRequestException("client restarting");
            return Json(new JsonObject { ["startupStage"] = "running", ["rpcInitialized"] = true, ["latest"] = new JsonObject { ["fresh"] = true } });
        }
        if (request.Method == HttpMethod.Post)
        {
            JsonNode body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            string method = body["method"]!.GetValue<string>();
            if (method == "eth_getBalance")
            {
                BalanceCalls++;
                Clock.Advance(TimeSpan.FromSeconds(coldBalanceSeconds));
                cancellationToken.ThrowIfCancellationRequested();
                if (balanceError) return Json(new JsonObject { ["error"] = new JsonObject { ["code"] = -32001, ["message"] = "Authenticated data unavailable" } });
            }
            return Json(new JsonObject { ["result"] = method == "eth_chainId" ? chainId : "0x1" });
        }
        if (uri.AbsolutePath == "/metrics")
        {
            string text = string.Join('\n', _samples.Select(sample => sample.Name + "{" + string.Join(',', sample.Labels.Select(label => $"{label.Key}=\"{label.Value}\"")) + "} 1"));
            if (mutation == "scrape_labels") text = text.Replace("layer=\"execution\"", "layer=\"wrong\"");
            return new(HttpStatusCode.OK) { Content = new StringContent(text) };
        }
        if (uri.AbsolutePath == "/api/health") return Json(new JsonObject { ["database"] = "ok" });
        if (uri.AbsolutePath.StartsWith("/api/dashboards/", StringComparison.Ordinal))
        {
            if (request.Headers.Authorization is null && mutation != "dashboard_public") return new(HttpStatusCode.Unauthorized);
            if (mutation == "dashboard_missing") return new(HttpStatusCode.NotFound);
            JsonNode dashboard = JsonNode.Parse(await File.ReadAllTextAsync(DeploymentTests.DashboardPath, cancellationToken))!;
            if (mutation == "dashboard_panels") dashboard["panels"] = new JsonArray();
            if (mutation == "dashboard_query") dashboard["panels"]![0]!["targets"]![0]!["expr"] = "up";
            return Json(new JsonObject { ["dashboard"] = dashboard });
        }
        string query = Uri.UnescapeDataString(uri.Query["?query=".Length..]);
        JsonArray values = [];
        if (query.Contains("rate(", StringComparison.Ordinal))
        {
            if (rateValue is not null) values.Add(new JsonObject { ["metric"] = new JsonObject(), ["value"] = new JsonArray(0, rateValue) });
        }
        else if (query.StartsWith("up{", StringComparison.Ordinal))
            values.Add(new JsonObject { ["metric"] = new JsonObject { ["job"] = "nethermind-light-client" }, ["value"] = new JsonArray(0, "1") });
        else
        {
            SmokeChecks.MetricSelector selector = SmokeChecks.Selectors(query)[0];
            foreach ((string name, Dictionary<string, string> labels) in _samples)
            {
                if (name != selector.Name || !selector.Labels.All(label => label.Key == "job" || labels.GetValueOrDefault(label.Key) == label.Value)) continue;
                JsonObject metric = new() { ["job"] = "nethermind-light-client", ["__name__"] = name };
                foreach ((string key, string value) in labels) metric[key] = value;
                if (name.Contains("head_block_number", StringComparison.Ordinal) && mutation == "query_labels") metric["network"] = "wrong";
                values.Add(new JsonObject { ["metric"] = metric, ["value"] = new JsonArray(0, "1") });
            }
            if (selector.Name.EndsWith("_ready", StringComparison.Ordinal) && ++ReadyQueries <= readyScrapes) values.Clear();
            if (selector.Name.Contains("head_block_number", StringComparison.Ordinal) && mutation == "query_empty") values.Clear();
        }
        return Json(new JsonObject { ["status"] = "success", ["data"] = new JsonObject { ["resultType"] = "vector", ["result"] = values } });
    }

    private static HttpResponseMessage Json(JsonNode node) => new(HttpStatusCode.OK) { Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json") };

    private static List<(string, Dictionary<string, string>)> Samples(string network)
    {
        List<(string, Dictionary<string, string>)> samples = [];
        void Add(string name, params (string Key, string Value)[] labels) => samples.Add(("nethermind_light_client_" + name,
            labels.ToDictionary(label => label.Key, label => label.Value).Append(new("network", network)).ToDictionary()));
        Add("ready"); Add("initialized"); Add("startup_stage", ("stage", "running")); Add("hosted_storage_slots");
        foreach (string layer in new[] { "beacon", "execution", "snap" }) Add("peers", ("layer", layer));
        foreach (string state in new[] { "total", "current", "failed" }) Add("hosted_accounts", ("state", state));
        foreach (string name in new[] { "head_block_number", "head_beacon_slot", "head_fresh", "head_available" })
            foreach (string head in new[] { "latest", "finalized" }) Add(name, ("head", head));
        Add("rpc_requests_total", ("method", "eth_chainId"), ("outcome", "ok"));
        Add("http_requests_total", ("outcome", "overloaded"));
        Add("rpc_duration_seconds_bucket", ("method", "eth_chainId"), ("outcome", "ok"), ("le", "+Inf"));
        foreach (string name in new[] { "process_cpu_seconds_total", "process_working_set_bytes", "process_private_memory_bytes" })
            samples.Add((name, new() { ["network"] = network }));
        return samples;
    }
}

internal sealed class TestClock : TimeProvider
{
    private long _ticks;
    private readonly List<TestTimer> _timers = [];
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _ticks;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        DeploymentTests.Require(period == Timeout.InfiniteTimeSpan, "The smoke fixture expects one-shot request budgets");
        TestTimer timer = new(this, callback, state, _ticks + dueTime.Ticks);
        _timers.Add(timer);
        return timer;
    }
    internal void Advance(TimeSpan duration)
    {
        _ticks += duration.Ticks;
        foreach (TestTimer timer in _timers.ToArray()) timer.Fire(_ticks);
    }
    private sealed class TestTimer(TestClock clock, TimerCallback callback, object? state, long deadline) : ITimer
    {
        private long _deadline = deadline;
        public bool Change(TimeSpan dueTime, TimeSpan period) { _deadline = clock._ticks + dueTime.Ticks; return true; }
        internal void Fire(long ticks) { if (ticks >= _deadline) { Dispose(); callback(state); } }
        public void Dispose() => clock._timers.Remove(this);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
