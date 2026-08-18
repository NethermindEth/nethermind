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
}
