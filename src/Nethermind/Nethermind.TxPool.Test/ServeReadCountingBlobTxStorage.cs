// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.TxPool.Test;

/// <summary>Counts the reads a <see cref="BlobTxStorage"/> is asked for, so a test can assert that a pool answered
/// from its caches rather than infer it from timing or allocation.</summary>
/// <remarks>Forwards every other member unchanged, including the internal capabilities
/// <see cref="PersistentBlobTxDistinctSortedPool"/> probes for with <c>as</c>: dropping either of those would
/// silently move the pool onto a different write path than production takes.</remarks>
internal sealed class CountingBlobTxStorage(BlobTxStorage inner)
    : IBlobTxStorage, IBlobTxMetadataStorage, IAtomicBlobTxStorage, ISpecChangeValidationStorage
{
    /// <summary>Full sidecar-carrying row reads.</summary>
    internal int FullRowReads { get; private set; }

    /// <summary>Sidecar-free record reads.</summary>
    internal int SidecarFreeReads { get; private set; }

    internal void ResetCounts() => (FullRowReads, SidecarFreeReads) = (0, 0);

    public bool TryGet(in ValueHash256 hash, Address sender, in UInt256 timestamp, [NotNullWhen(true)] out Transaction? transaction)
    {
        FullRowReads++;
        return inner.TryGet(hash, sender, timestamp, out transaction);
    }

    public bool TryGetWithoutBlobs(in ValueHash256 hash, Address sender, [NotNullWhen(true)] out Transaction? transaction)
    {
        SidecarFreeReads++;
        return inner.TryGetWithoutBlobs(hash, sender, out transaction);
    }

    public int TryGetMany(TxLookupKey[] keys, int count, Transaction?[] results)
    {
        FullRowReads += count;
        return inner.TryGetMany(keys, count, results);
    }

    public IEnumerable<LightTransaction> GetAll() => inner.GetAll();
    public void Add(Transaction transaction) => inner.Add(transaction);
    public void AddWithoutBlobs(Transaction transaction) => inner.AddWithoutBlobs(transaction);
    public void Delete(in ValueHash256 hash, in UInt256 timestamp) => inner.Delete(hash, timestamp);

    public bool TryGetBlobTransactionsFromBlock(ulong blockNumber, [NotNullWhen(true)] out Transaction[]? blockBlobTransactions)
        => inner.TryGetBlobTransactionsFromBlock(blockNumber, out blockBlobTransactions);

    public void AddBlobTransactionsFromBlock(ulong blockNumber, in ArrayPoolListRef<Transaction> blockBlobTransactions)
        => inner.AddBlobTransactionsFromBlock(blockNumber, blockBlobTransactions);

    public void DeleteBlobTransactionsFromBlock(ulong blockNumber) => inner.DeleteBlobTransactionsFromBlock(blockNumber);

    void IAtomicBlobTxStorage.DeleteMany(scoped ReadOnlySpan<BlobTxDeleteKey> keys) => ((IAtomicBlobTxStorage)inner).DeleteMany(keys);

    void IAtomicBlobTxStorage.Replace(Transaction transaction, scoped ReadOnlySpan<UInt256> obsoleteTimestamps)
        => ((IAtomicBlobTxStorage)inner).Replace(transaction, obsoleteTimestamps);

    string? ISpecChangeValidationStorage.GetSpecChangeValidationMarker() => ((ISpecChangeValidationStorage)inner).GetSpecChangeValidationMarker();

    void ISpecChangeValidationStorage.SetSpecChangeValidationMarker(string? marker) => ((ISpecChangeValidationStorage)inner).SetSpecChangeValidationMarker(marker);
}
