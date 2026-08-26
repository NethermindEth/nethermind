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
/// default value is an empty view that claims nothing.
/// </remarks>
public readonly struct PendingTransactionsView(
    IDictionary<AddressAsKey, Transaction[]> transactions,
    IDictionary<AddressAsKey, Transaction[]> blobTransactions,
    bool isRevalidated)
{
    private static readonly IDictionary<AddressAsKey, Transaction[]> _empty =
        new ReadOnlyDictionary<AddressAsKey, Transaction[]>(new Dictionary<AddressAsKey, Transaction[]>());

    private readonly IDictionary<AddressAsKey, Transaction[]>? _transactions = transactions;
    private readonly IDictionary<AddressAsKey, Transaction[]>? _blobTransactions = blobTransactions;

    /// <summary>Non-blob transactions grouped by sender address, sorted by nonce and later tx pool sorting.</summary>
    public IDictionary<AddressAsKey, Transaction[]> Transactions => _transactions ?? _empty;

    /// <summary>Blob transaction light equivalences grouped by sender address, sorted the same way.</summary>
    public IDictionary<AddressAsKey, Transaction[]> BlobTransactions => _blobTransactions ?? _empty;

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
