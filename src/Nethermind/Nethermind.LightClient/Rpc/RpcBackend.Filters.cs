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

    internal async Task<SerializedJson?> GetLogChangesAsync(LogQuery query, long startedAt, RpcFilters.HeadChange[] changes,
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
                List<BlockHeader> removed = [];
                foreach (RpcFilters.LogBlock block in change.Removed)
                    if (block.AddedAt > startedAt && query.Matches(block.Header)) removed.Add(block.Header);
                if (removed.Count != 0) await AppendAsync([.. removed], removed: true, writer);

                List<BlockHeader> added = [];
                foreach (BlockHeader header in change.AddedLogHeaders)
                    if (query.Matches(header)) added.Add(header);
                if (added.Count != 0) await AppendAsync([.. added], removed: false, writer);
            }
            writer.WriteEndArray();
        }
        catch (RpcException exception) when (!webSocket && exception.Code == ErrorCodes.LimitExceeded)
        {
            // Admission is outside this scope. An already oversized change cannot shrink on retry.
            return null;
        }
        return new SerializedJson(output.WrittenMemory.ToArray());

        async ValueTask AppendAsync(BlockHeader[] headers, bool removed, Utf8JsonWriter writer)
        {
            SerializedJson logs = await data.GetLogsAsync(headers, query.Addresses, query.Topics, token, removed: removed);
            Utf8JsonReader values = new(logs.Utf8Json);
            values.Read();
            while (values.Read() && values.TokenType != JsonTokenType.EndArray)
            {
                int start = (int)values.TokenStartIndex;
                values.Skip();
                writer.WriteRawValue(logs.Utf8Json.AsSpan(start, (int)values.BytesConsumed - start), skipInputValidation: true);
            }
        }
    }
}
