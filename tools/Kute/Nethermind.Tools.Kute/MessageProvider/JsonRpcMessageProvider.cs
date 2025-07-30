// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

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
            var node = JsonNode.Parse(msg);

            switch (node)
            {
                case JsonObject obj:
                    {
                        var isResponse = obj["response"] is not null;
                        if (isResponse)
                        {
                            yield return new JsonRpc.Response(node);
                        }
                        else
                        {
                            yield return new JsonRpc.Request.Single(node);
                        }
                    }
                    break;
                case JsonArray:
                    yield return new JsonRpc.Request.Batch(node);
                    break;
                default:
                    // TODO: Log potential error
                    break;
            }
        }
    }
}
