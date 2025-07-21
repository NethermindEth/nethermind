// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

namespace Nethermind.Tools.Kute.MessageProvider;

public sealed class UnwrapBatchJsonRpcMessageProvider : IMessageProvider<JsonRpc>
{
    private readonly IMessageProvider<JsonRpc> _provider;

    public UnwrapBatchJsonRpcMessageProvider(IMessageProvider<JsonRpc> provider)
    {
        _provider = provider;
    }

    public async IAsyncEnumerable<JsonRpc> Messages([EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var jsonRpc in _provider.Messages(token))
        {
            switch (jsonRpc)
            {
                case JsonRpc.Request.Single:
                    yield return jsonRpc;
                    break;
                case JsonRpc.Request.Batch batch:
                    foreach (var single in batch.Items())
                    {
                        if (single is not null)
                        {
                            yield return single;
                        }
                        else
                        {
                            // TODO: Log potential error
                        }
                    }
                    break;
            }
        }
    }
}
