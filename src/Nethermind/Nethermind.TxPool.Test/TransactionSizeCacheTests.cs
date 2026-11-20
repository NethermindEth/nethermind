// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
public class TransactionSizeCacheTests
{
    // Proof-version translation swaps the sidecar for one of a different length, so the announced size
    // must follow the sidecar rather than a value cached for the original one.
    [Test]
    public void Replacing_the_network_wrapper_changes_the_network_length()
    {
        MempoolTransaction mempoolTx = Build.A.Transaction.WithShardBlobTxTypeAndFields(1).SignedAndResolved().MempoolTestObject;
        ShardBlobNetworkWrapper original = mempoolTx.NetworkWrapper!;
        int sizeBeforeTranslation = mempoolTx.GetLength();

        byte[] moreProofs = new byte[original.Proofs.Length + 4 * ShardBlobNetworkWrapper.BytesPerProof];
        MempoolTransaction translated = mempoolTx with { NetworkWrapper = new ShardBlobNetworkWrapper(original.Blobs, original.Commitments, moreProofs, ProofVersion.V1) };

        Assert.That(translated.GetLength(), Is.Not.EqualTo(sizeBeforeTranslation));
    }

    // Priming the cache and then growing a field that does not clear it makes a recomputed value differ from a copied one.
    [Test]
    public void CopyTo_preserves_the_cached_size()
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
