// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Logging;

namespace Nethermind.TxPool.Collections;

public class BlobTxDistinctSortedPool(int capacity, IComparer<Transaction> comparer, ILogManager logManager)
    : TxDistinctSortedPool(capacity, comparer, logManager)
{
    protected override string ShortPoolName => "BlobPool";

    public ConcurrentDictionary<string, List<Hash256>> GetBlobIndex => _blobIndex;

    private readonly ConcurrentDictionary<string, List<Hash256>> _blobIndex = new();

    protected override IComparer<Transaction> GetReplacementComparer(IComparer<Transaction> comparer)
        => comparer.GetBlobReplacementComparer();

    public override bool TryInsert(ValueHash256 hash, Transaction blobTx, out Transaction? removed)
    {
        if (base.TryInsert(blobTx.Hash, blobTx, out removed))
        {
            AddToBlobIndex(blobTx);
            return true;
        }

        return false;
    }

    protected void AddToBlobIndex(Transaction blobTx)
    {
        if (blobTx.BlobVersionedHashes?.Length > 0)
        {
            foreach (var blobVersionedHash in blobTx.BlobVersionedHashes)
            {
                if (blobVersionedHash?.Length == KzgPolynomialCommitments.BytesPerBlobVersionedHash)
                {
                    _blobIndex.AddOrUpdate(blobVersionedHash.ToHexString(),
                        k => [blobTx.Hash!],
                        (k, b) =>
                        {
                            b.Add(blobTx.Hash!);
                            return b;
                        });
                }
            }
        }
    }

    protected override bool Remove(ValueHash256 hash, out Transaction? tx)
    {
        if (base.Remove(hash, out tx))
        {
            if (tx is not null)
            {
                RemoveFromBlobIndex(tx);
            }
            return true;
        }

        return false;
    }

    private void RemoveFromBlobIndex(Transaction blobTx)
    {
        if (blobTx.BlobVersionedHashes?.Length > 0)
        {
            foreach (var blobVersionedHash in blobTx.BlobVersionedHashes)
            {
                if (blobVersionedHash?.ToHexString() is { } key
                    && _blobIndex.TryGetValue(key, out List<Hash256>? txHashes))
                {
                    if (txHashes.Count < 2)
                    {
                        _blobIndex.Remove(key, out _);
                    }
                    else
                    {
                        txHashes.Remove(blobTx.Hash!);
                    }
                }
            }
        }
    }

    /// <summary>
    /// For tests only - to test sorting
    /// </summary>
    internal void TryGetBlobTxSortingEquivalent(Hash256 hash, out Transaction? lightBlobTx)
        => base.TryGetValue(hash, out lightBlobTx);
}
