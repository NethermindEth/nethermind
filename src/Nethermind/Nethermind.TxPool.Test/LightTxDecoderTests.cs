// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Collections;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

[TestFixture]
public class LightTxDecoderTests
{
    [Test]
    public void should_roundtrip_sparse_blob_tx_metadata()
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

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.BlobCellMask, Is.EqualTo(cellMask));
            Assert.That(decoded.ProofVersion, Is.EqualTo(ProofVersion.V1));
            Assert.That(decoded.GetConsensusEncodingSize(), Is.EqualTo(tx.GetLength(shouldCountBlobs: false)));
            Assert.That(decoded.GetElidedNetworkEncodingSize(), Is.EqualTo(tx.GetElidedNetworkEncodingSize()));
            Assert.That(decoded.Hash, Is.EqualTo(tx.Hash));
        }
    }

    [Test]
    public void should_roundtrip_v0_proof_version()
    {
        Transaction tx = Build.A.Transaction
            .WithShardBlobTxTypeAndFields(spec: Cancun.Instance)
            .WithMaxFeePerGas(1.GWei)
            .WithMaxPriorityFeePerGas(1.GWei)
            .WithNonce(0UL)
            .SignedAndResolved()
            .TestObject;

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        Assert.That(decoded.ProofVersion, Is.EqualTo(ProofVersion.V0));
    }

    [Test]
    public void should_preserve_sparse_metadata_when_reencoding_light_transaction()
    {
        Transaction tx = BuildBlobTx();
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        BlobCellMask cellMask = BlobCellMask.FromIndices([3, 42, 100]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out byte[][] cells), Is.True);
        byte[][] emptyBlobs = new byte[wrapper.Blobs.Length][];
        Array.Fill(emptyBlobs, []);
        tx.NetworkWrapper = wrapper with { Blobs = emptyBlobs, CellMask = cellMask, Cells = cells };
        tx.ClearLengthCache();

        LightTransaction first = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));
        LightTransaction second = LightTxDecoder.Decode(LightTxDecoder.Encode(first));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.ProofVersion, Is.EqualTo(first.ProofVersion));
            Assert.That(second.BlobCellMask, Is.EqualTo(first.BlobCellMask));
            Assert.That(second.GetElidedNetworkEncodingSize(), Is.EqualTo(first.GetElidedNetworkEncodingSize()));
        }
    }

    [Test]
    public void should_derive_elided_size_only_from_versioned_consensus_size([Values] bool hasConsensusSizeMarker)
    {
        Transaction tx = BuildBlobTx();
        BlobCellMask cellMask = BlobCellMask.FromIndices([3, 42, 100]);

        LightTransaction decoded = LightTxDecoder.Decode(EncodeLegacy(
            tx,
            includeProofVersion: true,
            cellMask,
            persistedSize: hasConsensusSizeMarker ? tx.GetLength(shouldCountBlobs: false) : 12345,
            sizeFormatVersion: hasConsensusSizeMarker ? (byte)1 : null));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.BlobCellMask, Is.EqualTo(cellMask));
            Assert.That(decoded.GetConsensusEncodingSize(), Is.EqualTo(hasConsensusSizeMarker ? tx.GetLength(shouldCountBlobs: false) : 0));
            Assert.That(decoded.GetElidedNetworkEncodingSize(), Is.EqualTo(hasConsensusSizeMarker ? tx.GetElidedNetworkEncodingSize() : 0));
        }
    }

    [Test]
    public void elided_network_encoding_size_matches_the_blob_elided_wire_length(
        [Values(1, 6)] int blobCount,
        [Values] ProofVersion proofVersion)
    {
        Transaction tx = BuildBlobTx(proofVersion, blobCount);
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        LightTransaction lightTx = new(tx);
        Assert.That(wrapper.Version, Is.EqualTo(proofVersion));

        int consensusSize = tx.GetLength(shouldCountBlobs: false);
        int wrapperOverhead =
            (proofVersion is ProofVersion.V1 ? 1 : 0)               // wrapper_version
            + Rlp.OfEmptyList.Length                                // elided blobs
            + Rlp.LengthOf(wrapper.Commitments)
            + Rlp.LengthOf(wrapper.Proofs);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(
                lightTx.GetElidedNetworkEncodingSize(),
                Is.EqualTo(1 + Rlp.LengthOfSequence(consensusSize - 1 + wrapperOverhead)));
            Assert.That(lightTx.GetElidedNetworkEncodingSize(), Is.LessThan(tx.GetLength()));
            Assert.That(lightTx.GetElidedNetworkEncodingSize() - consensusSize, Is.GreaterThan(8));
        }
    }

    [Test]
    public void overflowing_derived_elided_size_is_rejected([Values] ProofVersion proofVersion) =>
        Assert.That(
            TransactionExtensions.CalculateElidedNetworkEncodingSize(int.MaxValue, proofVersion, blobCount: 128),
            Is.Zero);

    [Test]
    public void should_read_and_preserve_elided_network_encoding_size_records_written_by_earlier_branch_versions()
    {
        Transaction tx = BuildBlobTx();
        int elidedNetworkEncodingSize = tx.GetElidedNetworkEncodingSize();
        LightTransaction decoded = LightTxDecoder.Decode(EncodeLegacy(
            tx,
            includeProofVersion: true,
            BlobCellMask.Full,
            elidedNetworkEncodingSize,
            sizeFormatVersion: 2));
        LightTransaction reencoded = LightTxDecoder.Decode(LightTxDecoder.Encode(decoded));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.GetConsensusEncodingSize(), Is.Zero);
            Assert.That(decoded.GetElidedNetworkEncodingSize(), Is.EqualTo(elidedNetworkEncodingSize));
            Assert.That(reencoded.GetElidedNetworkEncodingSize(), Is.EqualTo(elidedNetworkEncodingSize));
        }
    }

    [Test]
    public void should_decode_legacy_entry_without_mask_as_full([Values] bool includeProofVersion)
    {
        Transaction tx = BuildBlobTx();

        LightTransaction decoded = LightTxDecoder.Decode(EncodeLegacy(tx, includeProofVersion));

        // Entries persisted before the mask field was added always hold full blobs.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.BlobCellMask, Is.EqualTo(BlobCellMask.Full));
            Assert.That(decoded.ProofVersion, Is.EqualTo(includeProofVersion ? ProofVersion.V1 : ProofVersion.V0));
            Assert.That(decoded.GetElidedNetworkEncodingSize(), Is.Zero);
            Assert.That(decoded.Hash, Is.EqualTo(tx.Hash));
        }
    }

    [Test]
    public void should_refresh_unknown_elided_network_encoding_size_with_blob_pool_metadata()
    {
        Transaction tx = BuildBlobTx();
        LightTransaction lightTx = LightTxDecoder.Decode(EncodeLegacy(tx, includeProofVersion: true));

        lightTx.UpdateBlobPoolMetadata(tx);

        Assert.That(lightTx.GetElidedNetworkEncodingSize(), Is.EqualTo(tx.GetElidedNetworkEncodingSize()));
    }

    [Test]
    public void should_preserve_sparse_pool_public_api()
    {
        Type[] constructorParameters =
        [
            typeof(UInt256), typeof(Address), typeof(ulong), typeof(Hash256), typeof(UInt256),
            typeof(ulong), typeof(UInt256), typeof(UInt256), typeof(UInt256), typeof(byte[][]),
            typeof(ulong), typeof(int), typeof(ProofVersion)
        ];
        Transaction fullTx = BuildBlobTx();
        LightTransaction lightTx = new(fullTx);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(typeof(LightTransaction).GetConstructor(constructorParameters), Is.Not.Null);
            Assert.That(typeof(ITxPool).GetMethod(nameof(ITxPool.TryMergeBlobCells), [typeof(Hash256), typeof(BlobCellMask), typeof(byte[][])]), Is.Not.Null);
            Assert.That(typeof(BlobTxDistinctSortedPool).GetMethod(nameof(BlobTxDistinctSortedPool.TryMergeCells), [typeof(ValueHash256), typeof(BlobCellMask), typeof(byte[][])]), Is.Not.Null);
            Assert.That(lightTx.GetConsensusEncodingSize(), Is.EqualTo(fullTx.GetLength(shouldCountBlobs: false)));
            Assert.That(lightTx.GetElidedNetworkEncodingSize(), Is.EqualTo(fullTx.GetElidedNetworkEncodingSize()));
        }
    }

    private static Transaction BuildBlobTx(ProofVersion proofVersion = ProofVersion.V1, int blobCount = 1) => Build.A.Transaction
        .WithShardBlobTxTypeAndFields(blobCount, spec: proofVersion is ProofVersion.V1 ? Osaka.Instance : Cancun.Instance)
        .WithMaxFeePerGas(1.GWei)
        .WithMaxPriorityFeePerGas(1.GWei)
        .WithNonce(0UL)
        .SignedAndResolved()
        .TestObject;

    private static byte[] EncodeLegacy(
        Transaction tx,
        bool includeProofVersion,
        BlobCellMask? cellMask = null,
        int? persistedSize = null,
        byte? sizeFormatVersion = null)
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
            + (includeProofVersion ? Rlp.LengthOf(sizeof(byte)) : 0)
            + (cellMask is null ? 0 : Rlp.LengthOfByteString(BlobCellMask.FixedByteLength, firstByte: 0))
            + (persistedSize is null ? 0 : Rlp.LengthOf(persistedSize.Value))
            + (sizeFormatVersion is null ? 0 : Rlp.LengthOf(sizeFormatVersion.Value));

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

        if (cellMask is { } availableCellMask)
        {
            System.Span<byte> maskBytes = stackalloc byte[BlobCellMask.FixedByteLength];
            availableCellMask.WriteTo(maskBytes);
            writer.Encode(maskBytes);
        }

        if (persistedSize is { } networkSize)
        {
            writer.Encode(networkSize);
        }

        if (sizeFormatVersion is { } formatVersion)
        {
            writer.Encode(formatVersion);
        }

        return bytes;
    }
}
