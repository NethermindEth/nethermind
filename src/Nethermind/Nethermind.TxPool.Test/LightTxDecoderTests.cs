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
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.TxPool.Collections;
using NSubstitute;
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

    // A record layout change leaves every record unreadable at once, so the skips have to collapse into one line
    // rather than one per blob transaction in the pool.
    [Test]
    public void Unreadable_records_are_reported_as_one_warning()
    {
        const int unreadableCount = 5;
        MemColumnsDb<BlobTxsColumns> database = new();
        IDb lightBlobTxs = database.GetColumnDb(BlobTxsColumns.LightBlobTxs);
        for (int i = 0; i < unreadableCount; i++)
        {
            lightBlobTxs.Set([(byte)i], EncodeWithBlobFieldsLast(BlobCarryingTx(TxType.FrameTx)));
        }

        InterfaceLogger logger = Substitute.For<InterfaceLogger>();
        logger.IsWarn.Returns(true);

        // The exception type is what tells a layout change apart from one corrupt record, so the summary names it.
        string expectedType = Assert.Catch(() => LightTxDecoder.Decode(EncodeWithBlobFieldsLast(BlobCarryingTx(TxType.FrameTx))))!.GetType().Name;

        List<LightTransaction> loaded = [.. new BlobTxStorage(database, new OneLoggerLogManager(new ILogger(logger))).GetAll()];

        Assert.That(loaded, Is.Empty);
        logger.Received(1).Warn(Arg.Is<string>(text => text.Contains(unreadableCount.ToString()) && text.Contains(expectedType)));
    }

    // The catch must span every shape a foreign or damaged record decodes into, not just the mask check:
    // a truncated record surfaces from the reader's unchecked slice rather than as an RlpException.
    [Test]
    public void Records_failing_in_different_ways_are_all_skipped()
    {
        // A long-form list prefix mid-record makes the reader take a bogus length and index past the buffer,
        // which is the third root and reaches neither the mask check nor the RLP grammar errors.
        byte[] longFormPrefix = LightTxDecoder.Encode(BlobCarryingTx(TxType.Blob));
        longFormPrefix[64] = 0xf8;

        byte[][] corrupt =
        [
            EncodeWithBlobFieldsLast(BlobCarryingTx(TxType.FrameTx)),
            LightTxDecoder.Encode(BlobCarryingTx(TxType.Blob))[..^5],
            longFormPrefix,
            [0xff, 0xff, 0xff, 0xff],
        ];

        HashSet<string> shapes = [];
        foreach (byte[] record in corrupt)
        {
            shapes.Add(Assert.Catch(() => LightTxDecoder.Decode(record))!.GetType().Name);
        }

        Assert.That(shapes, Has.Count.GreaterThanOrEqualTo(3),
            $"these records must span the roots the catch filter lists, but they only produced: {string.Join(", ", shapes)}");

        Transaction readable = BlobCarryingTx(TxType.Blob);
        MemColumnsDb<BlobTxsColumns> database = new();
        IDb lightBlobTxs = database.GetColumnDb(BlobTxsColumns.LightBlobTxs);
        for (int i = 0; i < corrupt.Length; i++)
        {
            lightBlobTxs.Set([(byte)i], corrupt[i]);
        }

        lightBlobTxs.Set([(byte)corrupt.Length], LightTxDecoder.Encode(readable));

        List<LightTransaction> loaded = null;
        Assert.That(() => loaded = [.. new BlobTxStorage(database).GetAll()], Throws.Nothing);
        Assert.That(loaded, Has.Count.EqualTo(1));
        Assert.That(loaded[0].Hash, Is.EqualTo(readable.Hash));
    }

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

    // The nonce keys and the payer share one trailing group, because two adjacent optional sequences could not
    // be told apart: RLP does not distinguish a 20-byte integer from an address, so a two-key list is
    // byte-identical to a payer pair. All four combinations have to survive the round trip.
    [TestCase(false, false, TestName = "neither keys nor payer")]
    [TestCase(true, false, TestName = "keys alone")]
    [TestCase(false, true, TestName = "payer alone")]
    [TestCase(true, true, TestName = "keys and payer together")]
    public void Round_trip_carries_the_nonce_keys_and_the_payer_in_one_group(bool withKeys, bool withPayer)
    {
        // Two keys, each 20 bytes wide: the shape that is byte-identical to an address plus a scalar.
        UInt256[] keys = withKeys ? [UInt256.One << 152, (UInt256.One << 152) + 1] : null;
        Transaction tx = BlobCarryingTx(TxType.FrameTx, nonceKeys: keys);
        if (withPayer)
        {
            tx.PayerAddress = TestItem.AddressB;
            tx.PayerExposure = 12_345;
        }

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.NonceKeys, Is.EqualTo(keys));
            Assert.That(decoded.PayerAddress, Is.EqualTo(withPayer ? TestItem.AddressB : null));
            Assert.That(decoded.PayerExposure, Is.EqualTo(withPayer ? (UInt256)12_345 : null));
        }
    }

    // Records written before the grouping end in a flat nonce_keys list. Its first element is a scalar where the
    // grouped form always opens with the keys list, which is what lets one decoder read both.
    [Test]
    public void A_record_written_before_the_grouping_still_decodes()
    {
        Transaction bare = BlobCarryingTx(TxType.FrameTx);
        // The legacy trailing field: a flat list of the keys 1 and 2, each a single RLP byte.
        byte[] legacy = [.. LightTxDecoder.Encode(bare), 0xC2, 0x01, 0x02];

        LightTransaction decoded = LightTxDecoder.Decode(legacy);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.NonceKeys, Is.EqualTo(new UInt256[] { 1, 2 }));
            Assert.That(decoded.PayerAddress, Is.Null);
        }
    }

    // A payer that never reached the exposure gate holds no reservation, which this record cannot tell from a
    // zero one: reserving, releasing and restoring zero are all no-ops. The payer itself must still survive.
    [Test]
    public void Round_trip_of_a_payer_without_a_reservation_keeps_the_payer()
    {
        Transaction tx = BlobCarryingTx(TxType.FrameTx);
        tx.PayerAddress = TestItem.AddressB;
        tx.PayerExposure = null;

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.PayerAddress, Is.EqualTo(TestItem.AddressB));
            Assert.That(decoded.PayerExposure, Is.EqualTo(UInt256.Zero));
        }
    }

    // A legacy record whose flat list is empty leaves the group zero-length, and IsSequenceNext is an
    // unguarded index: deciding the branch on it first read past the end of the buffer.
    [Test]
    public void An_empty_legacy_key_list_decodes_rather_than_reading_past_the_buffer()
    {
        Transaction bare = BlobCarryingTx(TxType.FrameTx);
        byte[] legacy = [.. LightTxDecoder.Encode(bare), 0xC0];

        LightTransaction decoded = LightTxDecoder.Decode(legacy);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.NonceKeys, Is.Null);
            Assert.That(decoded.PayerAddress, Is.Null);
        }
    }

    // The pool holds a frameless record, so the sponsor the cap counts is recoverable only from the record
    // itself. A paymaster with no resolved payer leaves slot 1 empty, which is the shape that has to survive —
    // including behind a populated keys list, which a keyed transaction admitted without simulation produces.
    [TestCase(false, false, TestName = "paymaster alone")]
    [TestCase(true, false, TestName = "paymaster behind a payer")]
    [TestCase(false, true, TestName = "paymaster behind keys, no payer")]
    [TestCase(true, true, TestName = "paymaster behind keys and a payer")]
    public void Round_trip_carries_the_paymaster(bool withPayer, bool withKeys)
    {
        // Two keys, each 20 bytes wide: the shape byte-identical to an address plus a scalar.
        UInt256[] keys = withKeys ? [UInt256.One << 152, (UInt256.One << 152) + 1] : null;
        Transaction tx = BlobCarryingTx(TxType.FrameTx, nonceKeys: keys, paymaster: TestItem.AddressC);
        if (withPayer)
        {
            tx.PayerAddress = TestItem.AddressB;
            tx.PayerExposure = 12_345;
        }

        LightTransaction decoded = LightTxDecoder.Decode(LightTxDecoder.Encode(tx));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.PersistedPaymaster, Is.EqualTo(TestItem.AddressC));
            Assert.That(FrameTxValidation.GetPrefixPaymaster(decoded), Is.EqualTo(TestItem.AddressC));
            Assert.That(decoded.PayerAddress, Is.EqualTo(withPayer ? TestItem.AddressB : null));
            Assert.That(decoded.NonceKeys, Is.EqualTo(keys));
        }
    }

    // Downgrade readability is why the trailing fields are one nested group: a build predating a later slot
    // must still read the record, losing that field rather than the whole record.
    [Test]
    public void A_group_carrying_a_slot_this_build_does_not_know_still_decodes()
    {
        byte[] bare = LightTxDecoder.Encode(BlobCarryingTx(TxType.FrameTx));
        // [keys: [], payer: absent, exposure: 0, paymaster: AddressC, one slot this build has no name for]
        byte[] group = [0xD9, 0xC0, 0x80, 0x80, 0x94, .. TestItem.AddressC.Bytes, 0x01];

        LightTransaction decoded = LightTxDecoder.Decode([.. bare, .. group]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.PersistedPaymaster, Is.EqualTo(TestItem.AddressC));
            Assert.That(decoded.PayerAddress, Is.Null);
        }
    }

    // Slot 3 tolerates the placeholder for the reason slot 1 does: behind a later slot an absent paymaster is
    // written out rather than omitted, so the strict read would throw on a record a newer build wrote.
    [Test]
    public void A_group_whose_paymaster_slot_is_empty_decodes_as_unsponsored()
    {
        byte[] bare = LightTxDecoder.Encode(BlobCarryingTx(TxType.FrameTx));
        // [keys: [], payer: absent, exposure: 0, paymaster: absent, one slot this build has no name for]
        byte[] group = [0xC5, 0xC0, 0x80, 0x80, 0x80, 0x01];

        LightTransaction decoded = LightTxDecoder.Decode([.. bare, .. group]);

        Assert.That(decoded.PersistedPaymaster, Is.Null);
    }

    // A paymaster on its own opens the group, so it costs the header, the empty keys list, the empty payer
    // slot, a zero reservation and the address. Measured off two frameless records that differ in nothing
    // else: carrying the sponsor in a pay frame instead would also move the two persisted size fields.
    [Test]
    public void A_paymaster_alone_grows_the_record_by_the_group_and_the_address()
    {
        static LightTransaction Record(Address paymaster) => new(
            timestamp: 42, TestItem.AddressA, nonce: 7, TestItem.KeccakA, value: 5, gasLimit: 1_000_000,
            gasPrice: 1, maxFeePerGas: 2, maxFeePerBlobGas: 3, [new byte[32]], poolIndex: 11, size: 100,
            ProofVersion.V1, BlobCellMask.Full, sparseBlobNetworkSize: 100, TxType.FrameTx, paymaster: paymaster);

        int grownBy = LightTxDecoder.Encode(Record(TestItem.AddressC)).Length - LightTxDecoder.Encode(Record(null)).Length;

        Assert.That(grownBy, Is.EqualTo(1 + 1 + 1 + 1 + 21));
    }

    // The group is written only for a payer or a paymaster, so a keys-only record keeps the flat list every
    // earlier build writes — and stays readable by one, which a nested form would not be.
    [Test]
    public void A_keys_only_record_keeps_the_flat_list()
    {
        UInt256[] keys = [1, 2];
        Transaction tx = BlobCarryingTx(TxType.FrameTx, nonceKeys: keys);
        byte[] bare = LightTxDecoder.Encode(BlobCarryingTx(TxType.FrameTx));

        byte[] encoded = LightTxDecoder.Encode(tx);

        using (Assert.EnterMultipleScope())
        {
            // The flat list alone: 0xc2 over two single-byte keys, with no outer group header.
            Assert.That(encoded[bare.Length..], Is.EqualTo(new byte[] { 0xC2, 0x01, 0x02 }));
            Assert.That(LightTxDecoder.Decode(encoded).NonceKeys, Is.EqualTo(keys));
        }
    }

    // A record needing neither field must keep the exact layout every already-persisted one has, or the whole
    // pool becomes unreadable at once; a payer costs exactly the group and nothing more.
    [Test]
    public void A_payer_grows_the_record_by_the_group_alone()
    {
        Transaction bare = BlobCarryingTx(TxType.Blob);
        Transaction withPayer = BlobCarryingTx(TxType.Blob);
        withPayer.PayerAddress = TestItem.AddressB;
        withPayer.PayerExposure = 1;

        int grownBy = LightTxDecoder.Encode(withPayer).Length - LightTxDecoder.Encode(bare).Length;

        // The group header, the empty keys list holding slot 0, the 21-byte address and a 1-byte reservation.
        Assert.That(grownBy, Is.EqualTo(1 + 1 + 21 + 1));
    }

    private static Transaction BlobCarryingTx(TxType type, ulong? deadline = null, UInt256[] nonceKeys = null, Address paymaster = null)
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
            tx.Frames = paymaster is null
                ? [FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas)]
                : [FrameTxTestFrames.OnlyVerify(FrameTxTestFrames.PrefixFrameGas), FrameTxTestFrames.Pay(paymaster, FrameTxTestFrames.PrefixFrameGas)];
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
