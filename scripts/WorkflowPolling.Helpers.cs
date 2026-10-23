// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class WorkflowPolling
{
    internal static string? Discover(JsonElement response, string branch, string namePattern, DateTimeOffset since)
    {
        if (!response.TryGetProperty("workflow_runs", out var runs) || runs.ValueKind != JsonValueKind.Array) return null;
        long? newest = null;
        foreach (var run in runs.EnumerateArray())
        {
            if (run.GetProperty("head_branch").GetString() != branch || run.GetProperty("status").GetString() == "completed") continue;
            if (!DateTimeOffset.TryParse(run.GetProperty("created_at").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) || created < since) continue;
            if (namePattern.Length > 0 && !Regex.IsMatch(run.GetProperty("name").GetString() ?? "", namePattern, RegexOptions.None, TimeSpan.FromSeconds(1))) continue;
            var id = run.GetProperty("id").GetInt64();
            if (newest is null || id > newest) newest = id;
        }
        return newest?.ToString(CultureInfo.InvariantCulture);
    }

    internal static async Task<int> RunAsync(Func<string, CancellationToken, Task<JsonDocument>> request, Func<TimeSpan, CancellationToken, Task> delay,
        Func<string, string> environment, DateTimeOffset now, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        var organization = environment("ORG_NAME"); var repository = environment("REPO_NAME");
        var reference = environment("REF");
        if (!reference.StartsWith("refs/heads/", StringComparison.Ordinal)) reference = "refs/heads/" + reference;
        var name = environment("NAME_FILTER");
        var maxWait = double.Parse(environment("MAX_WAIT_MINUTES"), CultureInfo.InvariantCulture);
        var timeout = double.Parse(environment("TIMEOUT"), CultureInfo.InvariantCulture);
        var interval = double.Parse(environment("INTERVAL"), CultureInfo.InvariantCulture);
        if (!double.IsFinite(interval) || interval <= 0 || !double.IsFinite(maxWait) || maxWait < 0 || !double.IsFinite(timeout) || timeout < 0)
            throw new ArgumentException("workflow timing values must be finite and nonnegative, with a positive interval");
        output.WriteLine($"ℹ️ Organization: {organization}\nℹ️ Repository: {repository}\nℹ️ Reference: {reference}\nℹ️ Maximum wait time: {maxWait} minutes\nℹ️ Timeout for the workflow to complete: {timeout} minutes\nℹ️ Interval between checks: {interval} seconds\nℹ️ Name filter applied: {name}");
        var runId = environment("RUN_ID");
        if (runId.Length > 0) output.WriteLine($"ℹ️ Using provided Run ID: {runId}");
        else
        {
            var workflow = environment("WORKFLOW_ID");
            output.WriteLine($"ℹ️ Workflow ID: {workflow}");
            var attempts = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.WriteLine("⏳ Waiting for the workflow to be triggered...");
                using var response = await request($"repos/{organization}/{repository}/actions/workflows/{workflow}/runs", cancellationToken);
                if (response.RootElement.TryGetProperty("message", out var message))
                {
                    var text = message.GetString() ?? "";
                    if (text.Contains("API rate limit exceeded", StringComparison.Ordinal)) { error.WriteLine("❌ API rate limit exceeded. Please try again later."); return 1; }
                    if (text.Contains("Not Found", StringComparison.Ordinal)) { error.WriteLine("❌ Invalid input provided (organization, repository, or workflow ID). Please check your inputs."); return 1; }
                }
                runId = Discover(response.RootElement, reference[11..], name, now.AddMinutes(-1)) ?? "";
                if (runId.Length > 0) { output.WriteLine($"🎉 Workflow triggered! Run ID: {runId}"); break; }
                if (++attempts * interval >= maxWait * 60) { error.WriteLine("❌ Maximum waiting time for the workflow to be triggered has been reached. Exiting."); return 1; }
                await delay(TimeSpan.FromSeconds(interval), cancellationToken);
            }
        }
        var polls = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            output.WriteLine("⌛ Waiting for the workflow to complete...");
            using var response = await request($"repos/{organization}/{repository}/actions/runs/{runId}", cancellationToken);
            if (response.RootElement.TryGetProperty("status", out var status) && status.GetString() == "completed")
            {
                if (!response.RootElement.TryGetProperty("conclusion", out var conclusion) || conclusion.GetString() != "success")
                {
                    error.WriteLine("❌ The workflow has not completed successfully. Exiting."); return 1;
                }
                output.WriteLine($"✅ The workflow completed successfully! Exiting.\n👀 Check workflow details at: https://github.com/{organization}/{repository}/actions/runs/{runId}");
                await File.AppendAllTextAsync(environment("GITHUB_OUTPUT"), $"run_id={runId}\n", cancellationToken);
                return 0;
            }
            if (++polls * interval >= timeout * 60) { error.WriteLine("❌ Timeout waiting for the workflow to complete. Exiting."); return 1; }
            await delay(TimeSpan.FromSeconds(interval), cancellationToken);
        }
    }
}
