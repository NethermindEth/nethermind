// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.TxPool;

/// <summary>
/// Pending transactions of a single sender and pool membership by hash, for consumers that need no other part of
/// <see cref="ITxPool"/>.
/// </summary>
public interface IPendingTxsBySender
{
    /// <summary>
    /// from a specific sender, sorted by nonce and later tx pool sorting
    /// </summary>
    /// <returns></returns>
    Transaction[] GetPendingTransactionsBySender(Address address);

    /// <summary>
    /// Blob txs light equivalences from a specific sender, sorted by nonce.
    /// </summary>
    Transaction[] GetPendingLightBlobTransactionsBySender(Address address);

    bool ContainsTx(Hash256 hash, TxType txType);

    /// <summary>
    /// A count that changes whenever a transaction of <paramref name="sender"/> stops being reported by
    /// <see cref="ContainsTx"/>, <see cref="GetPendingTransactionsBySender"/> or
    /// <see cref="GetPendingLightBlobTransactionsBySender"/>.
    /// </summary>
    /// <remarks>
    /// It may also change for removals of other senders' transactions, so an unchanged value proves no removal of
    /// this sender while a changed one proves nothing. A reader that sees the changed value finds the removal
    /// reflected by the queries it makes afterwards.
    /// </remarks>
    long GetRemovalGeneration(Address sender);
}
