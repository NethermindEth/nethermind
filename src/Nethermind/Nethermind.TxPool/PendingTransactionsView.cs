// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Collections.ObjectModel;
using Nethermind.Core;

namespace Nethermind.TxPool;

/// <summary>
/// A consistent view of the pending transactions to build a block from.
/// </summary>
/// <remarks>
/// The transactions and <see cref="IsRevalidated"/> are read as one, so nothing accepted or evicted while the
/// producer works can make the flag describe a different set of transactions than the one handed out. The
/// default value is an empty view that claims nothing. Bucket arrays may be shared with later views and must not be modified.
/// </remarks>
public readonly struct PendingTransactionsView(
    IDictionary<AddressAsKey, MempoolTransaction[]> transactions,
    IDictionary<AddressAsKey, MempoolTransaction[]> blobTransactions,
    bool isRevalidated)
{
    private static readonly IReadOnlyDictionary<AddressAsKey, MempoolTransaction[]> _empty =
        new ReadOnlyDictionary<AddressAsKey, MempoolTransaction[]>(new Dictionary<AddressAsKey, MempoolTransaction[]>());

    private readonly IReadOnlyDictionary<AddressAsKey, MempoolTransaction[]>? _transactions =
        transactions as IReadOnlyDictionary<AddressAsKey, MempoolTransaction[]>
        ?? new ReadOnlyDictionary<AddressAsKey, MempoolTransaction[]>(transactions);
    private readonly IReadOnlyDictionary<AddressAsKey, MempoolTransaction[]>? _blobTransactions =
        blobTransactions as IReadOnlyDictionary<AddressAsKey, MempoolTransaction[]>
        ?? new ReadOnlyDictionary<AddressAsKey, MempoolTransaction[]>(blobTransactions);

    /// <summary>Non-blob transactions grouped by sender address, sorted by nonce and later tx pool sorting.</summary>
    public IReadOnlyDictionary<AddressAsKey, MempoolTransaction[]> Transactions => _transactions ?? _empty;

    /// <summary>Blob transaction light equivalences grouped by sender address, sorted the same way.</summary>
    public IReadOnlyDictionary<AddressAsKey, MempoolTransaction[]> BlobTransactions => _blobTransactions ?? _empty;

    /// <summary>
    /// Whether every transaction in this view has already been validated against the target block's release
    /// specification.
    /// </summary>
    /// <remarks>
    /// <see langword="false"/> is always safe: the caller then applies the specification-dependent checks to
    /// each transaction itself.
    /// </remarks>
    public bool IsRevalidated { get; } = isRevalidated;
}
