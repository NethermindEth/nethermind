// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.MessageProvider;

public class JsonRpcMessageProvider : IMessageProvider<JsonRpc?>
{
    private readonly IMessageProvider<string> _provider;


    public JsonRpcMessageProvider(IMessageProvider<string> provider)
    {
        _provider = provider;
    }

    public IAsyncEnumerable<JsonRpc?> Messages { get => MessagesImpl(); }

    private async IAsyncEnumerable<JsonRpc?> MessagesImpl()
    {
        await foreach (var msg in _provider.Messages)
        {
            var jsonDoc = JsonSerializer.Deserialize<JsonDocument>(msg);

            yield return jsonDoc?.RootElement.ValueKind switch
            {
                JsonValueKind.Object => new JsonRpc.SingleJsonRpc(jsonDoc),
                JsonValueKind.Array => new JsonRpc.BatchJsonRpc(jsonDoc),
                _ => null
            };
        }
    }
}
