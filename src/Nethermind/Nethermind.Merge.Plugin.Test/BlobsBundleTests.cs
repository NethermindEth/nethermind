// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using CkzgLib;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

// The header counts a type-6's blob gas, so the bundle has to publish its blobs alongside type-3's.
[TestFixture]
public class BlobsBundleTests
{
    // Symmetry only: ResolveBlob declines a V0 wrapper, so this shape cannot reach a produced block.
    [Test]
    public void BlobsBundleV1_includes_blob_carrying_frame_tx()
    {
        MempoolTransaction frameBlobTx = BuildBlobCarryingFrameTx(blobCount: 2, ProofVersion.V0);
        MempoolTransaction type3Tx = Build.A.Transaction.WithShardBlobTxTypeAndFields(blobCount: 1).SignedAndResolved().MempoolTestObject;

        BlobsBundleV1 bundle = new(ProducedBlock(type3Tx, frameBlobTx));

        // Arrays are sized from the type-agnostic blob count, so only non-null entries prove the frame tx was copied in.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Blobs, Has.Length.EqualTo(3).And.None.Null);
            Assert.That(bundle.Commitments, Has.Length.EqualTo(3).And.None.Null);
            Assert.That(bundle.Proofs, Has.Length.EqualTo(3).And.None.Null);
        }
    }

    [Test]
    public void BlobsBundleV2_includes_blob_carrying_frame_tx()
    {
        MempoolTransaction frameBlobTx = BuildBlobCarryingFrameTx(blobCount: 2, ProofVersion.V1);

        BlobsBundleV2 bundle = new(ProducedBlock(frameBlobTx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bundle.Blobs, Has.Length.EqualTo(2).And.None.Null);
            Assert.That(bundle.Commitments, Has.Length.EqualTo(2).And.None.Null);
            Assert.That(bundle.Proofs, Has.Length.EqualTo(2 * Ckzg.CellsPerExtBlob).And.None.Null);
        }
    }

    // An empty bundle for a payload that still carries the blob transaction would be proposed as an invalid block.
    [Test]
    public void BlobsBundleV2_throws_for_blob_tx_without_cell_proofs([Values] bool hasBlobProofs)
    {
        MempoolTransaction type3Tx = Build.A.Transaction.WithShardBlobTxTypeAndFields(blobCount: 1).SignedAndResolved().MempoolTestObject;
        Block block = ProducedBlock(hasBlobProofs ? type3Tx : type3Tx with { NetworkWrapper = null });

        Assert.That(() => new BlobsBundleV2(block), Throws.ArgumentException);
    }

    private static BlockToProduce ProducedBlock(params MempoolTransaction[] transactions) =>
        new(Build.A.BlockHeader.TestObject, transactions, []);

    private static MempoolTransaction BuildBlobCarryingFrameTx(int blobCount, ProofVersion version)
    {
        if (!KzgPolynomialCommitments.IsInitialized)
        {
            KzgPolynomialCommitments.InitializeAsync().Wait();
        }

        IBlobProofsManager proofsManager = IBlobProofsManager.For(version);
        ShardBlobNetworkWrapper wrapper = proofsManager.AllocateWrapper([.. Enumerable.Range(1, blobCount).Select(i =>
        {
            byte[] blob = new byte[Ckzg.BytesPerBlob];
            blob[0] = (byte)(i % 256);
            return blob;
        })]);
        proofsManager.ComputeProofsAndCommitments(wrapper);

        return new MempoolTransaction(new Transaction
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = TestItem.AddressA,
            GasLimit = 1_000_000,
            GasPrice = 1,
            MaxFee = 100_000_000,
            DecodedMaxFeePerGas = 100,
            Frames =
            [
                new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            BlobVersionedHashes = proofsManager.ComputeHashes(wrapper),
        }, wrapper);
    }
}
