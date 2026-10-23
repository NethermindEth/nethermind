// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class BenchmarkRpc
{
    internal static async Task<string?> HeadAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        try
        {
            using var body = new StringContent("{\"jsonrpc\":\"2.0\",\"method\":\"eth_blockNumber\",\"params\":[],\"id\":1}", Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(url, body, cancellationToken);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    internal static async Task WaitAsync(string url, int timeout, string container, CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        BenchmarkProcess.Log($"Waiting for JSON-RPC at {url} (timeout {timeout}s)...");
        var elapsed = 0;
        while (true)
        {
            var listing = await BenchmarkProcess.RunAsync("docker", ["ps", "--format", "{{.Names}}"], cancellationToken);
            if (!listing.Output.Split('\n').Select(line => line.TrimEnd('\r')).Contains(container, StringComparer.Ordinal))
            {
                var logs = await BenchmarkProcess.RunAsync("docker", ["logs", container], cancellationToken);
                Console.Error.WriteLine(string.Join('\n', (logs.Output + logs.Error).Split('\n').TakeLast(100)));
                throw new IOException("node container died before serving JSON-RPC");
            }
            var head = await HeadAsync(client, url, cancellationToken);
            if (head is not null && Regex.IsMatch(head, "^0x[0-9a-fA-F]{1,15}$"))
            {
                var number = long.Parse(head[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                if (number == 0) throw new IOException("node reports head block 0 — datadir mismatch; refusing to benchmark genesis");
                BenchmarkProcess.Log($"JSON-RPC is up. Head block: {number} ({head})"); return;
            }
            await Task.Delay(5000, cancellationToken); elapsed += 5;
            if (elapsed >= timeout) throw new IOException($"JSON-RPC did not become ready within {timeout}s");
            if (elapsed % 30 == 0) BenchmarkProcess.Log($"  still waiting for RPC... ({elapsed}/{timeout}s)");
        }
    }
}
