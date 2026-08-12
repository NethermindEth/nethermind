// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using CkzgLib;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

// The header counts a type-6's blob gas, so the bundle has to publish its blobs alongside type-3's or the
// CL receives one that does not add up.
[TestFixture]
public class BlobsBundleTests
{
    // Symmetry only: type-6 exists only where BlobProofVersion is V1, and ResolveBlob declines any other
    // version, so a V0-wrapped frame tx cannot actually reach a produced block or the V1 bundle.
    [Test]
    public void BlobsBundleV1_includes_blob_carrying_frame_tx()
    {
        Transaction frameBlobTx = BuildBlobCarryingFrameTx(blobCount: 2, ProofVersion.V0);
        Transaction type3Tx = Build.A.Transaction.WithShardBlobTxTypeAndFields(blobCount: 1).SignedAndResolved().TestObject;
        Block block = Build.A.Block.WithTransactions(type3Tx, frameBlobTx).TestObject;

        BlobsBundleV1 bundle = new(block);

        Assert.That(bundle.Blobs, Has.Length.EqualTo(3));
        Assert.That(bundle.Commitments, Has.Length.EqualTo(3));
        Assert.That(bundle.Proofs, Has.Length.EqualTo(3));
    }

    [Test]
    public void BlobsBundleV2_includes_blob_carrying_frame_tx()
    {
        Transaction frameBlobTx = BuildBlobCarryingFrameTx(blobCount: 2, ProofVersion.V1);
        Block block = Build.A.Block.WithTransactions(frameBlobTx).TestObject;

        BlobsBundleV2 bundle = new(block);

        Assert.That(bundle.Blobs, Has.Length.EqualTo(2));
        Assert.That(bundle.Commitments, Has.Length.EqualTo(2));
        Assert.That(bundle.Proofs, Has.Length.EqualTo(2 * Ckzg.CellsPerExtBlob));
    }

    private static Transaction BuildBlobCarryingFrameTx(int blobCount, ProofVersion version)
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

        return new Transaction
        {
            Type = TxType.FrameTx,
            ChainId = TestBlockchainIds.ChainId,
            Nonce = 0,
            SenderAddress = TestItem.AddressA,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 100,
            MaxFeePerBlobGas = 1,
            Frames =
            [
                new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default),
            ],
            FrameSignatures = [],
            BlobVersionedHashes = proofsManager.ComputeHashes(wrapper),
            NetworkWrapper = wrapper,
        };
    }
}
