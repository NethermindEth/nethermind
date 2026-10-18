// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc.Modules.Subscribe;

/// <summary>
/// Serializes a pending transaction once for every newPendingTransactions subscriber of a factory that asked for full
/// transactions, instead of building and serializing it once per subscriber.
/// </summary>
/// <remarks>
/// Subscribers drain their queues at their own pace, so the most recent transactions are kept in a small table. A subscriber far enough behind for its slot to be reused serializes the transaction itself.
/// </remarks>
internal sealed class PendingTransactionPayloadCache(ISpecProvider specProvider)
{
    private const int Size = 256;
    private readonly Entry?[] _entries = new Entry?[Size];

    public SerializedJson Get(Transaction transaction)
    {
        // By reference: entries match by reference too, and a pending transaction may not have its hash yet.
        ref Entry? slot = ref _entries[RuntimeHelpers.GetHashCode(transaction) & (Size - 1)];
        Entry? entry = Volatile.Read(ref slot);
        if (entry is null || !ReferenceEquals(entry.Transaction, transaction))
        {
            TransactionForRpc payload = TransactionForRpc.FromTransaction(transaction, new(specProvider.ChainId));
            entry = new Entry(transaction, new SerializedJson(JsonRpcResponseWriter.SerializePayload(payload, EthereumJsonSerializer.JsonOptions)));
            Volatile.Write(ref slot, entry);
        }

        return entry.Json;
    }

    private sealed record Entry(Transaction Transaction, SerializedJson Json);
}
