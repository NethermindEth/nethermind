// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Caching;
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
/// Subscribers drain their queues at their own pace, so the most recent transactions are kept in a small lock-free
/// cache keyed by hash. A subscriber far enough behind for its entry to be evicted serializes the transaction itself.
/// Entries hold only the JSON, never the transaction, so blob sidecars are not kept alive. The cache is used only while
/// a subscriber that reads it exists, and is replaced by an empty one when the last one leaves, so a send loop that
/// read the old one before that and stores into it after does not keep its entry.
/// </remarks>
internal sealed class PendingTransactionPayloadCache(ISpecProvider specProvider)
{
    private const int Size = 256;
    private AssociativeCache<ValueHash256, byte[]> _payloads = new(Size);
    private int _subscribers;

    internal AssociativeCache<ValueHash256, byte[]> Generation => Volatile.Read(ref _payloads);

    public void AddSubscriber() => Interlocked.Increment(ref _subscribers);

    public void RemoveSubscriber()
    {
        if (Interlocked.Decrement(ref _subscribers) == 0) Volatile.Write(ref _payloads, new(Size));
    }

    public SerializedJson Get(Transaction transaction)
    {
        Hash256? hash = transaction.Hash;
        if (hash is null) return Serialize(transaction);

        // Read before the subscriber count: a last unsubscribe after this replaces it, so a late store is dropped with it.
        AssociativeCache<ValueHash256, byte[]> payloads = Generation;
        if (payloads.TryGet(hash.ValueHash256, out byte[]? cached) && cached is not null) return new SerializedJson(cached);

        SerializedJson json = Serialize(transaction);
        // A queue still draining after its subscriber left must not refill a cache nobody clears.
        if (Volatile.Read(ref _subscribers) > 0) payloads.Set(hash.ValueHash256, json.Utf8Json);
        return json;
    }

    private SerializedJson Serialize(Transaction transaction) =>
        new(JsonRpcResponseWriter.SerializePayload(TransactionForRpc.FromTransaction(transaction, new(specProvider.ChainId)), EthereumJsonSerializer.JsonOptions));
}
