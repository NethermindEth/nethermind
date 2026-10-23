#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:include WorkflowPolling.Helpers.cs

using System.Net.Http.Headers;
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
try
{
    return await WorkflowPolling.RunAsync(async (path, cancellationToken) =>
    {
        using var response = await client.GetAsync(path, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode && response.StatusCode is not (System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden))
            response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(text);
    }, Task.Delay, name => Environment.GetEnvironmentVariable(name) ?? "", DateTimeOffset.UtcNow, Console.Out, Console.Error, cancellation.Token);
}
catch (OperationCanceledException) { Console.Error.WriteLine("Workflow polling cancelled or timed out."); return 1; }
catch (Exception exception) when (exception is HttpRequestException or JsonException or IOException or ArgumentException or FormatException or OverflowException)
{
    Console.Error.WriteLine($"Workflow polling failed: {exception.Message}"); return 1;
}
finally { Console.CancelKeyPress -= cancel; }
