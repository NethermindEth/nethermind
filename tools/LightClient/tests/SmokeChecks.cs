// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

internal sealed record SmokeOptions(string RpcUrl = "http://127.0.0.1:8545", string GrafanaUrl = "http://127.0.0.1:3000",
    string? PrometheusUrl = null, string Network = "mainnet", int WaitSeconds = 900)
{
    internal static SmokeOptions Parse(string[] args)
    {
        SmokeOptions options = new();
        for (int i = 0; i < args.Length; i++)
        {
            if (i + 1 == args.Length) throw new ArgumentException($"Missing value for {args[i]}");
            string name = args[i++];
            string value = args[i];
            options = name switch
            {
                "--rpc-url" => options with { RpcUrl = value },
                "--grafana-url" => options with { GrafanaUrl = value },
                "--prometheus-url" => options with { PrometheusUrl = value },
                "--network" when value is "mainnet" or "sepolia" or "hoodi" => options with { Network = value },
                "--wait-seconds" when int.TryParse(value, out int seconds) && seconds >= 0 => options with { WaitSeconds = seconds },
                _ => throw new ArgumentException($"Invalid option: {name} {value}"),
            };
        }
        return options;
    }
}

internal sealed class SmokeChecks(HttpClient client, TimeProvider clock, Func<TimeSpan, CancellationToken, Task> delay, TextWriter output)
{
    private static readonly Regex SelectorPattern = new("\\b([a-zA-Z_:][a-zA-Z0-9_:]*)\\{([^{}]*)\\}");
    private static readonly Regex LabelPattern = new("(\\w+)=(\"(?:[^\"\\\\]|\\\\.)*\")");
    private static readonly Regex SamplePattern = new("^([a-zA-Z_:][a-zA-Z0-9_:]*)(\\{[^{}]*\\})?\\s+(\\S+)(?:\\s+\\S+)?$");

    internal static string DeploymentDirectory()
    {
        if (AppContext.GetData("EntryPointFileDirectoryPath") is not string directory || !Path.IsPathFullyQualified(directory))
            throw new InvalidOperationException("Run the .cs source with dotnet run so the SDK supplies its physical source directory.");
        return Path.GetFullPath(Path.Combine(directory, ".."));
    }

