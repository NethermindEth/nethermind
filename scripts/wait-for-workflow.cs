#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Text.Json;

using var cancellation = new CancellationTokenSource();
using var terminate = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); });
ConsoleCancelEventHandler cancel = (_, context) => { context.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
using var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(100) };
client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
client.DefaultRequestHeaders.UserAgent.ParseAdd("Nethermind-CI");
var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
if (!string.IsNullOrEmpty(token)) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);
var started = DateTimeOffset.UtcNow;
Regex? nameFilter = null;
try
{
    var organization = EnvironmentValue("ORG_NAME"); var repository = EnvironmentValue("REPO_NAME");
    var reference = EnvironmentValue("REF");
    if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal)) reference = "refs/heads/" + reference;
    var name = EnvironmentValue("NAME_FILTER");
    var since = EnvironmentValue("DISCOVER_SINCE") is { Length: > 0 } dispatched ? DateTimeOffset.Parse(dispatched, CultureInfo.InvariantCulture).AddMinutes(-1) : started.AddMinutes(-1);
    var maxWait = double.Parse(EnvironmentValue("MAX_WAIT_MINUTES"), CultureInfo.InvariantCulture);
    var timeout = double.Parse(EnvironmentValue("TIMEOUT"), CultureInfo.InvariantCulture);
    var interval = double.Parse(EnvironmentValue("INTERVAL"), CultureInfo.InvariantCulture);
    if (!double.IsFinite(interval) || interval <= 0 || !double.IsFinite(maxWait) || maxWait < 0 || !double.IsFinite(timeout) || timeout < 0)
        throw new ArgumentException("workflow timing values must be finite and nonnegative, with a positive interval");
    Console.WriteLine($"ℹ️ Organization: {organization}\nℹ️ Repository: {repository}\nℹ️ Reference: {reference}\nℹ️ Maximum wait time: {maxWait} minutes\nℹ️ Timeout for the workflow to complete: {timeout} minutes\nℹ️ Interval between checks: {interval} seconds\nℹ️ Name filter applied: {name}");
    var runId = EnvironmentValue("RUN_ID");
    if (runId.Length > 0) Console.WriteLine($"ℹ️ Using provided Run ID: {runId}");
    else
    {
        var workflow = EnvironmentValue("WORKFLOW_ID");
        Console.WriteLine($"ℹ️ Workflow ID: {workflow}");
        var attempts = 0;
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            Console.WriteLine("⏳ Waiting for the workflow to be triggered...");
            using var response = await Poll($"repos/{organization}/{repository}/actions/workflows/{workflow}/runs");
            if (response.RootElement.TryGetProperty("message", out var message))
            {
                var text = message.GetString() ?? "";
                if (text.Contains("API rate limit exceeded", StringComparison.Ordinal)) { Console.Error.WriteLine("❌ API rate limit exceeded. Please try again later."); return 1; }
                if (text.Contains("Not Found", StringComparison.Ordinal)) { Console.Error.WriteLine("❌ Invalid input provided (organization, repository, or workflow ID). Please check your inputs."); return 1; }
            }
            runId = Discover(response.RootElement, reference[11..], name, since, ref nameFilter) ?? "";
            if (runId.Length > 0) { Console.WriteLine($"🎉 Workflow triggered! Run ID: {runId}"); break; }
            if (++attempts * interval >= maxWait * 60) { Console.Error.WriteLine("❌ Maximum waiting time for the workflow to be triggered has been reached. Exiting."); return 1; }
            await Task.Delay(TimeSpan.FromSeconds(interval), cancellation.Token);
        }
    }
    var polls = 0;
    while (true)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        Console.WriteLine("⌛ Waiting for the workflow to complete...");
        using var response = await Poll($"repos/{organization}/{repository}/actions/runs/{runId}");
        if (response.RootElement.TryGetProperty("status", out var status) && status.GetString() == "completed")
        {
            if (!response.RootElement.TryGetProperty("conclusion", out var conclusion) || conclusion.GetString() != "success")
            {
                Console.Error.WriteLine("❌ The workflow has not completed successfully. Exiting."); return 1;
            }
            Console.WriteLine($"✅ The workflow completed successfully! Exiting.\n👀 Check workflow details at: https://github.com/{organization}/{repository}/actions/runs/{runId}");
            await File.AppendAllTextAsync(EnvironmentValue("GITHUB_OUTPUT"), $"run_id={runId}\n", cancellation.Token);
            return 0;
        }
        if (++polls * interval >= timeout * 60) { Console.Error.WriteLine("❌ Timeout waiting for the workflow to complete. Exiting."); return 1; }
        await Task.Delay(TimeSpan.FromSeconds(interval), cancellation.Token);
    }

    async Task<JsonDocument> Poll(string path)
    {
        try
        {
            using var result = await client.GetAsync(path, cancellation.Token);
            var text = await result.Content.ReadAsStringAsync(cancellation.Token);
            if (!result.IsSuccessStatusCode && result.StatusCode is not (System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden)) result.EnsureSuccessStatusCode();
            var response = JsonDocument.Parse(text);
            if (response.RootElement.ValueKind == JsonValueKind.Object) return response;
            response.Dispose();
            throw new JsonException("Workflow response is not an object");
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException || exception is OperationCanceledException && !cancellation.Token.IsCancellationRequested)
        {
            Console.Error.WriteLine($"Workflow status unavailable; retrying: {exception.Message}");
            return JsonDocument.Parse("{}");
        }
    }
}
catch (OperationCanceledException) { Console.Error.WriteLine("Workflow polling cancelled or timed out."); return 1; }
catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"Workflow polling failed: {exception.Message}"); return 1;
}
finally { Console.CancelKeyPress -= cancel; }

static string EnvironmentValue(string name) => Environment.GetEnvironmentVariable(name) ?? "";

static string? Discover(JsonElement response, string branch, string namePattern, DateTimeOffset since, ref Regex? nameFilter)
{
    if (!response.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return null;
    long? newest = null;
    foreach (var run in runs.EnumerateArray())
    {
        if (run.GetProperty("head_branch").GetString() != branch || run.GetProperty("status").GetString() == "completed") continue;
        if (!DateTimeOffset.TryParse(run.GetProperty("created_at").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) || created < since) continue;
        if (namePattern.Length > 0 && !(nameFilter ??= new Regex(namePattern, RegexOptions.None, TimeSpan.FromSeconds(1))).IsMatch(run.GetProperty("name").GetString() ?? "")) continue;
        var id = run.GetProperty("id").GetInt64();
        if (newest is null || id > newest) newest = id;
    }
    return newest?.ToString(CultureInfo.InvariantCulture);
}
