// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Subscribe;

/// <summary>
/// Serializes a pending transaction once for every newPendingTransactions subscriber of a factory that asked for full
/// transactions, instead of building and serializing it once per subscriber.
/// </summary>
/// <remarks>
/// Subscribers drain their queues at their own pace, so the most recent transactions are kept in a small table keyed
/// by hash. A subscriber far enough behind for its slot to be reused serializes the transaction itself. Entries hold
/// only the hash and the JSON, never the transaction, so blob sidecars are not kept alive. The table is used only
/// while a subscriber that reads it exists, and is cleared when the last one leaves.
/// </remarks>
internal sealed class PendingTransactionPayloadCache(ISpecProvider specProvider)
{
    private const int Size = 256;
    private readonly Entry?[] _entries = new Entry?[Size];
    private int _subscribers;

    public void AddSubscriber() => Interlocked.Increment(ref _subscribers);

    public void RemoveSubscriber()
    {
        if (Interlocked.Decrement(ref _subscribers) == 0) Array.Clear(_entries);
    }

    public SerializedJson Get(Transaction transaction)
    {
        Hash256? hash = transaction.Hash;
        if (hash is null) return Serialize(transaction);

        ref Entry? slot = ref _entries[hash.GetHashCode() & (Size - 1)];
        Entry? entry = Volatile.Read(ref slot);
        if (entry is not null && entry.Hash == hash) return entry.Json;

        SerializedJson json = Serialize(transaction);
        // A queue still draining after its subscriber left must not refill a table nobody clears.
        if (Volatile.Read(ref _subscribers) > 0) Volatile.Write(ref slot, new Entry(hash, json));
        return json;
    }

    private SerializedJson Serialize(Transaction transaction) =>
        new(JsonRpcResponseWriter.SerializePayload(TransactionForRpc.FromTransaction(transaction, new(specProvider.ChainId)), EthereumJsonSerializer.JsonOptions));

    private sealed record Entry(Hash256 Hash, SerializedJson Json);
}
