// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool;

public readonly record struct TxLookupKey(ValueHash256 Hash, Address Sender, UInt256 Timestamp);

internal readonly record struct BlobTxDeleteKey(ValueHash256 Hash, UInt256 Timestamp);

public interface ITxStorage
{
    bool TryGet(in ValueHash256 hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out Transaction? transaction);
    int TryGetMany(TxLookupKey[] keys, int count, Transaction?[] results);
    IEnumerable<LightTransaction> GetAll();
    void Add(Transaction transaction);
    void Delete(in ValueHash256 hash, in UInt256 timestamp);
}

internal interface IAtomicBlobTxStorage
{
    /// <summary>
    /// Atomically removes the timestamped full-body record and the hash-keyed light and elided records for each key.
    /// </summary>
    void DeleteMany(scoped ReadOnlySpan<BlobTxDeleteKey> keys);

    /// <summary>
    /// Atomically writes <paramref name="transaction"/> and removes obsolete full bodies for the same hash.
    /// </summary>
    void Replace(Transaction transaction, scoped ReadOnlySpan<UInt256> obsoleteTimestamps);
}

/// <summary>Identifies one processed blob transaction row by block number and stored position.</summary>
/// <param name="BlockNumber">The number of the block the row was saved for.</param>
/// <param name="Position">The stored position among the pool-held blob transactions saved for that block, not the transaction's index in the block.</param>
internal readonly record struct ProcessedBlobTxKey(ulong BlockNumber, int Position);

/// <summary>
/// Reads single rows of the processed blob transactions saved by <see cref="IBlobTxStorage.AddBlobTransactionsFromBlock"/>.
/// </summary>
/// <remarks>
/// Reads are lock-free: they never wait for a block's blob transactions being saved or read back, so they are safe on
/// paths that must not stall the head loop. An absent row is a normal miss, not an error, because finalization cleanup
/// deletes rows asynchronously. Rows in the legacy whole-block layout are not served and read as misses.
/// </remarks>
internal interface IProcessedBlobTxStorage
{
    /// <summary>Reads one processed blob transaction row.</summary>
    /// <param name="blockNumber">The number of the block the row was saved for.</param>
    /// <param name="position">The stored position among the pool-held blob transactions saved for that block, not the transaction's index in the block.</param>
    /// <param name="transaction">The decoded transaction, or <see langword="null"/> on a miss.</param>
    /// <returns><see langword="true"/> if the row exists and decodes; <see langword="false"/> on a miss.</returns>
    bool TryGetProcessedTransaction(ulong blockNumber, int position, [NotNullWhen(true)] out Transaction? transaction);

    /// <summary>Reads several processed blob transaction rows.</summary>
    /// <param name="keys">The rows to read.</param>
    /// <param name="results">Receives the decoded transaction for each key, or <see langword="null"/> on a miss. Must be at least as long as <paramref name="keys"/>.</param>
    /// <returns>The number of rows found.</returns>
    int TryGetProcessedTransactions(scoped ReadOnlySpan<ProcessedBlobTxKey> keys, scoped Span<Transaction?> results);
}

internal interface ISpecChangeValidationStorage
{
    string? GetSpecChangeValidationMarker();
    void SetSpecChangeValidationMarker(string? marker);
}
