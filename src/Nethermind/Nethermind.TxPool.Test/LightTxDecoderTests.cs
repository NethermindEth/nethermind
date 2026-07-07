// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
public class LightTxDecoderTests
{
    [Test]
    public void should_roundtrip_sparse_blob_tx_cell_mask_and_network_size()
    {
        Transaction tx = BuildBlobTx();
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        BlobCellMask cellMask = BlobCellMask.FromIndices([3, 42, 100]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out byte[][] cells), Is.True);
        byte[][] emptyBlobs = new byte[wrapper.Blobs.Length][];
        System.Array.Fill(emptyBlobs, []);
        tx.NetworkWrapper = wrapper with { Blobs = emptyBlobs, CellMask = cellMask, Cells = cells };
        tx.ClearLengthCache();

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        Assert.That(decoded.BlobCellMask, Is.EqualTo(cellMask));
        Assert.That(decoded.ProofVersion, Is.EqualTo(ProofVersion.V1));
        Assert.That(decoded.GetSparseBlobNetworkSize(), Is.EqualTo(tx.TryCalculateSparseBlobNetworkSize()));
        Assert.That(decoded.Hash, Is.EqualTo(tx.Hash));
    }

    [TestCase(true)]
    [TestCase(false)]
    public void should_decode_legacy_entry_without_mask_as_full(bool includeProofVersion)
    {
        Transaction tx = BuildBlobTx();

        LightTransaction decoded = LightTxDecoder.Decode(EncodeLegacy(tx, includeProofVersion));

        // Entries persisted before the mask field was added always hold full blobs.
        Assert.That(decoded.BlobCellMask, Is.EqualTo(BlobCellMask.Full));
        Assert.That(decoded.ProofVersion, Is.EqualTo(includeProofVersion ? ProofVersion.V1 : ProofVersion.V0));
        Assert.That(decoded.GetSparseBlobNetworkSize(), Is.EqualTo(0));
        Assert.That(decoded.Hash, Is.EqualTo(tx.Hash));
    }

    private static Transaction BuildBlobTx() => Build.A.Transaction
        .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
        .WithMaxFeePerGas(1.GWei)
        .WithMaxPriorityFeePerGas(1.GWei)
        .WithNonce(0UL)
        .SignedAndResolved()
        .TestObject;

    private static byte[] EncodeLegacy(Transaction tx, bool includeProofVersion)
    {
        int length = Rlp.LengthOf(tx.Timestamp)
            + Rlp.LengthOf(tx.SenderAddress)
            + Rlp.LengthOf(tx.Nonce)
            + Rlp.LengthOf(tx.Hash)
            + Rlp.LengthOf(tx.Value)
            + Rlp.LengthOf(tx.GasLimit)
            + Rlp.LengthOf(tx.GasPrice)
            + Rlp.LengthOf(tx.DecodedMaxFeePerGas)
            + Rlp.LengthOf(tx.MaxFeePerBlobGas!.Value)
            + Rlp.LengthOf(tx.BlobVersionedHashes!)
            + Rlp.LengthOf(tx.PoolIndex)
            + Rlp.LengthOf(tx.GetLength())
            + (includeProofVersion ? Rlp.LengthOf(sizeof(byte)) : 0);

        byte[] bytes = new byte[length];
        RlpWriter writer = new(bytes);
        writer.Encode(tx.Timestamp);
        writer.Encode(tx.SenderAddress);
        writer.Encode(tx.Nonce);
        writer.Encode(tx.Hash);
        writer.Encode(in tx.ValueRef);
        writer.Encode(tx.GasLimit);
        writer.Encode(tx.GasPrice);
        writer.Encode(tx.DecodedMaxFeePerGas);
        writer.Encode(tx.MaxFeePerBlobGas!.Value);
        writer.Encode(tx.BlobVersionedHashes!);
        writer.Encode(tx.PoolIndex);
        writer.Encode(tx.GetLength());
        if (includeProofVersion)
        {
            writer.Encode((byte)ProofVersion.V1);
        }

        return bytes;
    }
}
