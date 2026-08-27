// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Db;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Collections;
using NUnit.Framework;

namespace Nethermind.TxPool.Test;

/// <summary>Backward compatibility of the persisted <see cref="Nethermind.Db.BlobTxsColumns.LightBlobTxs"/> record;
/// a record that fails to decode breaks pool startup.</summary>
[TestFixture]
public class LightTxDecoderTests
{
    [TestCase(TxType.Blob)]
    [TestCase(TxType.FrameTx)]
    public void Round_trip_preserves_the_type_and_proof_version(TxType type)
    {
        Transaction tx = BlobCarryingTx(type);

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Type, Is.EqualTo(type));
            Assert.That(decoded.ProofVersion, Is.EqualTo(ProofVersion.V1));
            Assert.That(decoded.SenderAddress, Is.EqualTo(tx.SenderAddress));
            Assert.That(decoded.Nonce, Is.EqualTo(tx.Nonce));
            Assert.That(decoded.Hash, Is.EqualTo(tx.Hash));
            Assert.That(decoded.PoolIndex, Is.EqualTo(tx.PoolIndex));
            Assert.That(decoded.BlobVersionedHashes, Is.EqualTo(tx.BlobVersionedHashes));
        }
    }

    // The type is the last always-written field and is one RLP byte, so dropping it reproduces the older layout.
    [Test]
    public void Legacy_record_without_the_type_field_decodes_as_a_blob_tx()
    {
        byte[] full = LightTxDecoder.Encode(BlobCarryingTx(TxType.Blob));

        LightTransaction decoded = LightTxDecoder.Decode(full[..^1]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Type, Is.EqualTo(TxType.Blob));
            Assert.That(decoded.ProofVersion, Is.EqualTo(ProofVersion.V1));
            Assert.That(decoded.NonceKeys, Is.Null);
            Assert.That(decoded.PersistedExpiryDeadline, Is.Null);
        }
    }

    // ProofVersion.V0 encodes as the RLP empty string, which a raw byte read returns as 128.
    [TestCase(ProofVersion.V0)]
    [TestCase(ProofVersion.V1)]
    public void Round_trip_preserves_the_proof_version(ProofVersion version)
    {
        Transaction tx = BlobCarryingTx(TxType.Blob);
        tx.NetworkWrapper = new ShardBlobNetworkWrapper([[1]], [[2]], [[3]], version);

        Assert.That(LightTxDecoder.Decode(LightTxDecoder.Encode(tx)).ProofVersion, Is.EqualTo(version));
    }

    // Both trailing fields are optional and positional, so every combination has to decode back to what was
    // written - in particular a zero deadline is a deadline, not an absent field.
    [TestCaseSource(nameof(TrailingFieldCases))]
    public void Round_trip_preserves_the_optional_trailing_fields(UInt256[] nonceKeys, ulong? deadline)
    {
        Transaction tx = BlobCarryingTx(TxType.FrameTx, deadline, nonceKeys);

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.NonceKeys, Is.EqualTo(nonceKeys));
            Assert.That(decoded.PersistedExpiryDeadline, Is.EqualTo(deadline));
        }
    }

    private static IEnumerable<TestCaseData> TrailingFieldCases()
    {
        // [0] aliases the account nonce, so it must survive as itself rather than collapse to an absent field.
        UInt256[][] nonceKeySets = [null, [UInt256.Zero], [0xbeef], [1, UInt256.MaxValue], FullWidthKeys()];
        foreach (ulong? deadline in (ulong?[])[null, 0ul, 1_000ul])
        {
            foreach (UInt256[] nonceKeys in nonceKeySets)
            {
                yield return new TestCaseData(nonceKeys, deadline);
            }
        }
    }

    // A full set of 32-byte keys is the only case that reaches the long-form sequence header and fills the
    // decoder's stack buffer exactly.
    private static UInt256[] FullWidthKeys()
    {
        UInt256[] keys = new UInt256[Eip8250Constants.MaxNonceKeys];
        for (int i = 0; i < keys.Length; i++)
        {
            keys[i] = UInt256.MaxValue - (UInt256)(keys.Length - 1 - i);
        }

        return keys;
    }

    // The blob fields were later put ahead of the frame-transaction ones, so a record written by an earlier build of
    // this branch no longer decodes. The pool is a cache, so it has to lose that record rather than refuse to start.
    [TestCase(TxType.Blob, null)]
    [TestCase(TxType.FrameTx, null)]
    [TestCase(TxType.FrameTx, 1_000ul)]
    public void Unreadable_record_is_skipped_and_leaves_the_rest_of_the_pool_loadable(TxType type, ulong? deadline)
    {
        Transaction readable = BlobCarryingTx(TxType.Blob);
        MemColumnsDb<BlobTxsColumns> database = new();
        IDb lightBlobTxs = database.GetColumnDb(BlobTxsColumns.LightBlobTxs);
        lightBlobTxs.Set(UnreadableRecordKey, EncodeWithBlobFieldsLast(BlobCarryingTx(type, deadline)));
        lightBlobTxs.Set(ReadableRecordKey, LightTxDecoder.Encode(readable));

        List<LightTransaction> loaded = null;
        Assert.That(() => loaded = [.. new BlobTxStorage(database).GetAll()], Throws.Nothing);
        Assert.That(loaded, Has.Count.EqualTo(1));
        Assert.That(loaded[0].Hash, Is.EqualTo(readable.Hash));
    }

    // GetAllValues walks the column in key order, so the unreadable record is reached first.
    private static readonly byte[] UnreadableRecordKey = [0];
    private static readonly byte[] ReadableRecordKey = [1];

    /// <summary>Writes the record layout that preceded the blob fields moving in front of the frame-transaction ones.</summary>
    private static byte[] EncodeWithBlobFieldsLast(Transaction tx)
    {
        bool hasDeadline = FrameTxValidation.TryGetExpiryDeadline(tx, out ulong expiryDeadline);
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
            + Rlp.LengthOf(sizeof(byte))
            + Rlp.LengthOf((byte)tx.Type)
            + (hasDeadline ? Rlp.LengthOf(expiryDeadline) : 0);

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
        writer.Encode((byte)((tx.NetworkWrapper as ShardBlobNetworkWrapper)?.Version ?? default));
        writer.Encode((byte)tx.Type);
        if (hasDeadline) writer.Encode(expiryDeadline);

        return bytes;
    }

    private static Transaction BlobCarryingTx(TxType type, ulong? deadline = null, UInt256[] nonceKeys = null)
    {
        byte[][] versionedHashes = [new byte[32]];
        Transaction tx = new()
        {
            Type = type,
            ChainId = TestBlockchainIds.ChainId,
            SenderAddress = TestItem.AddressA,
            Nonce = 7,
            GasLimit = 1_000_000,
            GasPrice = 1,
            DecodedMaxFeePerGas = 2,
            MaxFeePerBlobGas = 3,
            BlobVersionedHashes = versionedHashes,
            Value = 5,
            PoolIndex = 11,
            Timestamp = 42,
            NetworkWrapper = new ShardBlobNetworkWrapper([[1]], [[2]], [[3]], ProofVersion.V1),
            NonceKeys = nonceKeys,
        };

        if (type == TxType.FrameTx)
        {
            tx.Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveExecutionAndPayment, target: null, gasLimit: 100_000, UInt256.Zero, default)];
            tx.FrameSignatures = [];
        }

        if (deadline is not null)
        {
            // An expiry verifier frame carries a deadline only where the spec permits it: at the head.
            byte[] expiryData = new byte[Eip8141Constants.ExpiryDataLength];
            BinaryPrimitives.WriteUInt64BigEndian(expiryData, deadline.Value);
            tx.Frames = [new TxFrame(TxFrame.ModeVerify, TxFrame.ApproveScopeNone, Eip8141Constants.ExpiryVerifierAddress, gasLimit: 50_000, UInt256.Zero, expiryData), .. tx.Frames!];
        }

        tx.Hash = tx.CalculateHash();
        return tx;
    }

    [Test]
    public void should_roundtrip_sparse_blob_tx_cell_mask_and_consensus_size()
    {
        Transaction tx = BuildBlobTx();
        ShardBlobNetworkWrapper wrapper = (ShardBlobNetworkWrapper)tx.NetworkWrapper!;
        BlobCellMask cellMask = BlobCellMask.FromIndices([3, 42, 100]);
        Assert.That(BlobCellsHelper.TryGetFlattenedCells(wrapper, cellMask, out byte[][] cells), Is.True);
        byte[][] emptyBlobs = new byte[wrapper.Blobs.Length][];
        Array.Fill(emptyBlobs, []);
        tx.NetworkWrapper = wrapper with { Blobs = emptyBlobs, CellMask = cellMask, Cells = cells };
        tx.ClearLengthCache();

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        Assert.That(decoded.BlobCellMask, Is.EqualTo(cellMask));
        Assert.That(decoded.ProofVersion, Is.EqualTo(ProofVersion.V1));
        Assert.That(decoded.GetConsensusEncodingSize(), Is.EqualTo(tx.GetLength(shouldCountBlobs: false)));
        Assert.That(decoded.Hash, Is.EqualTo(tx.Hash));
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
            Assert.That(second.GetConsensusEncodingSize(), Is.EqualTo(first.GetConsensusEncodingSize()));
        }
    }

    [Test]
    public void should_not_treat_legacy_sparse_network_size_as_consensus_encoding_size()
    {
        Transaction tx = BuildBlobTx();
        BlobCellMask cellMask = BlobCellMask.FromIndices([3, 42, 100]);

        LightTransaction decoded = LightTxDecoder.Decode(EncodeLegacy(
            tx,
            includeProofVersion: true,
            cellMask,
            sparseBlobNetworkSize: 12345));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.BlobCellMask, Is.EqualTo(cellMask));
            Assert.That(decoded.GetConsensusEncodingSize(), Is.Zero);
        }
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
        Assert.That(decoded.GetConsensusEncodingSize(), Is.EqualTo(0));
        Assert.That(decoded.Hash, Is.EqualTo(tx.Hash));
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
        }
    }

    private static Transaction BuildBlobTx() => Build.A.Transaction
        .WithShardBlobTxTypeAndFields(spec: Osaka.Instance)
        .WithMaxFeePerGas(1.GWei)
        .WithMaxPriorityFeePerGas(1.GWei)
        .WithNonce(0UL)
        .SignedAndResolved()
        .TestObject;

    private static byte[] EncodeLegacy(
        Transaction tx,
        bool includeProofVersion,
        BlobCellMask? cellMask = null,
        int? sparseBlobNetworkSize = null)
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
            + (sparseBlobNetworkSize is null ? 0 : Rlp.LengthOf(sparseBlobNetworkSize.Value));

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
            Span<byte> maskBytes = stackalloc byte[BlobCellMask.FixedByteLength];
            availableCellMask.WriteTo(maskBytes);
            writer.Encode(maskBytes);
        }

        if (sparseBlobNetworkSize is { } networkSize)
        {
            writer.Encode(networkSize);
        }

        return bytes;
    }
}
