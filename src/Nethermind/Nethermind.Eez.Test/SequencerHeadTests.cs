// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Network;
using Nethermind.Int256;
using Nethermind.Serialization.FluentRlp;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Proofs;
using NUnit.Framework;
using Snappier;

namespace Nethermind.Eez.Test;

public partial class SequencerHeadTests
{
    private static readonly Address Registry = new("0x5FbDB2315678afecb367f032d93F642f64180aa3");
    private static readonly Address Signer = new("0xf39Fd6e51aad88F6F4ce6aB8827279cffFb92266");
    private static readonly Hash256 BlockHash = new("0xd122d407ef1f4b4722b48ba4dd225fa5cba50a2b95a1265585060f0464dcce25");
    private static readonly Hash256 ParentHash = new("0x" + new string('a', 64));
    private static readonly SequencerHeadClaim Claim = new(1, 7, BlockHash, ParentHash, 1700000000000, 1);
    private static readonly byte[] Envelope = LoadEnvelope();
    private static readonly byte[] Compressed = Snappy.CompressToArray(Envelope);
    private readonly SequencerHeadCodec _codec = new(1337, Registry, 1, Signer);

    [Test]
    public void Published_vector_matches_hashes_signature_block_and_message_id()
    {
        VerifiedSequencerHead head = Decode(Compressed);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_codec.Topic, Is.EqualTo("/eez/1337/1/heads/0"));
            Assert.That(_codec.DomainSeparator, Is.EqualTo(new Hash256("0x7bcd13c41deec50e6183399b45bf4a4676869087cef8d68623fe7ff72b7f8f9c")));
            Assert.That(_codec.SigningHash(Claim), Is.EqualTo(new Hash256("0x14fb14f7072872b45edee4f4136fb40bce291cc9baacd928b65157800681e090")));
            Assert.That(head.Claim, Is.EqualTo(Claim));
            Assert.That(head.Signer, Is.EqualTo(Signer));
            Assert.That(head.Block.Hash, Is.EqualTo(BlockHash));
            Assert.That(head.Block.Withdrawals, Is.Empty);
            Assert.That(_codec.MessageId(Compressed).ToHexString(), Is.EqualTo("81d7c145eb07b6c067870afb26ccda4aabdff8e7"));
        }
    }

    [Test]
    public void Recompression_preserves_message_id()
    {
        byte[] literal = LiteralCompress(Envelope);
        Assert.That(literal, Is.Not.EqualTo(Compressed));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_codec.TryDecode(literal, out _), Is.True);
            Assert.That(_codec.MessageId(literal), Is.EqualTo(_codec.MessageId(Compressed)));
        }
    }

    [TestCaseSource(nameof(InvalidSignatures))]
    public void Rejects_noncanonical_or_invalid_signature(byte[] signature)
    {
        RlpReader outer = new(Envelope);
        RlpReader reader = outer.EnterSequence(out _);
        reader.SkipItem();
        reader.SkipItem();
        byte[] block = reader.DecodeByteArray();
        byte[] encoded = Rlp.Encode(EncodeClaim(Claim), Rlp.Encode(signature), Rlp.Encode(block)).Bytes;
        Assert.That(_codec.TryDecode(Snappy.CompressToArray(encoded), out _), Is.False);
    }

    private static IEnumerable<byte[]> InvalidSignatures()
    {
        RlpReader outer = new(Envelope);
        RlpReader reader = outer.EnterSequence(out _);
        reader.SkipItem();
        byte[] valid = reader.DecodeByteArray();
        List<byte[]> signatures = [];
        foreach (byte recovery in new byte[] { 0, 1, 26, 29, 35, 255 })
        {
            byte[] signature = (byte[])valid.Clone();
            signature[64] = recovery;
            signatures.Add(signature);
        }
        signatures.Add(valid[..64]);
        signatures.Add([.. valid, 0]);
        foreach (int offset in new[] { 0, 32 })
        {
            byte[] zero = (byte[])valid.Clone();
            zero.AsSpan(offset, 32).Clear();
            signatures.Add(zero);
            byte[] order = (byte[])valid.Clone();
            UInt256.Parse("0xfffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141").ToBigEndian(order.AsSpan(offset, 32));
            signatures.Add(order);
        }
        byte[] malleated = (byte[])valid.Clone();
        UInt256 s = new(valid.AsSpan(32, 32), isBigEndian: true);
        (UInt256.Parse("0xfffffffffffffffffffffffffffffffebaaedce6af48a03bbfd25e8cd0364141") - s).ToBigEndian(malleated.AsSpan(32, 32));
        malleated[64] = valid[64] == 27 ? (byte)28 : (byte)27;
        signatures.Add(malleated);
        return signatures;
    }

    [Test]
    public void Rejects_foreign_rollup_registry_chain_and_signer()
    {
        SequencerHeadCodec[] codecs = [
            new(1337, Registry, 2, Signer), new(1337, TestItem.AddressA, 1, Signer),
            new(1338, Registry, 1, Signer), new(1337, Registry, 1, TestItem.AddressB),
        ];
        using (Assert.EnterMultipleScope())
        {
            foreach (SequencerHeadCodec codec in codecs) Assert.That(codec.TryDecode(Compressed, out _), Is.False);
        }
    }

    [Test]
    public void Rejects_absent_block_extra_fields_and_trailing_bytes()
    {
        RlpReader outer = new(Envelope);
        RlpReader reader = outer.EnterSequence(out _);
        reader.SkipItem();
        byte[] signature = reader.DecodeByteArray();
        byte[] block = reader.DecodeByteArray();
        byte[][] invalid = [
            Rlp.Encode(EncodeClaim(Claim), Rlp.Encode(signature), Rlp.OfEmptyByteArray).Bytes,
            Rlp.Encode(EncodeClaim(Claim), Rlp.Encode(signature), Rlp.Encode(block), Rlp.OfZero).Bytes,
            [.. Envelope, 0],
        ];
        using (Assert.EnterMultipleScope())
        {
            foreach (byte[] encoded in invalid) Assert.That(_codec.TryDecode(Snappy.CompressToArray(encoded), out _), Is.False);
        }
    }

    [Test]
    public void Rejects_noncanonical_claim_integer()
    {
        RlpReader outer = new(Envelope);
        RlpReader reader = outer.EnterSequence(out _);
        reader.SkipItem();
        byte[] signature = reader.DecodeByteArray();
        byte[] block = reader.DecodeByteArray();
        Rlp claim = Rlp.Encode(new Rlp([0x81, 1]), Rlp.Encode(Claim.Number), Rlp.Encode(Claim.BlockHash),
            Rlp.Encode(Claim.ParentHash), Rlp.Encode(Claim.EpochMs), Rlp.Encode(Claim.Counter));
        byte[] encoded = Rlp.Encode(claim, Rlp.Encode(signature), Rlp.Encode(block)).Bytes;
        Assert.That(_codec.TryDecode(Snappy.CompressToArray(encoded), out _), Is.False);
    }

    [Test]
    public void Rejects_body_roots_that_do_not_match_a_validly_signed_header()
    {
        VerifiedSequencerHead head = Decode(Compressed);
        head.Block.Header.TxRoot = TestItem.KeccakA;
        SequencerHeadClaim claim = Claim with { BlockHash = head.Block.Header.CalculateHash() };
        byte[] bytes = SignEnvelope(claim, Rlp.Encode(head.Block).Bytes);
        Assert.That(_codec.TryDecode(Snappy.CompressToArray(bytes), out _), Is.False);
    }

    [Test]
    public void Rejects_claim_that_disagrees_with_its_block([Values("number", "parent", "hash")] string field)
    {
        VerifiedSequencerHead head = Decode(Compressed);
        SequencerHeadClaim claim = field switch
        {
            "number" => Claim with { Number = 8 },
            "parent" => Claim with { ParentHash = TestItem.KeccakA },
            _ => Claim with { BlockHash = TestItem.KeccakA },
        };
        byte[] bytes = SignEnvelope(claim, Rlp.Encode(head.Block).Bytes);
        Assert.That(_codec.TryDecode(Snappy.CompressToArray(bytes), out _), Is.False);
    }

    [Test]
    public void Accepts_every_optional_header_prefix([Range(15, 23)] int fields)
    {
        Rlp[] header = HeaderFields();
        header[15] = Rlp.OfZero;
        byte[] encoded = EncodeBlock(header[..fields], withdrawals: fields >= 17 ? Rlp.OfEmptyList : null);
        VerifiedSequencerHead head = Decode(encoded);
        Assert.That(head.Block.Hash, Is.EqualTo(Keccak.Compute(Rlp.Encode(header[..fields]).Bytes)));
    }

    [Test]
    public void Accepts_large_header_with_an_explicit_limit_without_changing_shared_default()
    {
        Rlp[] header = HeaderFields();
        header[12] = Rlp.Encode(new byte[5 * 1024 * 1024]);
        Rlp encodedHeader = Rlp.Encode(header);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => new HeaderDecoder().DecodeComplete(encodedHeader.Bytes), Throws.TypeOf<RlpLimitException>());
            Assert.That(_codec.TryDecode(EncodeBlock(header), out _), Is.True);
        }
    }

    [Test]
    public void Accepts_exact_decompressed_limit_and_literal_compression_above_that_limit()
    {
        Rlp[] header = HeaderFields();
        header[12] = Rlp.Encode(new byte[SequencerHeadCodec.MaxMessageSize - 1024]);
        byte[] first = Snappy.DecompressToArray(EncodeBlock(header));
        int payloadLength = SequencerHeadCodec.MaxMessageSize - 1024 + SequencerHeadCodec.MaxMessageSize - first.Length;
        header[12] = Rlp.Encode(new byte[payloadLength]);
        byte[] compressed = EncodeBlock(header);
        byte[] raw = Snappy.DecompressToArray(compressed);
        Assert.That(raw.Length, Is.EqualTo(SequencerHeadCodec.MaxMessageSize));
        byte[] literal = LiteralCompress(raw);
        Assert.That(literal.Length, Is.GreaterThan(SequencerHeadCodec.MaxMessageSize));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_codec.TryDecode(compressed, out _), Is.True);
            Assert.That(_codec.TryDecode(literal, out _), Is.True);
            Assert.That(_codec.MessageId(literal), Is.EqualTo(_codec.MessageId(compressed)));
        }
    }

    [Test]
    public void Rejects_a_valid_compressed_stream_expanding_above_the_limit()
    {
        byte[] oversized = Snappy.CompressToArray(new byte[SequencerHeadCodec.MaxMessageSize + 1]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_codec.TryDecode(oversized, out _), Is.False);
            Assert.That(_codec.MessageId(oversized), Is.EqualTo(InvalidMessageId(oversized)));
            Assert.That(SequencerHeadCodec.HasValidPreamble(new byte[SequencerHeadCodec.MaxFrameSize + 1]), Is.False);
        }
    }

    [Test]
    public void Rejects_validly_signed_ommer_and_withdrawal_root_mismatches([Values(1, 16)] int field)
    {
        Rlp[] header = HeaderFields();
        header[field] = Rlp.Encode(TestItem.KeccakA);
        Assert.That(_codec.TryDecode(EncodeBlock(header), out _), Is.False);
    }

    [Test]
    public void Accepts_zero_base_fee_ommer_headers_without_changing_their_shape()
    {
        Rlp[] uncle = HeaderFields()[..16];
        uncle[15] = Rlp.OfZero;
        Rlp uncles = Rlp.Encode([Rlp.Encode(uncle)]);
        Rlp[] header = HeaderFields();
        header[1] = Rlp.Encode(Keccak.Compute(uncles.Bytes));
        Assert.That(_codec.TryDecode(EncodeBlock(header, uncles: uncles), out _), Is.True);
    }

    [Test]
    public void Accepts_canonical_typed_transactions_and_rejects_unwrapped_copies()
    {
        Transaction transaction = Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(1337)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Rlp[] header = HeaderFields();
        header[4] = Rlp.Encode(TxTrie.CalculateRoot([transaction]));
        Rlp wrapped = TxDecoder.Instance.Encode(transaction);
        VerifiedSequencerHead head = Decode(EncodeBlock(header, transactions: Rlp.Encode([wrapped])));
        Rlp unwrapped = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(head.Block.Transactions[0].Hash, Is.EqualTo(transaction.Hash));
            Assert.That(_codec.TryDecode(EncodeBlock(header, transactions: Rlp.Encode([unwrapped])), out _), Is.False);
        }
    }

    [Test]
    public void Rejects_a_system_transaction_to_the_other_profile_destination()
    {
        Transaction transaction = SystemTransactions.Create(chainId: 1337, data: [1]);
        Rlp[] header = HeaderFields();
        header[4] = Rlp.Encode(TxTrie.CalculateRoot([transaction]));
        Assert.That(_codec.TryDecode(EncodeBlock(header, transactions: Rlp.Encode([TxDecoder.Instance.Encode(transaction)])), out _), Is.True);
        transaction.To = EezConstants.RollupXEezl2Address;
        transaction.Hash = null;
        header[4] = Rlp.Encode(TxTrie.CalculateRoot([transaction]));
        Assert.That(_codec.TryDecode(EncodeBlock(header, transactions: Rlp.Encode([TxDecoder.Instance.Encode(transaction)])), out _), Is.False);
    }

    [Test]
    public void Rejects_a_decodable_transaction_type_outside_the_profile()
    {
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = 1337,
            SenderAddress = TestItem.AddressA,
            Frames = [new TxFrame(FrameMode.Default, FrameFlags.None, TestItem.AddressB, 100000, 0, UInt256.Zero, ReadOnlyMemory<byte>.Empty)],
            FrameSignatures = [],
        };
        Rlp encoded = TxDecoder.Instance.Encode(transaction);
        RlpReader reader = new(encoded.Bytes);
        Transaction decoded = TxDecoder.Instance.DecodeGuardNotNull(ref reader, RlpBehaviors.SkipPooledTransactions | RlpBehaviors.ExcludeHashes);
        Rlp[] header = HeaderFields();
        header[4] = Rlp.Encode(TxTrie.CalculateRoot([transaction]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.Type, Is.EqualTo(TxType.FrameTx));
            Assert.That(_codec.TryDecode(EncodeBlock(header, transactions: Rlp.Encode([encoded])), out _), Is.False);
        }
    }

    [Test]
    public void Accepts_a_nonempty_withdrawal_list_with_the_matching_root()
    {
        Rlp withdrawal = Rlp.Encode(Rlp.Encode(1UL), Rlp.Encode(2UL), Rlp.Encode(TestItem.AddressA.Bytes), Rlp.Encode(3UL));
        Withdrawal item = new WithdrawalDecoder().DecodeCompleteNotNull(withdrawal.Bytes);
        Rlp[] header = HeaderFields();
        header[16] = Rlp.Encode(WithdrawalTrie.CalculateRoot([item]));
        VerifiedSequencerHead head = Decode(EncodeBlock(header, withdrawals: Rlp.Encode([withdrawal])));
        Assert.That(head.Block.Withdrawals, Has.Length.EqualTo(1));
    }

    [Test]
    public void Rejects_unsigned_body_changes_before_transaction_allocations()
    {
        Rlp[] transactions = new Rlp[100000];
        Array.Fill(transactions, new Rlp(Bytes.FromHexString("c98080808080801b0101")));
        Rlp block = Rlp.Encode(Rlp.Encode(HeaderFields()[..21]), Rlp.Encode(transactions), Rlp.OfEmptyList, Rlp.OfEmptyList);
        RlpReader outer = new(Envelope);
        RlpReader reader = outer.EnterSequence(out _);
        reader.SkipItem();
        byte[] signature = reader.DecodeByteArray();
        byte[] message = Snappy.CompressToArray(Rlp.Encode(EncodeClaim(Claim), Rlp.Encode(signature), Rlp.Encode(block.Bytes)).Bytes);
        Decode(Compressed);
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool accepted = _codec.TryDecode(message, out _);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(accepted, Is.False);
            Assert.That(allocated, Is.LessThan(4 * 1024 * 1024));
        }
    }

    [Test]
    public void Rejects_empty_optional_header_hashes([Values(19, 20, 21)] int field)
    {
        Rlp[] header = HeaderFields();
        header[field] = Rlp.OfEmptyByteArray;
        Assert.That(_codec.TryDecode(EncodeBlock(header), out _), Is.False);
    }

    [Test]
    public void Rejects_padded_snappy_preamble_before_decompression()
    {
        byte[] padded = [Compressed[0], (byte)(Compressed[1] | 0x80), 0, .. Compressed.AsSpan(2)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SequencerHeadCodec.HasValidPreamble(padded), Is.False);
            Assert.That(_codec.TryDecode(padded, out _), Is.False);
            Assert.That(_codec.MessageId(padded), Is.EqualTo(InvalidMessageId(padded)));
        }
    }

    [TestCaseSource(nameof(InvalidSnappy))]
    public void Rejects_invalid_or_oversized_snappy(byte[] bytes)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_codec.TryDecode(bytes, out _), Is.False);
            Assert.That(_codec.MessageId(bytes), Is.EqualTo(InvalidMessageId(bytes)));
        }
    }

    private static IEnumerable<byte[]> InvalidSnappy() => [
        [], [0x80], [1, 0], [0x81, 0x80, 0x80, 5], [0x80, 0x80, 0x80, 0x80, 0],
    ];

    [Test]
    public void Tracker_preserves_first_equivocation_and_orders_sources_by_epoch()
    {
        VerifiedSequencerHead head = Decode(Compressed);
        SequencerHeadTracker tracker = new();
        Observe(tracker, head, Claim.EpochMs, SequencerHeadVerdict.Accept);
        Observe(tracker, head, Claim.EpochMs, SequencerHeadVerdict.Ignore);
        Observe(tracker, head with { Claim = Claim with { BlockHash = TestItem.KeccakA } }, Claim.EpochMs, SequencerHeadVerdict.Reject);
        AssertCandidates(tracker, head);
        VerifiedSequencerHead newer = head with { Claim = Claim with { EpochMs = Claim.EpochMs + 1, Counter = 1 } };
        Observe(tracker, newer, Claim.EpochMs, SequencerHeadVerdict.Accept);
        AssertCandidates(tracker, newer, head);
    }

    [Test]
    public void Tracker_limits_sources_and_does_not_remember_future_heads()
    {
        VerifiedSequencerHead head = Decode(Compressed);
        SequencerHeadTracker tracker = new();
        Observe(tracker, head, Claim.EpochMs - SequencerHeadTracker.MaxFutureMs - 1, SequencerHeadVerdict.Ignore);
        AssertCandidates(tracker);
        for (ulong epoch = 1; epoch <= SequencerHeadTracker.MaxSources + 1; epoch++)
            Observe(tracker, head with { Claim = Claim with { EpochMs = epoch } }, epoch, SequencerHeadVerdict.Accept);
        Assert.That(Array.ConvertAll(tracker.Candidates(), item => item.Claim.EpochMs), Is.EqualTo(new ulong[] { 9, 8, 7, 6, 5, 4, 3, 2 }));
        Observe(tracker, head with { Claim = Claim with { EpochMs = 1 } }, 1, SequencerHeadVerdict.Ignore);
    }

    [Test]
    public void Tracker_replaces_higher_counters_and_ignores_lower_counters()
    {
        VerifiedSequencerHead head = Decode(Compressed);
        SequencerHeadTracker tracker = new();
        Observe(tracker, head, Claim.EpochMs, SequencerHeadVerdict.Accept);
        VerifiedSequencerHead newer = head with { Claim = Claim with { Counter = 3 } };
        Observe(tracker, newer, Claim.EpochMs, SequencerHeadVerdict.Accept);
        Observe(tracker, head with { Claim = Claim with { Counter = 2 } }, Claim.EpochMs, SequencerHeadVerdict.Ignore);
        AssertCandidates(tracker, newer);
    }

    [Test]
    public void Tracker_accepts_the_exact_future_boundary()
    {
        VerifiedSequencerHead head = Decode(Compressed);
        SequencerHeadTracker tracker = new();
        Observe(tracker, head, Claim.EpochMs - SequencerHeadTracker.MaxFutureMs, SequencerHeadVerdict.Accept);
    }

    private static void Observe(SequencerHeadTracker tracker, VerifiedSequencerHead head, ulong now, SequencerHeadVerdict expected)
    {
        SequencerHeadVerdict actual = tracker.Observe(head, now);
        Assert.That(actual, Is.EqualTo(expected));
    }

    private static void AssertCandidates(SequencerHeadTracker tracker, params VerifiedSequencerHead[] expected) =>
        Assert.That(tracker.Candidates(), Is.EqualTo(expected));

    private VerifiedSequencerHead Decode(byte[] bytes)
    {
        Assert.That(_codec.TryDecode(bytes, out VerifiedSequencerHead? head), Is.True);
        return head!;
    }

    private byte[] EncodeBlock(Rlp[] header, Rlp? transactions = null, Rlp? uncles = null, Rlp? withdrawals = null)
    {
        Rlp encodedHeader = Rlp.Encode(header);
        Rlp block = header.Length >= 17
            ? Rlp.Encode(encodedHeader, transactions ?? Rlp.OfEmptyList, uncles ?? Rlp.OfEmptyList, withdrawals ?? Rlp.OfEmptyList)
            : Rlp.Encode(encodedHeader, transactions ?? Rlp.OfEmptyList, uncles ?? Rlp.OfEmptyList);
        SequencerHeadClaim claim = Claim with { BlockHash = Keccak.Compute(encodedHeader.Bytes) };
        return Snappy.CompressToArray(SignEnvelope(claim, block.Bytes));
    }

    private static Rlp[] HeaderFields()
    {
        RlpReader outer = new(Envelope);
        RlpReader envelope = outer.EnterSequence(out _);
        envelope.SkipItem();
        envelope.SkipItem();
        RlpReader blockOuter = new(envelope.DecodeByteArraySpan());
        RlpReader block = blockOuter.EnterSequence(out _);
        RlpReader header = block.EnterSequence(out _);
        List<Rlp> fields = [];
        while (header.HasNext) fields.Add(new Rlp(header.ReadEncodedItem().ToArray()));
        fields.Add(Rlp.Encode(Keccak.Zero));
        fields.Add(Rlp.OfZero);
        return fields.ToArray();
    }

    private static Rlp EncodeClaim(in SequencerHeadClaim claim) => Rlp.Encode(
        Rlp.Encode(claim.RollupId), Rlp.Encode(claim.Number), Rlp.Encode(claim.BlockHash),
        Rlp.Encode(claim.ParentHash), Rlp.Encode(claim.EpochMs), Rlp.Encode(claim.Counter));

    private byte[] SignEnvelope(SequencerHeadClaim claim, byte[] block)
    {
        using PrivateKey key = new("ac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80");
        Signature signature = new Ecdsa().Sign(key, _codec.SigningHash(claim).ValueHash256);
        byte[] bytes = [.. signature.Bytes, (byte)signature.V];
        return Rlp.Encode(EncodeClaim(claim), Rlp.Encode(bytes), Rlp.Encode(block)).Bytes;
    }

    private static byte[] InvalidMessageId(byte[] data)
    {
        byte[] topic = Encoding.ASCII.GetBytes("/eez/1337/1/heads/0");
        byte[] bytes = new byte[12 + topic.Length + data.Length];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), (ulong)topic.Length);
        topic.CopyTo(bytes, 12);
        data.CopyTo(bytes, 12 + topic.Length);
        return SHA256.HashData(bytes)[..20];
    }

    private static byte[] LiteralCompress(byte[] bytes)
    {
        List<byte> prefix = [];
        uint length = (uint)bytes.Length;
        while (length >= 128)
        {
            prefix.Add((byte)((length & 0x7f) | 0x80));
            length >>= 7;
        }
        prefix.Add((byte)length);
        uint last = (uint)bytes.Length - 1;
        int width = last <= byte.MaxValue ? 1 : last <= ushort.MaxValue ? 2 : 3;
        prefix.Add((byte)((59 + width) << 2));
        for (int index = 0; index < width; index++) prefix.Add((byte)(last >> (index * 8)));
        byte[] data = new byte[prefix.Count + bytes.Length];
        prefix.CopyTo(data);
        bytes.CopyTo(data, prefix.Count);
        return data;
    }

    private static byte[] LoadEnvelope()
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "Fixtures", "p2p", "signed-head-v0.json");
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        return Bytes.FromHexString(json.RootElement.GetProperty("envelope").GetString()!);
    }
}
