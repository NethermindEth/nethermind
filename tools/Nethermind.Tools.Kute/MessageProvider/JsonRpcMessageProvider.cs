// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.MessageProvider;

public class JsonRpcMessageProvider : IMessageProvider<JsonRpc?>
{
    private readonly IMessageProvider<string> _provider;
    private readonly bool _unwrapBatches;


    public JsonRpcMessageProvider(IMessageProvider<string> provider, bool unwrapBatches)
    {
        _provider = provider;
        _unwrapBatches = unwrapBatches;
    }


    public IAsyncEnumerable<JsonRpc?> Messages { get => MessagesImpl(); }

    private async IAsyncEnumerable<JsonRpc?> MessagesImpl()
    {
        await foreach (var msg in _provider.Messages)
        {
            var jsonDoc = JsonSerializer.Deserialize<JsonDocument>(msg);

            switch (jsonDoc?.RootElement.ValueKind)
            {
                case JsonValueKind.Object:
                    yield return new JsonRpc.SingleJsonRpc(jsonDoc);
                    break;
                case JsonValueKind.Array:
                    if (_unwrapBatches)
                    {
                        foreach (JsonElement single in jsonDoc.RootElement.EnumerateArray())
                        {
                            yield return new JsonRpc.SingleJsonRpc(JsonDocument.Parse(single.ToString()));
                        }
                    }
                    yield return new JsonRpc.BatchJsonRpc(jsonDoc);
                    break;
                default:
                    yield return null;
                    break;

            }
        }
    }
}
