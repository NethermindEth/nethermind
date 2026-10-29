#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

#if !SCRIPT_TEST
var key = "";
for (var index = 0; index < args.Length; index++)
{
    if (args[index] is "-k" or "--key" && index + 1 < args.Length) key = args[++index];
    else
    {
        Console.WriteLine("usage: sync-settings.cs [-k|--key API_KEY]");
        return args[index] is "-h" or "--help" ? 0 : 2;
    }
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
try
{
    return await SyncSettings.Update(http, key, "./src/Nethermind/Nethermind.Runner/configs", Task.Delay, cancellation.Token);
}
catch (Exception) when (cancellation.IsCancellationRequested) { return 130; }
#endif

internal static class SyncSettings
{
    internal static readonly (string Name, string Url, int Reduction, int Multiple, bool Pos)[] Configs =
    [
        ("mainnet", "https://api.etherscan.io/v2/api?chainid=1", 1000, 1000, true),
        ("gnosis", "https://rpc.gnosischain.com", 8192, 10000, true),
        ("chiado", "https://rpc.chiadochain.net", 8192, 10000, true),
        ("sepolia", "https://api.etherscan.io/v2/api?chainid=11155111", 1000, 1000, true),
        ("linea-mainnet", "https://rpc.linea.build", 8192, 10000, false),
        ("linea-sepolia", "https://rpc.sepolia.linea.build", 8192, 10000, false),
        ("xdc", "https://erpc.xinfin.network", 8192, 10000, false),
        ("xdc-testnet", "https://rpc.apothem.network", 8192, 10000, false)
    ];

    internal static async Task<int> Update(HttpClient http, string key, string directory, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        var indentedJson = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine("Fast Sync configuration settings initialization     ✅ ");
        var failed = new List<string>();
        foreach (var (name, url, reduction, multiple, pos) in Configs)
        {
            Console.WriteLine($"{char.ToUpperInvariant(name[0])}{name[1..]} section                                     ✅ ");
            (long Number, string Hash, BigInteger Difficulty)? pivot = null;
            for (var attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    var latestResult = (await Request("eth_blockNumber", null))?.GetValue<string>() ?? throw new JsonException("Missing latest block number");
                    var latest = long.Parse(latestResult[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    var number = latest - reduction;
                    number -= (number % multiple + multiple) % multiple;
                    var block = await Request("eth_getBlockByNumber", "0x" + number.ToString("x", CultureInfo.InvariantCulture)) ?? throw new JsonException("Missing block");
                    var hash = block["hash"]?.GetValue<string>() ?? throw new JsonException("Missing block hash");
                    var totalDifficulty = block.AsObject().TryGetPropertyValue("totalDifficulty", out var declaredDifficulty)
                        ? declaredDifficulty?.GetValue<string>() ?? throw new JsonException("Null totalDifficulty") : "0x0";
                    var difficulty = BigInteger.Parse("0" + totalDifficulty[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    Console.WriteLine($"{name} LatestBlock: {latest}");
                    pivot = (number, hash, difficulty);
                    break;
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested && exception is HttpRequestException or OperationCanceledException or JsonException or FormatException or OverflowException or InvalidOperationException or ArgumentOutOfRangeException)
                {
                    Console.WriteLine($"{name} attempt {attempt}/5 failed: {exception.GetType().Name}");
                    if (attempt < 5) await delay(TimeSpan.FromSeconds(10 * attempt), cancellationToken);
                }
            }
            if (pivot is not { } value)
            {
                Console.WriteLine($"::error::{name}: could not fetch the pivot from {url}, config left unchanged");
                failed.Add(name);
                continue;
            }
            var updated = new Dictionary<string, JsonNode>();
            foreach (var target in name == "mainnet" ? new[] { name, "mainnet_aztec" } : [name])
            {
                Console.WriteLine($"{target} PivotNumber: {value.Number}\n{target} PivotHash: {value.Hash}");
                if (!pos) Console.WriteLine($"{target} PivotTotalDifficulty: {value.Difficulty}");
                var config = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, target + ".json"), cancellationToken))!;
                var sync = config["Sync"] ?? throw new JsonException($"Missing Sync section in {target}");
                sync["PivotNumber"] = value.Number;
                sync["PivotHash"] = value.Hash;
                if (!pos) sync["PivotTotalDifficulty"] = value.Difficulty.ToString(CultureInfo.InvariantCulture);
                updated[target] = config;
            }
            // Leader and follower configs are written together; cancelling between or during writes would split them.
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (target, config) in updated)
                await File.WriteAllTextAsync(Path.Combine(directory, target + ".json"), config.ToJsonString(indentedJson));

            async Task<JsonNode?> Request(string method, string? tag)
            {
                using var request = new HttpRequestMessage();
                if (url.Contains("etherscan", StringComparison.Ordinal))
                    request.RequestUri = new Uri(url + "&module=proxy&action=" + method + "&apikey=" + Uri.EscapeDataString(key) + (tag is null ? "" : "&tag=" + tag + "&boolean=true"));
                else
                {
                    request.Method = HttpMethod.Post;
                    request.RequestUri = new Uri(url);
                    var body = new JsonObject { ["id"] = 0, ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = tag is null ? [] : new JsonArray(JsonValue.Create(tag), JsonValue.Create(false)) };
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                }
                using var response = await http.SendAsync(request, cancellationToken);
                return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))?["result"];
            }
        }
        if (failed.Count == 0) return 0;
        Console.Error.WriteLine("Failed to update: " + string.Join(", ", failed));
        return 1;
    }
}