    internal async Task RunAsync(SmokeOptions options, string password, string dashboardPath, CancellationToken token = default)
    {
        if (string.IsNullOrEmpty(password))
            throw new InvalidOperationException("Set GRAFANA_ADMIN_PASSWORD to verify the provisioned Grafana dashboard");
        string grafanaAuthorization = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"admin:{password}"));
        string? authorization = options.PrometheusUrl is null ? grafanaAuthorization : null;
        string queryUrl = options.PrometheusUrl is { } prometheus ? prometheus + "/api/v1/query?query="
            : options.GrafanaUrl + "/api/datasources/proxy/uid/Prometheus/api/v1/query?query=";
        long started = clock.GetTimestamp();
        TimeSpan Remaining() => TimeSpan.FromSeconds(options.WaitSeconds) - clock.GetElapsedTime(started);
        while (true)
        {
            string diagnostic;
            try
            {
                JsonNode status = await JsonAsync(options.RpcUrl + "/status", token: token);
                if (status["startupStage"]?.GetValue<string>() == "running" && status["rpcInitialized"]?.GetValue<bool>() == true
                    && status["latest"]?["fresh"]?.GetValue<bool>() == true) break;
                diagnostic = $"startupStage={status["startupStage"]}, rpcInitialized={status["rpcInitialized"]}, latest={status["latest"]}";
            }
            catch (Exception error) when (error is HttpRequestException or IOException or TimeoutException)
            {
                diagnostic = error.Message;
            }
            if (Remaining() <= TimeSpan.Zero) throw new InvalidOperationException($"Client not ready: {diagnostic}");
            output.WriteLine($"Waiting for fresh authenticated head: {diagnostic}");
            await delay(MinimumDelay(Remaining()), token);
        }

        int chainId = options.Network switch { "mainnet" => 1, "sepolia" => 11155111, "hoodi" => 560048, _ => throw new ArgumentException("Unknown network") };
        foreach ((string method, JsonArray parameters, string? expected) in new (string, JsonArray, string?)[]
        {
            ("eth_chainId", [], $"0x{chainId:x}"),
            ("eth_getBalance", ["0xde198901C5ee4611142E0c61DfbbCEc7ab468F32", "latest"], null),
        })
        {
            long requestStarted = clock.GetTimestamp();
            JsonNode response = await JsonAsync(options.RpcUrl + "/", new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = 1,
                ["method"] = method,
                ["params"] = parameters,
            }, timeout: TimeSpan.FromSeconds(95), token: token);
            if (response.AsObject().ContainsKey("error") || !response.AsObject().ContainsKey("result"))
                throw new InvalidOperationException($"{method} failed: {response}");
            if (expected is not null && response["result"]?.GetValue<string>() != expected)
                throw new InvalidOperationException($"Unexpected chain: {response["result"]}");
            output.WriteLine($"{method}: {response["result"]} ({clock.GetElapsedTime(requestStarted).TotalSeconds:F3}s)");
        }

        string metrics = await TextAsync(options.RpcUrl + "/metrics", token);
        List<MetricSample> samples = ScrapeSeries(metrics);
        JsonNode dashboard = JsonNode.Parse(await File.ReadAllTextAsync(dashboardPath, token))!;
        string dashboardUrl = options.GrafanaUrl + "/api/dashboards/uid/" + dashboard["uid"]!.GetValue<string>();
        JsonNode provisioned = (await JsonAsync(dashboardUrl, authorization: grafanaAuthorization, token: token))["dashboard"] ?? new JsonObject();
        if (provisioned["uid"]?.GetValue<string>() != dashboard["uid"]?.GetValue<string>()
            || !JsonNode.DeepEquals(DashboardContract(provisioned), DashboardContract(dashboard)))
            throw new InvalidOperationException("Provisioned Grafana dashboard does not match the expected panels and queries");

        List<string> queries = DashboardQueries(dashboard);
        foreach (string template in queries)
        {
            string expression = template.Replace("$network", options.Network).Replace("$__rate_interval", "1m");
            List<MetricSelector> expected = Selectors(expression);
            if (expected.Count == 0) throw new InvalidOperationException($"Dashboard query has no metric selectors: {expression}");
            foreach (MetricSelector selector in expected)
            {
                if (selector.Labels.GetValueOrDefault("job") != "nethermind-light-client")
                    throw new InvalidOperationException($"Dashboard query selects the wrong scrape job: {expression}");
                if (selector.Name != "up" && !samples.Any(sample => sample.Name == selector.Name && double.IsFinite(sample.Value)
                    && selector.Labels.All(label => label.Key == "job" || sample.Labels.GetValueOrDefault(label.Key) == label.Value)))
                    throw new InvalidOperationException($"Scrape has no matching metric and labels for: {expression}");
            }
            while (true)
            {
                JsonNode result = await JsonAsync(queryUrl + Uri.EscapeDataString(expression), authorization: authorization, token: token);
                if (result["status"]?.GetValue<string>() != "success" || result["data"]?["resultType"]?.GetValue<string>() != "vector")
                    throw new InvalidOperationException($"Dashboard query failed: {expression}: {result}");
                JsonArray values = result["data"]!["result"]!.AsArray();
                if (expression.Contains("rate(", StringComparison.Ordinal)) break;
                bool valid = values.Count > 0 && values.All(item => double.IsFinite(Number(item!["value"]![1]!.GetValue<string>()))
                    && expected.Any(selector => selector.Labels.All(label => item["metric"]?[label.Key]?.GetValue<string>() == label.Value)));
                if (expected[0].Name is "up" or "nethermind_light_client_ready")
                    valid &= values.All(item => item!["value"]![1]!.GetValue<string>() == "1");
                if (valid) break;
                if (Remaining() <= TimeSpan.Zero)
                    throw new InvalidOperationException($"Prometheus has no matching live series before the deadline: {expression}: {values}");
                output.WriteLine($"Waiting for Prometheus series: {expression}");
                await delay(MinimumDelay(Remaining()), token);
            }
        }

        if ((await JsonAsync(options.GrafanaUrl + "/api/health", token: token))["database"]?.GetValue<string>() != "ok")
            throw new InvalidOperationException("Grafana database is not healthy");
        try { await JsonAsync(options.GrafanaUrl + "/api/dashboards/uid/nethermind-light-client", token: token); }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Unauthorized)
        {
            output.WriteLine($"Ready {options.Network} client; metrics, {queries.Count} PromQL queries, and Grafana access policy passed.");
            return;
        }
        throw new InvalidOperationException("Grafana dashboard is accessible without authentication");
    }

    internal static List<MetricSelector> Selectors(string expression)
    {
        List<MetricSelector> result = [];
        foreach (Match match in SelectorPattern.Matches(expression))
        {
            Dictionary<string, string> labels = [];
            foreach (Match label in LabelPattern.Matches(match.Groups[2].Value))
                labels.Add(label.Groups[1].Value, JsonNode.Parse(label.Groups[2].Value)!.GetValue<string>());
            result.Add(new(match.Groups[1].Value, labels));
        }
        return result;
    }

    internal static List<string> DashboardQueries(JsonNode dashboard) => [.. dashboard["panels"]!.AsArray()
        .SelectMany(panel => panel!["targets"]!.AsArray()).Select(target => target!["expr"]!.GetValue<string>())];

    private static JsonArray DashboardContract(JsonNode dashboard)
    {
        JsonArray panels = [];
        foreach (JsonNode? panel in dashboard["panels"]?.AsArray() ?? [])
        {
            JsonArray targets = [];
            foreach (JsonNode? target in panel!["targets"]!.AsArray())
                targets.Add(new JsonArray(target!["refId"]?.DeepClone(), target["expr"]?.DeepClone(), target["datasource"]?.DeepClone()));
            panels.Add(new JsonArray(panel["id"]?.DeepClone(), panel["title"]?.DeepClone(), panel["type"]?.DeepClone(), panel["datasource"]?.DeepClone(), targets));
        }
        return panels;
    }

    private static List<MetricSample> ScrapeSeries(string text)
    {
        List<MetricSample> result = [];
        foreach (string line in text.Split('\n'))
        {
            Match match = SamplePattern.Match(line.TrimEnd('\r'));
            if (!match.Success) continue;
            string name = match.Groups[1].Value;
            Dictionary<string, string> labels = Selectors(name + (match.Groups[2].Success ? match.Groups[2].Value : "{}"))[0].Labels;
            result.Add(new(name, labels, Number(match.Groups[3].Value)));
        }
        return result;
    }

    private async Task<JsonNode> JsonAsync(string url, JsonNode? body = null, string? authorization = null,
        TimeSpan? timeout = null, CancellationToken token = default)
    {
        using HttpRequestMessage request = new(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        if (authorization is not null) request.Headers.Add("Authorization", authorization);
        TimeSpan requestTimeout = timeout ?? TimeSpan.FromSeconds(15);
        using CancellationTokenSource budget = new(requestTimeout, clock);
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(token, budget.Token);
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, stopping.Token);
            response.EnsureSuccessStatusCode();
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(stopping.Token))!;
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new TimeoutException($"HTTP request to {url} exceeded its {requestTimeout.TotalSeconds} second budget", error);
        }
    }

    private async Task<string> TextAsync(string url, CancellationToken token)
    {
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(15), clock);
        using CancellationTokenSource stopping = CancellationTokenSource.CreateLinkedTokenSource(token, budget.Token);
        using HttpResponseMessage response = await client.GetAsync(url, stopping.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(stopping.Token);
    }

    private static TimeSpan MinimumDelay(TimeSpan remaining) => remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
    private static double Number(string value) => value switch
    {
        "+Inf" => double.PositiveInfinity,
        "-Inf" => double.NegativeInfinity,
        _ => double.Parse(value, CultureInfo.InvariantCulture),
    };
    internal sealed record MetricSelector(string Name, Dictionary<string, string> Labels);
    private sealed record MetricSample(string Name, Dictionary<string, string> Labels, double Value);
}
