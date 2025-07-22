// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Nethermind.Tools.Kute.MessageProvider;

public sealed class JsonRpcMessageProvider : IMessageProvider<JsonRpc>
{
    private readonly IMessageProvider<string> _provider;

    public JsonRpcMessageProvider(IMessageProvider<string> provider)
    {
        _provider = provider;
    }

    public async IAsyncEnumerable<JsonRpc> Messages([EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var msg in _provider.Messages(token))
        {
            var jsonDoc = JsonSerializer.Deserialize<JsonDocument>(msg);

            switch (jsonDoc?.RootElement.ValueKind)
            {
                case JsonValueKind.Object:
                    {
                        var isResponse = jsonDoc.RootElement.TryGetProperty("response", out _);
                        if (isResponse)
                        {
                            yield return new JsonRpc.Response(jsonDoc);
                        }
                        else
                        {
                            yield return new JsonRpc.Request.Single(jsonDoc);
                        }
                    }
                    break;
                case JsonValueKind.Array:
                    yield return new JsonRpc.Request.Batch(jsonDoc);
                    break;
                default:
                    // TODO: Log potential error
                    break;
            }
        }
    }
}
