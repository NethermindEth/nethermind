// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;
using Nethermind.Core;
using Nethermind.JsonRpc;

namespace Nethermind.LightClient.Rpc;

internal sealed partial class RpcBackend
{
    // Reserve room for the largest WebSocket request ID and response envelope.
    private const int WebSocketFilterResultLimit = RpcSubscriptions.MaxMessageBytes - RpcEndpoint.StandardRequestBodySize;

    internal async Task<JsonElement?> GetLogChangesAsync(LogQuery query, long startedAt, RpcFilters.HeadChange[] changes,
        CancellationToken token, bool webSocket)
    {
        ExecutionData data = _data ?? throw new RpcException(ErrorCodes.MethodNotFound, "Verified execution data is unavailable.");
        using DataRequest admission = new(token);
        int resultLimit = webSocket ? WebSocketFilterResultLimit : BoundedJson.ResultLimit;
        using BoundedJsonBuffer output = new(resultLimit, 6 * WebSocketFilterResultLimit,
            limitMessage: webSocket ? "Polling filter changes exceed the WebSocket response limit; poll this filter over HTTP." : null);
        try
        {
            using Utf8JsonWriter writer = new(output);
            writer.WriteStartArray();
            foreach (RpcFilters.HeadChange change in changes)
            {
                BlockHeader[] removed = [.. change.Removed.Where(block => block.AddedAt > startedAt && query.Matches(block.Header))
                    .Select(block => block.Header)];
                BlockHeader[] added = [.. change.AddedLogHeaders.Where(query.Matches)];
                await AppendAsync(removed, removed: true, writer);
                await AppendAsync(added, removed: false, writer);
            }
            writer.WriteEndArray();
        }
        catch (RpcException exception) when (!webSocket && exception.Code == ErrorCodes.LimitExceeded)
        {
            // Admission is outside this scope. An already oversized change cannot shrink on retry.
            return null;
        }
        using JsonDocument document = JsonDocument.Parse(output.WrittenMemory);
        return document.RootElement.Clone();

        async ValueTask AppendAsync(BlockHeader[] headers, bool removed, Utf8JsonWriter writer)
        {
            if (headers.Length == 0) return;
            JsonElement logs = await data.GetLogsAsync(headers, query.Addresses, query.Topics, token, removed: removed);
            foreach (JsonElement log in logs.EnumerateArray()) log.WriteTo(writer);
        }
    }
}
