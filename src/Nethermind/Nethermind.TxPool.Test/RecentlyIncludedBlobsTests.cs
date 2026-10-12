// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using CkzgLib;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
[FixtureLifeCycle(LifeCycle.InstancePerTestCase)]
public class RecentlyIncludedBlobsTests
{
    private readonly RecentlyIncludedBlobs _blobs = new();

    [TestCase(9UL, true)]
    [TestCase(10UL, true)]
    [TestCase(13UL, true)]
    [TestCase(14UL, false)]
    public void Blobs_are_retained_for_four_blocks_from_their_height(ulong nextBlockNumber, bool expectedServed)
    {
        Transaction tx = CreateBlobTx(1);
        _blobs.StartBlock(10);
        _blobs.Add(tx);

        _blobs.StartBlock(nextBlockNumber);

        Assert.That(IsServed(tx), Is.EqualTo(expectedServed));
    }

    [Test]
    public void Blobs_added_at_a_lower_height_after_a_reorg_age_out_by_their_own_height()
    {
        Transaction original = CreateBlobTx(1);
        Transaction reorged = CreateBlobTx(2);
        Transaction replacement = CreateBlobTx(3);
        _blobs.StartBlock(10);
        _blobs.Add(original);
        _blobs.StartBlock(9);
        _blobs.Add(reorged);
        _blobs.StartBlock(10);
        _blobs.Add(replacement);
        bool allServedAfterReorg = IsServed(original) && IsServed(reorged) && IsServed(replacement);

        _blobs.StartBlock(13);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(allServedAfterReorg, Is.True, "all batches survive the reorg");
            Assert.That(IsServed(original), Is.True, "height 10 at head 13");
            Assert.That(IsServed(reorged), Is.False, "height 9 at head 13");
            Assert.That(IsServed(replacement), Is.True, "height 10 at head 13");
        }
    }

    [TestCase(RecentlyIncludedBlobs.MaxRetainedBatches, 0)]
    [TestCase(RecentlyIncludedBlobs.MaxRetainedBatches + 1, 1)]
    [TestCase(RecentlyIncludedBlobs.MaxRetainedBatches + 4, 4)]
    public void Oldest_inserted_batches_are_evicted_beyond_the_cap(int batchCount, int expectedEvicted)
    {
        List<Transaction> txs = [];
        for (int i = 0; i < batchCount; i++)
        {
            // Every start is at the same height, so only the cap can evict.
            _blobs.StartBlock(100);
            Transaction tx = CreateBlobTx((byte)(i + 1));
            _blobs.Add(tx);
            txs.Add(tx);
        }

        using (Assert.EnterMultipleScope())
        {
            for (int i = 0; i < batchCount; i++)
            {
                Assert.That(IsServed(txs[i]), Is.EqualTo(i >= expectedEvicted), $"batch {i}");
            }
        }
    }

    [Test]
    public void Blobs_of_the_same_block_share_one_batch_and_are_all_served()
    {
        Transaction first = CreateBlobTx(1);
        Transaction second = CreateBlobTx(2);
        _blobs.StartBlock(10);
        _blobs.Add(first);
        _blobs.Add(second);

        _blobs.StartBlock(13);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(IsServed(first), Is.True);
            Assert.That(IsServed(second), Is.True);
        }
    }

    private bool IsServed(Transaction tx) => _blobs.TryGetBlobAndProofV1(tx.BlobVersionedHashes![0]!, out _, out _);

    private static Transaction CreateBlobTx(byte seed)
    {
        byte[] versionedHash = new byte[32];
        versionedHash[0] = seed;
        byte[][] proofs = new byte[Ckzg.CellsPerExtBlob][];
        for (int i = 0; i < proofs.Length; i++)
        {
            proofs[i] = [seed];
        }

        return new Transaction
        {
            Type = TxType.Blob,
            BlobVersionedHashes = [versionedHash],
            NetworkWrapper = new ShardBlobNetworkWrapper([[seed]], [[seed]], proofs, ProofVersion.V1)
        };
    }
}
