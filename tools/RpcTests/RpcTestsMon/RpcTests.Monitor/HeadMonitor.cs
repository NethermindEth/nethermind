// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Nethermind.RpcTests.Monitor.Notifiers;

namespace Nethermind.RpcTests.Monitor;

internal record HeadInfo(long Number, string Hash) : IFormattable
{
    public override string ToString() => $"{Number} ({Hash})";

    public string ToString(string? format, IFormatProvider? formatProvider) => format?.Equals("#") == true
        ? Number.ToString()
        : ToString();
}

internal class HeadMonitor(Uri nodeUrl, INotifier notifier)
{
    private const string SubscribeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"eth_subscribe","params":["newHeads"]}""";

    public static Uri DeriveWsUrl(Uri httpUrl) =>
        new UriBuilder(httpUrl) { Scheme = httpUrl.Scheme == "https" ? "wss" : "ws" }.Uri;

    // TODO: add automatic reconnection with exponential backoff
    public async IAsyncEnumerable<HeadInfo> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        using ClientWebSocket ws = new();
        await ws.ConnectAsync(DeriveWsUrl(nodeUrl), ct);
        await ws.SendAsync(Encoding.UTF8.GetBytes(SubscribeRequest), WebSocketMessageType.Text, true, ct);

        using MemoryStream ms = new();
        byte[] buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            JsonNode? message = await ReadMessageAsync(ws, buffer, ms, ct);
            if (message is null)
                yield break;

            HeadInfo? head = ParseHead(message);
            if (head is not null)
                yield return head;
        }
    }

    private HeadInfo? ParseHead(JsonNode message)
    {
        if (message["error"] is { } error)
        {
            string errorMsg = $"WebSocket error: {error}";
            Console.Error.WriteLine(errorMsg);
            _ = notifier.NotifyErrorAsync(errorMsg);
        }

        JsonNode? result = message["params"]?["result"];
        if (result is null) return null;

        string? numberHex = result["number"]?.GetValue<string>();
        string? hash = result["hash"]?.GetValue<string>();
        if (numberHex is null || hash is null) return null;

        // TODO: forward more info
        return new HeadInfo(Convert.ToInt64(numberHex, 16), hash);
    }

    private static async Task<JsonNode?> ReadMessageAsync(ClientWebSocket ws, byte[] buffer, MemoryStream ms, CancellationToken ct)
    {
        ms.SetLength(0);
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        ms.Position = 0;
        return await JsonNode.ParseAsync(ms, cancellationToken: ct);
    }
}
