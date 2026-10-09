// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Subscribe;

/// <summary>
/// Serializes the newest head once per transactions option for every newHeads subscriber of a factory, instead of
/// building and serializing the block once per subscriber. A subscriber still on an older head serializes its own.
/// </summary>
internal sealed class NewHeadPayloadCache(ISpecProvider specProvider, IBlockForRpcFactory blockForRpcFactory)
{
    private Entry? _withTransactions;
    private Entry? _withoutTransactions;

    public SerializedJson Get(Block block, bool includeTransactions)
    {
        ref Entry? slot = ref includeTransactions ? ref _withTransactions : ref _withoutTransactions;
        Entry? entry = Volatile.Read(ref slot);
        if (entry is null || !ReferenceEquals(entry.Block, block))
        {
            BlockForRpc payload = blockForRpcFactory.Create(block, includeTransactions, specProvider);
            entry = new Entry(block, new SerializedJson(JsonRpcResponseWriter.SerializePayload(payload, EthereumJsonSerializer.JsonOptions)));
            Volatile.Write(ref slot, entry);
        }

        return entry.Json;
    }

    private sealed record Entry(Block Block, SerializedJson Json);
}
