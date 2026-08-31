// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public readonly record struct TxLookupKey(ValueHash256 Hash, Address Sender, UInt256 Timestamp);

public interface ITxStorage
{
    bool TryGet(in ValueHash256 hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out Transaction? transaction);
    int TryGetMany(TxLookupKey[] keys, int count, Transaction?[] results);
    IEnumerable<LightTransaction> GetAll();
    void Add(Transaction transaction);
    void Delete(in ValueHash256 hash, in UInt256 timestamp);
}

internal interface IBatchDeleteTxStorage
{
    /// <summary>
    /// Removes the timestamped full-body record and the hash-keyed light and elided records for each key.
    /// </summary>
    void DeleteMany(scoped ReadOnlySpan<TxLookupKey> keys);

    /// <summary>
    /// Removes only the timestamped full-body record for each key, leaving the hash-keyed light and elided
    /// records intact.
    /// </summary>
    /// <remarks>
    /// Used to drop an obsolete body when the same hash is live under a different <see cref="TxLookupKey.Timestamp"/>;
    /// use <see cref="DeleteMany"/> when no current transaction owns the hash-keyed records.
    /// </remarks>
    void DeleteFullBlobTransactions(scoped ReadOnlySpan<TxLookupKey> keys);

    /// <summary>
    /// Removes timestamped full-body records that are not referenced by their hash-keyed light record.
    /// </summary>
    /// <remarks>
    /// This can scan the entire persisted full-transaction collection and should run outside latency-sensitive paths.
    /// </remarks>
    /// <param name="cancellationToken">
    /// Aborts the scan. Implementations must throw rather than return early so an interrupted sweep is not treated as complete.
    /// </param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    void DeleteObsoleteFullBlobTransactions(CancellationToken cancellationToken);
}

internal interface ISpecChangeValidationStorage
{
    string? GetSpecChangeValidationMarker();
    void SetSpecChangeValidationMarker(string? marker);
}
