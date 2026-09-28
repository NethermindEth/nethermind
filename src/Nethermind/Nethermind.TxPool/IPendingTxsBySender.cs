// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// Pending transactions of a single sender, for consumers that need no other part of <see cref="ITxPool"/>.
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
}
