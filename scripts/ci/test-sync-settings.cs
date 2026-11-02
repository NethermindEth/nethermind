#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
#:property DefineConstants=SCRIPT_TEST
#:include ../sync-settings.cs

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

const string original = "{\"Sync\":{\"PivotNumber\":1,\"PivotHash\":\"old\"}}";
var passed = 0;
foreach (var (failures, invalidResult) in new[] { (1, "json"), (5, "json"), (1, "difficulty"), (5, "difficulty"), (1, "block"), (5, "block"), (1, "hash"), (5, "hash"), (1, "latest"), (5, "latest"), (1, "timeout"), (5, "timeout"), (1, "cancel"), (0, "json") })
{
    var directory = Directory.CreateTempSubdirectory("sync-settings-");
    try
    {
        foreach (var name in SyncSettings.Configs.Select(config => config.Name).Append("mainnet_aztec"))
            File.WriteAllText(Path.Combine(directory.FullName, name + ".json"), original);
        if (failures == 0) File.WriteAllText(Path.Combine(directory.FullName, "mainnet_aztec.json"), "{}");
        using var cancellation = new CancellationTokenSource();
        using var handler = new Responses(failures, invalidResult, cancellation);
        using var http = new HttpClient(handler);
        var delays = new List<double>();
        var output = Console.Out;
        var error = Console.Error;
        using var captured = new StringWriter();
        using var capturedError = new StringWriter();
        Console.SetOut(captured);
        Console.SetError(capturedError);
        var code = -1;
        try
        {
            code = await SyncSettings.Update(http, "", false, directory.FullName, (duration, token) => { Require(token == cancellation.Token, "retry delay ignores cancellation"); delays.Add(duration.TotalSeconds); return Task.CompletedTask; }, cancellation.Token);
            if (failures == 0) throw new InvalidOperationException("broken follower accepted");
            if (invalidResult == "cancel") throw new InvalidOperationException("caller cancellation swallowed");
        }
        catch (JsonException) when (failures == 0) { code = 2; }
        catch (OperationCanceledException) when (invalidResult == "cancel") { code = 130; }
        finally { Console.SetOut(output); Console.SetError(error); }
        if (failures == 0)
            Require(code == 2 && File.ReadAllText(Path.Combine(directory.FullName, "mainnet.json")) == original, "leader changed before follower validation");
        else if (invalidResult == "cancel")
            Require(code == 130 && handler.Failures == 1 && delays.Count == 0 && File.ReadAllText(Path.Combine(directory.FullName, "chiado.json")) == original, "caller cancellation retried or later chain updated");
        else
        {
            Require(handler.Failures == failures && delays.SequenceEqual(failures == 1 ? new double[] { 10 } : [10, 20, 30, 40]), "retry backoff changed");
            Require(code == (failures == 5 ? 1 : 0), "exit code changed");
            Require(capturedError.ToString().Trim() == (failures == 5 ? "Failed to update: gnosis" : ""), "failure summary changed");
            var gnosis = File.ReadAllText(Path.Combine(directory.FullName, "gnosis.json"));
            Require(failures == 5 ? gnosis == original : JsonNode.Parse(gnosis)!["Sync"]!["PivotNumber"]!.GetValue<long>() == 50000, "failed chain mutation or wrong pivot");
            Require(JsonNode.Parse(File.ReadAllText(Path.Combine(directory.FullName, "chiado.json")))!["Sync"]!["PivotNumber"]!.GetValue<long>() == 50000, "later chain not updated");
        }
        Console.WriteLine($"PASS {(failures == 0 ? "broken follower preserves leader" : invalidResult == "cancel" ? "caller cancellation stops without retry" : $"retry and continuation after {failures} failures, invalid {invalidResult}")}");
        passed++;
    }
    finally { directory.Delete(recursive: true); }
}
Console.WriteLine($"Passed {passed} tests.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

sealed class Responses(int maximumFailures, string invalidResult, CancellationTokenSource caller) : HttpMessageHandler
{
    internal int Failures { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.Method == HttpMethod.Get
            ? request.RequestUri!.Query.Contains("action=eth_blockNumber", StringComparison.Ordinal) ? "eth_blockNumber" : "eth_getBlockByNumber"
            : JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!["method"]!.GetValue<string>();
        if (request.RequestUri!.Host == "rpc.gnosischain.com" && Failures < maximumFailures && (invalidResult is "json" or "latest" or "timeout" or "cancel" || method == "eth_getBlockByNumber"))
        {
            Failures++;
            // HttpClient reports its own timeout as a cancellation the caller did not request.
            if (invalidResult == "timeout") throw new TaskCanceledException();
            if (invalidResult == "cancel")
            {
                await caller.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }
            var response = invalidResult switch
            {
                "difficulty" => "{\"result\":{\"hash\":\"0xab\",\"totalDifficulty\":null}}",
                "block" or "latest" => "{\"result\":null}",
                "hash" => "{\"result\":{}}",
                _ => "not JSON"
            };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response) };
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(method == "eth_blockNumber" ? "{\"result\":\"0x10000\"}" : "{\"result\":{\"hash\":\"0x" + new string('a', 64) + "\",\"totalDifficulty\":\"0x0\"}}")
        };
    }
}
