// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Tools.Kute.MessageProvider;

public sealed class UnwrapBatchJsonRpcMessageProvider : IMessageProvider<JsonRpc>
{
    private readonly IMessageProvider<JsonRpc> _provider;

    public UnwrapBatchJsonRpcMessageProvider(IMessageProvider<JsonRpc> provider)
    {
        _provider = provider;
    }

    public IAsyncEnumerable<JsonRpc> Messages { get => MessagesImpl(); }

    private async IAsyncEnumerable<JsonRpc> MessagesImpl()
    {
        await foreach (var jsonRpc in _provider.Messages)
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
