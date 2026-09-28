// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
public class TransactionSizeCacheTests
{
    // Proof-version translation swaps the sidecar for one of a different length. The announced size is
    // already cached by then, so a stale value would advertise a length the encoded transaction no longer has.
    [Test]
    public void Replacing_the_network_wrapper_invalidates_the_cached_size()
    {
        Transaction tx = Build.A.Transaction.WithShardBlobTxTypeAndFields(1).SignedAndResolved().TestObject;
        ShardBlobNetworkWrapper original = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        int sizeBeforeTranslation = tx.GetLength();

        byte[][] moreProofs = new byte[original.Proofs.Length + 4][];
        for (int i = 0; i < moreProofs.Length; i++) moreProofs[i] = new byte[48];
        tx.NetworkWrapper = new ShardBlobNetworkWrapper(original.Blobs, original.Commitments, moreProofs, ProofVersion.V1);

        Assert.That(tx.GetLength(), Is.Not.EqualTo(sizeBeforeTranslation));
    }

    // Assigning NetworkWrapper clears the size, so CopyTo has to copy it after the wrapper. Priming the
    // cache and then growing a field that does not clear it makes a recomputed value differ from a copied one.
    [Test]
    public void CopyTo_preserves_the_cached_size_despite_the_wrapper_clearing_it()
    {
        Transaction source = Build.A.Transaction.WithShardBlobTxTypeAndFields(1).SignedAndResolved().TestObject;
        int cached = source.GetLength();
        source.BlobVersionedHashes = [source.BlobVersionedHashes![0], new byte[32], new byte[32]];
        Assert.That(source.GetLength(), Is.EqualTo(cached), "precondition: the cache is not cleared by this field");

        Transaction copy = new();
        source.CopyTo(copy);

        Assert.That(copy.GetLength(), Is.EqualTo(cached));
    }
}
