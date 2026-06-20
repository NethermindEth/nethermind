// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Nethermind.RpcTests.Monitor;

internal class HeadMonitor(Uri nodeUrl, ErrorReporter errorReporter)
{
    private const string SubscribeRequest =
        """{"jsonrpc":"2.0","id":1,"method":"eth_subscribe","params":["newHeads"]}""";

    public static Uri DeriveWsUrl(Uri httpUrl) =>
        new UriBuilder(httpUrl) { Scheme = httpUrl.Scheme == "https" ? "wss" : "ws" }.Uri;

    public async IAsyncEnumerable<BlockInfo> SubscribeAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        Channel<BlockInfo> channel = Channel.CreateUnbounded<BlockInfo>();
        _ = ProduceWithRetryAsync(channel.Writer, ct);

        await foreach (BlockInfo head in channel.Reader.ReadAllAsync(ct))
            yield return head;
    }

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5)
    ];

    private async Task ProduceWithRetryAsync(ChannelWriter<BlockInfo> writer, CancellationToken ct)
    {
        int failureCount = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndProduceAsync(writer, ct);
                    failureCount = 0;

                    if (!ct.IsCancellationRequested)
                        Console.Error.WriteLine("WebSocket disconnected, reconnecting...");

                    // reached in case of a clean disconnect
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    continue;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // reduce disconnect spamming in case if node is down/restarting
                    if (failureCount >= RetryDelays.Length)
                        errorReporter.Report("WebSocket connection error", ex);
                }

                TimeSpan delay = RetryDelays[Math.Min(failureCount, RetryDelays.Length - 1)];
                failureCount++;

                await Task.Delay(delay, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
        finally
        {
            writer.Complete();
        }
    }

    private async Task ConnectAndProduceAsync(ChannelWriter<BlockInfo> writer, CancellationToken ct)
    {
        using ClientWebSocket ws = new();
        await ws.ConnectAsync(DeriveWsUrl(nodeUrl), ct);
        await ws.SendAsync(Encoding.UTF8.GetBytes(SubscribeRequest), WebSocketMessageType.Text, true, ct);

        using MemoryStream ms = new();
        byte[] buffer = new byte[64 * 1024];
        while (!ct.IsCancellationRequested)
        {
            JsonNode? message = await ReadMessageAsync(ws, buffer, ms, ct);
            if (message is null) return;

            BlockInfo? head = TryParseHead(message);
            if (head is not null)
                await writer.WriteAsync(head, ct);
        }
    }

    private BlockInfo? TryParseHead(JsonNode message)
    {
        try
        {
            if (message["error"] is { } error) throw new Exception(error.ToCompactString());
            return message["params"]?["result"] is { } result ? new BlockInfo(result) : null;
        }
        catch (Exception exception)
        {
            errorReporter.Report("Error parsing block info", exception);
            return null;
        }
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
