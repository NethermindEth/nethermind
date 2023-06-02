// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.MessageProvider;

public class JsonMessageProvider : IMessageProvider<JsonElement?>
{
    private readonly IMessageProvider<string> _provider;

    public JsonMessageProvider(IMessageProvider<string> provider)
    {
        _provider = provider;
    }

    public IAsyncEnumerable<JsonElement?> Messages { get => MessagesImpl(); }

    private async IAsyncEnumerable<JsonElement?> MessagesImpl()
    {
        await foreach (var msg in _provider.Messages)
        {
            var jsonDoc = JsonSerializer.Deserialize<JsonDocument>(msg);
            if (jsonDoc == null)
            {
                yield return null;
                continue;
            }

            switch (jsonDoc.RootElement.ValueKind)
            {
                case JsonValueKind.Object:
                    yield return jsonDoc.RootElement;
                    break;
                case JsonValueKind.Array:
                    foreach (var rpc in jsonDoc.RootElement.EnumerateArray())
                    {
                        yield return rpc;
                    }

                    break;
            }
        }
    }
}
