#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include Metrics.Helpers.cs
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

try
{
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    using var client = new HttpClient { BaseAddress = new Uri("https://api.github.com/"), Timeout = TimeSpan.FromSeconds(30) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Environment.GetEnvironmentVariable("GH_TOKEN") ?? throw new InvalidOperationException("GH_TOKEN is required"));
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Nethermind-Sync-Metrics/1.0");
    await Metrics.Publish(args[0], args[1], Environment.GetEnvironmentVariable("GITHUB_REPOSITORY")!, long.Parse(Environment.GetEnvironmentVariable("GITHUB_RUN_ID")!), Environment.GetEnvironmentVariable("GITHUB_RUN_ATTEMPT"), Environment.GetEnvironmentVariable("TRIGGER_SHA"),
        async (path, token) => JsonNode.Parse(await client.GetStringAsync(path, token))!, cancellation.Token);
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 143;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
