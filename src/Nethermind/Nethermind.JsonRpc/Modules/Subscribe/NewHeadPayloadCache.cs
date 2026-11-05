// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
        while (entry is null || !ReferenceEquals(entry.Block, block))
        {
            if (entry is not null && entry.Block.Number > block.Number)
            {
                return Serialize(block, includeTransactions);
            }

            // Published before serializing, so subscribers missing at once wait for one serialization.
            Entry candidate = new(block, new Lazy<SerializedJson>(() => Serialize(block, includeTransactions)));
            Entry? current = Interlocked.CompareExchange(ref slot, candidate, entry);
            entry = ReferenceEquals(current, entry) ? candidate : current;
        }

        return entry.Json.Value;
    }

    private SerializedJson Serialize(Block block, bool includeTransactions)
    {
        BlockForRpc payload = blockForRpcFactory.Create(block, includeTransactions, specProvider);
        return new SerializedJson(JsonRpcResponseWriter.SerializePayload(payload, EthereumJsonSerializer.JsonOptions));
    }

    private sealed record Entry(Block Block, Lazy<SerializedJson> Json);
}
