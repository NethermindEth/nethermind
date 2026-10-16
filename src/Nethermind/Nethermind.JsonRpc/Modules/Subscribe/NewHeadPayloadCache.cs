// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Subscribe;

/// <summary>
/// Serializes the newest head once per payload shape for every newHeads subscriber of a factory, instead of
/// building and serializing the block once per subscriber. A subscriber still on an older head serializes its own.
/// </summary>
internal sealed class NewHeadPayloadCache(ISpecProvider specProvider, IBlockForRpcFactory blockForRpcFactory)
{
    private Entry? _withTransactions;
    private Entry? _withoutTransactions;
    private Entry? _headerOnly;

    /// <param name="block">The head to announce.</param>
    /// <param name="includeTransactions">Full transactions; takes precedence over <paramref name="headerOnly"/>.</param>
    /// <param name="headerOnly">The header alone, as geth sends it: no transaction hashes, withdrawals or uncles.</param>
    public SerializedJson Get(Block block, bool includeTransactions, bool headerOnly = false)
    {
        headerOnly &= !includeTransactions;
        ref Entry? slot = ref Slot(includeTransactions, headerOnly);
        Entry? entry = Volatile.Read(ref slot);
        if (entry is null || !ReferenceEquals(entry.Block, block))
        {
            byte[] json = headerOnly
                ? JsonRpcResponseWriter.SerializePayload(blockForRpcFactory.CreateHeader(block.Header, specProvider), EthereumJsonSerializer.JsonOptions)
                : JsonRpcResponseWriter.SerializePayload(blockForRpcFactory.Create(block, includeTransactions, specProvider), EthereumJsonSerializer.JsonOptions);
            entry = new Entry(block, new SerializedJson(json));
            Volatile.Write(ref slot, entry);
        }

        return entry.Json;
    }

    private ref Entry? Slot(bool includeTransactions, bool headerOnly)
    {
        if (includeTransactions) return ref _withTransactions;
        if (headerOnly) return ref _headerOnly;
        return ref _withoutTransactions;
    }

    private sealed record Entry(Block Block, SerializedJson Json);
}
