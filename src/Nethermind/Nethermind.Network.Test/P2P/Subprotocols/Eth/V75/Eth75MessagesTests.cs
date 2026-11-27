// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V75;
using Nethermind.Network.P2P.Subprotocols.Eth.V75.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V75;

public class Eth75MessagesTests
{
    private static readonly string VectorDirectory = Path.Combine(TestContext.CurrentContext.TestDirectory, "P2P", "Subprotocols", "Eth", "V75", "Vectors");

    private static JsonElement LoadVectors(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(VectorDirectory, $"eip8437-{name}-vectors.json"))).RootElement;

    private static IEnumerable<TestCaseData> AnnouncementVectors()
    {
        foreach (JsonElement vector in LoadVectors("announcement").EnumerateArray())
            yield return new TestCaseData(vector.GetProperty("mode").GetInt32(), vector.GetProperty("size").GetInt32(),
                vector.GetProperty("content_hash").GetString(), vector.GetProperty("announcement_rlp").GetString())
                .SetArgDisplayNames($"mode {vector.GetProperty("mode").GetInt32()}, {vector.GetProperty("size").GetInt32()} bytes");
    }

    private static IEnumerable<TestCaseData> WrapperVectors()
    {
        foreach (JsonProperty vector in LoadVectors("wrapper").EnumerateObject())
            if (vector.Value.ValueKind == JsonValueKind.Object) yield return new TestCaseData(vector.Value).SetArgDisplayNames(vector.Name);
    }

    private static byte[] Pattern(int size) => [.. Enumerable.Range(0, size).Select(i => (byte)(i % 251))];

    // ethereum/EIPs#12712, assets/eip-8437/announcement-vectors.json: wrapper[i] = i % 251, announced without metadata.
    [TestCaseSource(nameof(AnnouncementVectors))]
    public void Announcement_matches_eip8437_announcement_vectors(int mode, int size, string hash, string entryRlp)
    {
        WrapperAnnouncement announcement = WrapperAnnouncement.Create(Pattern(size), mode, default);
        WrapperAnnouncement? decoded = WrapperAnnouncement.Decode(Bytes.FromHexString(entryRlp), out ValueHash256 decodedHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcement.Hash.ToString(false), Is.EqualTo(hash));
            Assert.That(announcement.Encoded.ToHexString(), Is.EqualTo(entryRlp));
            Assert.That(decodedHash, Is.EqualTo(announcement.Hash));
            Assert.That((decoded!.Mode, decoded.Length, decoded.Metadata), Is.EqualTo((mode, size, (WrapperMetadata?)null)));
        }
    }

    // ethereum/EIPs#12712, assets/eip-8437/wrapper-vectors.json: full-envelope mode-0 wrappers, one with a blob-carrying
    // transaction in its network form. The synthetic frames carry no gas limits, so the checker's fixed 100,000 stands in.
    [TestCaseSource(nameof(WrapperVectors))]
    public void Wrapper_vectors_match_their_metadata_announcement_and_messages(JsonElement vector)
    {
        byte[] body = Hex(vector, "body");
        ValueHash256 hash = new(Hex(vector, "content_hash"));
        WrapperMetadata metadata = WrapperMetadata.Of(body, static (ref Eth75RlpReader frames) => 100_000);
        WrapperAnnouncement? decoded = WrapperAnnouncement.Decode(Hex(vector, "announcement_rlp"), out _);
        ValueHash256[] transactions = WrapperBodies.ParseWrapper(body, out WrapperShape shape);
        ProofWrappersMessage response = new ProofWrappersMessageSerializer().Decode(Hex(vector, "response_rlp"));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ValueKeccak.Compute(body), Is.EqualTo(hash), "the hash covers the complete wrapper, envelopes included");
            Assert.That(shape.Mode, Is.EqualTo(MempoolWrapper.ModeDirect));
            Assert.That(transactions.Select(static h => h.ToString(false)),
                Is.EqualTo(vector.GetProperty("transaction_hashes").EnumerateArray().Select(static h => h.GetString())), "ordered by consensus hash");
            Assert.That(metadata.Encoded.ToHexString(), Is.EqualTo(vector.GetProperty("transaction_metadata_rlp").GetString()));
            Assert.That(WrapperMetadata.Decode(metadata.Encoded, body.Length, Eth75Protocol.MaxInlineTransactions).Encoded, Is.EqualTo(metadata.Encoded));
            Assert.That(WrapperAnnouncement.Create(body, 0, default, metadata).Encoded.ToHexString(),
                Is.EqualTo(vector.GetProperty("announcement_rlp").GetString()));
            Assert.That((decoded!.Hash, decoded.Length, decoded.Metadata), Is.EqualTo((hash, body.Length, metadata)));
            Assert.That(new ProofWrappersMessageSerializer().Encode(new(7, [new(hash, body)])), Is.EqualTo(Hex(vector, "response_rlp")));
            Assert.That((response.RequestId, response.Entries.Single().Hash, response.Entries.Single().Wrapper), Is.EqualTo((7L, hash, body)));
            Assert.That(new GetProofWrappersMessageSerializer().Encode(new(8, false, [hash])), Is.EqualTo(Hex(vector, "request_rlp")));
        }
    }

    private static byte[] Hex(JsonElement vector, string name) => Bytes.FromHexString(vector.GetProperty(name).GetString()!);

    private const string Mode1Body = "f90163f888b84206f83f0180940000000000000000000000000000000000000000e1a00000000000000000000000000000000000000000000000000000000000000000c0c3010180c0b84206f83f0101940000000000000000000000000000000000000000e1a00000000000000000000000000000000000000000000000000000000000000000c0c3010180c001f8d6f8c4b860000000000000000000000000000000000000000000000000000000000000001006eaaa302366a2b8e60080fe649cb019814b843b9bd681df41d60bde4badd3790000000000000000000000000000000000000000000000000000000000000001b8600000000000000000000000000000000000000000000000000000000000000010f3bc792f5a550e9e8271695b0d0c1ee9b8d42b9071976b6447658021b3d8099f00000000000000000000000000000000000000000000000000000000000000028f6167677265676174652070726f6f66";
    private const string Mode1Compact = "f9014a07f90146f90143a0082aec04a3cd181c4a376032dc0819b192a8b39ab14839f3fffb09089d1f9638f9011ff844e1a074a44b10cf2efcdcd904f1c13bc82f5862c6b8e0d89ed3ab9f3df5ac42b22e72e1a0a9627cc76ad0b92ebe067018f96cf3acc2d3411fea2c23441c019c780a88e39a01f8d6f8c4b860000000000000000000000000000000000000000000000000000000000000001006eaaa302366a2b8e60080fe649cb019814b843b9bd681df41d60bde4badd3790000000000000000000000000000000000000000000000000000000000000001b8600000000000000000000000000000000000000000000000000000000000000010f3bc792f5a550e9e8271695b0d0c1ee9b8d42b9071976b6447658021b3d8099f00000000000000000000000000000000000000000000000000000000000000028f6167677265676174652070726f6f66";
    private static readonly string[] TransactionHashes =
    [
        "74a44b10cf2efcdcd904f1c13bc82f5862c6b8e0d89ed3ab9f3df5ac42b22e72",
        "a9627cc76ad0b92ebe067018f96cf3acc2d3411fea2c23441c019c780a88e39a"
    ];

    // Both vectors share one dependency list and one transaction set, so their hints are equal.
    private const string VectorDepsHash = "f1a05553412fee67bfa84b3fa65697d8d9d752ca56158a416f53cf7664f79545";

    // wrapper-vectors.json blob_mode1: a blob-carrying frame transaction in its network form, blob data elided, is ordered and
    // requested by its consensus hash, while its commitments and cell proofs are part of the wrapper's identity.
    [Test]
    public void Blob_wrapper_vector_uses_consensus_hashes_and_binds_blob_metadata()
    {
        byte[] blob = BlobTransaction(2);
        byte[] body = BlobWrapper(blob);
        ValueHash256[] transactions = WrapperBodies.ParseWrapper(body, out _);
        byte[] altered = BlobTransaction(2, alterProof: true);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(ValueKeccak.Compute(body).ToString(false), Is.EqualTo("292a0168039d849bb2911f277cd4b4eb67e9bef42d282eb530ed6e0e5c4ea9c8"));
            Assert.That(WrapperAnnouncement.Create(body).Coverage, Is.EqualTo(new WrapperCoverage(new ValueHash256(VectorDepsHash),
                new ValueHash256("4c6e5a209bf8767ceecfc626ca7e837f6da15c865df639f41510d7da1df7f914"))), "the transaction digest uses consensus hashes");
            Assert.That(transactions.Select(static h => h.ToString(false)),
                Is.EqualTo(new[] { "5cd30939bb47312756827dae080429249177bd7f61effb31fea35f40061c3d18" }.Concat(TransactionHashes)));
            Assert.That(ValueKeccak.Compute(blob).ToString(false), Is.EqualTo("326120544192bb4ac70f41145387674e51badba806182abd014e0c43ee2054f7"),
                "the encoding hash differs from the consensus hash");
            Assert.That(WrapperBodies.TransactionHash(altered), Is.EqualTo(WrapperBodies.TransactionHash(blob)));
            Assert.That(ValueKeccak.Compute(BlobWrapper(altered)), Is.Not.EqualTo(ValueKeccak.Compute(body)));
        }
    }

    // check_vectors.py: nonempty blob data, mismatched commitments or wrong cell proof counts are rejected, as is a
    // blob-carrying transaction without its network wrapper.
    [TestCase("blob data")]
    [TestCase("version")]
    [TestCase("commitment count")]
    [TestCase("commitment encoding")]
    [TestCase("commitment hash")]
    [TestCase("missing proof")]
    [TestCase("extra proof")]
    [TestCase("proof encoding")]
    [TestCase("missing network wrapper")]
    public void Blob_metadata_violations_are_rejected(string scenario)
    {
        byte[] encoded = BlobTransaction(2, scenario: scenario);
        Assert.That(() => WrapperBodies.TransactionHash(encoded), Throws.InstanceOf<RlpException>());
    }

    [TestCase(0)]
    [TestCase(Eth75Protocol.MaxWrapperBytes + 1)]
    public void Wrapper_length_outside_one_to_max_wrapper_bytes_is_rejected(int length)
    {
        Assert.That(() => WrapperAnnouncement.Create(new byte[length], 0, default), Throws.ArgumentException);
        foreach (ulong mode in new ulong[] { 0, 1 })
            Assert.That(() => WrapperAnnouncement.Decode(Entry(Eth75Rlp.EncodeUInt(mode), (ulong)length, Hash32()), out _), Throws.InstanceOf<RlpException>(),
                "the length is checked for unsupported modes too");
    }

    private static byte[] Hash32(int length = 32) => Eth75Rlp.EncodeBytes(new byte[length]);

    private static byte[] Entry(byte[] mode, ulong length, byte[] hash, params byte[][] metadata) =>
        Eth75Rlp.EncodeList([mode, Eth75Rlp.EncodeUInt(length), hash, .. metadata.Length == 0 ? [Eth75Rlp.EncodeList()] : metadata]);

    // check_vectors.py: an entry has exactly four fields, a 32-byte hash, a canonical integer mode and a metadata list.
    [TestCase("list mode")]
    [TestCase("noncanonical mode")]
    [TestCase("three fields")]
    [TestCase("five fields")]
    [TestCase("eth/76 shape")]
    [TestCase("short hash")]
    [TestCase("string metadata")]
    public void Announcement_fields_are_checked(string scenario)
    {
        byte[] encoded = scenario switch
        {
            "list mode" => Entry(Eth75Rlp.EncodeList(), 1, Hash32()),
            "noncanonical mode" => Entry([0x81, 0x01], 1, Hash32()),
            "three fields" => Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(0), Eth75Rlp.EncodeUInt(1), Hash32()),
            "five fields" => Entry(Eth75Rlp.EncodeUInt(0), 1, Hash32(), Eth75Rlp.EncodeList(), Eth75Rlp.EncodeList()),
            "eth/76 shape" => Entry(Eth75Rlp.EncodeUInt(0), 1, Hash32(), Hash32(), Hash32(), Eth75Rlp.EncodeList()),
            "short hash" => Entry(Eth75Rlp.EncodeUInt(0), 1, Hash32(31)),
            _ => Entry(Eth75Rlp.EncodeUInt(0), 1, Hash32(), Eth75Rlp.EncodeBytes([]))
        };
        Assert.That(() => WrapperAnnouncement.Decode(encoded, out _), Throws.InstanceOf<RlpException>());
    }

    // check_vectors.py: entries of unsupported modes are ignored once the message's encoding, count, length and ordering pass;
    // on eth/75 only mode 0 is supported.
    [TestCase(new byte[] { 0x01 })]
    [TestCase(new byte[] { 0x02 })]
    [TestCase(new byte[] { 0x82, 0x01, 0x00 })]
    [TestCase(new byte[] { 0x89, 0x01, 0, 0, 0, 0, 0, 0, 0, 0 })]
    public void Unsupported_modes_are_ignored_after_the_message_checks(byte[] mode)
    {
        NewProofWrapperHashesMessageSerializer serializer = new();
        byte[] supported = Entry(Eth75Rlp.EncodeUInt(0), 1, Eth75Rlp.EncodeBytes(Word(2)));
        byte[] unsupported = Entry(mode, 1, Eth75Rlp.EncodeBytes(Word(1)), Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes([1])));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(WrapperAnnouncement.Decode(unsupported, out _), Is.Null);
            Assert.That(serializer.Decode(Eth75Rlp.EncodeList(unsupported, supported)).Announcements.Select(static a => a.Hash),
                Is.EqualTo(new[] { new ValueHash256(Word(2)) }));
            Assert.That(serializer.Decode(Eth75Rlp.EncodeList(unsupported)).Announcements, Is.Empty);
            Assert.That(() => serializer.Decode(Eth75Rlp.EncodeList(supported, unsupported)), Throws.InstanceOf<RlpException>(), "ordering covers every entry");
        }
    }

    private static byte[] Word(int value)
    {
        byte[] word = new byte[32];
        word[^1] = (byte)value;
        return word;
    }

    // check_vectors.py: uneven metadata lists, unordered hashes, zero or excessive sizes, misshapen fees or out-of-range values,
    // and more than 17 transactions make the announcement malformed.
    [TestCase("valid", false)]
    [TestCase("no transactions", true)]
    [TestCase("four fields", true)]
    [TestCase("uneven lists", true)]
    [TestCase("zero size", true)]
    [TestCase("sizes reach the length", true)]
    [TestCase("unordered hashes", true)]
    [TestCase("short source", true)]
    [TestCase("fee over 256 bits", true)]
    [TestCase("fee not a list", true)]
    [TestCase("gas over 64 bits", true)]
    [TestCase("18 transactions", true)]
    [TestCase("no fees", false)]
    [TestCase("four fees", false)]
    public void Metadata_is_checked_on_arrival(string scenario, bool malformed)
    {
        const int length = 1000;
        int count = scenario == "18 transactions" ? 18 : 2;
        byte[][] hashes = [.. Enumerable.Range(1, count).Select(static i => Eth75Rlp.EncodeBytes(Word(i)))];
        byte[] Ints(params ulong[] values) => Eth75Rlp.EncodeList([.. values.Select(static v => Eth75Rlp.EncodeUInt(v))]);
        byte[] Repeat(byte[] item) => Eth75Rlp.EncodeList([.. Enumerable.Repeat(item, count)]);
        byte[] fee = scenario switch
        {
            "fee over 256 bits" => Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes([1, .. new byte[32]])),
            "no fees" => Eth75Rlp.EncodeList(),
            "four fees" => Ints(1, 2, 3, 4),
            _ => Ints(7, 3)
        };
        List<byte[]> fields =
        [
            scenario switch { "zero size" => Ints(0, 1), "sizes reach the length" => Ints(length / 2, length / 2), _ => Repeat(Eth75Rlp.EncodeUInt(10)) },
            scenario == "unordered hashes" ? Eth75Rlp.EncodeList(hashes[1], hashes[0]) : Eth75Rlp.EncodeList(hashes),
            scenario == "short source" ? Repeat(Eth75Rlp.EncodeBytes(new byte[19])) : Repeat(Eth75Rlp.EncodeBytes(new byte[20])),
            scenario == "fee not a list" ? Repeat(Eth75Rlp.EncodeUInt(1)) : Repeat(fee),
            scenario == "gas over 64 bits" ? Repeat(Eth75Rlp.EncodeBytes([1, .. new byte[8]])) : Repeat(Eth75Rlp.EncodeUInt(ulong.MaxValue)),
        ];
        if (scenario == "no transactions") fields = [.. fields.Select(static _ => Eth75Rlp.EncodeList())];
        if (scenario == "uneven lists") fields[4] = Ints(1);
        if (scenario == "four fields") fields.RemoveAt(4);
        byte[] metadata = Eth75Rlp.EncodeList([.. fields]);
        byte[] entry = Entry(Eth75Rlp.EncodeUInt(0), length, Hash32(), metadata);
        if (malformed) Assert.That(() => WrapperAnnouncement.Decode(entry, out _), Throws.InstanceOf<RlpException>());
        else Assert.That(WrapperAnnouncement.Decode(entry, out _)!.Metadata!.Encoded, Is.EqualTo(metadata));
    }

    // Transaction metadata reads the explicit sender, past EIP-8250 nonce keys, the fees list and the summed frame limits; it is
    // inline only for mode-0 wrappers of at most 17 transactions.
    [Test]
    public void Metadata_is_derived_from_the_wrapper_transactions()
    {
        Transaction[] transactions = [.. Enumerable.Range(0, 18).Select(WrapperTestObjects.FrameTransaction)];
        transactions[0].SenderAddress = TestItem.AddressA;
        transactions[0].MaxFee = 7;
        transactions[0].GasPrice = 3;
        transactions[0].Hash = new Hash256(ValueKeccak.Compute(WrapperTestObjects.Envelope(transactions[0])));
        byte[] direct = WrapperTestObjects.DirectWrapper(transactions[0]);
        WrapperMetadata metadata = WrapperMetadata.InlineFor(direct)!;
        Eth75RlpReader outer = new(metadata.Encoded);
        Eth75RlpReader fields = outer.ReadList();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fields.ReadList().ReadUInt64(), Is.EqualTo((ulong)WrapperTestObjects.Envelope(transactions[0]).Length));
            Assert.That(fields.ReadList().ReadHash(), Is.EqualTo(transactions[0].Hash!.ValueHash256));
            Assert.That(fields.ReadList().ReadBytes().ToArray(), Is.EqualTo(TestItem.AddressA.Bytes.ToArray()));
            Eth75RlpReader fees = fields.ReadList().ReadList();
            Assert.That((fees.ReadUInt64(), fees.ReadUInt64()), Is.EqualTo((7UL, 3UL)));
            Assert.That(fields.ReadList().ReadUInt64(), Is.EqualTo(FrameTxValidation.TotalGasLimit(transactions[0].Frames)));
            fields.End();
            Assert.That(WrapperAnnouncement.Create(direct).Metadata, Is.EqualTo(metadata));
            Assert.That(WrapperMetadata.InlineFor(WrapperTestObjects.Wrapper(64, transactions[0])), Is.Null, "mode 1 carries no inline metadata");
            Assert.That(WrapperMetadata.InlineFor(WrapperTestObjects.DirectWrapper(transactions[..17])), Is.Not.Null);
            Assert.That(WrapperMetadata.InlineFor(WrapperTestObjects.DirectWrapper(transactions)), Is.Null, "18 transactions exceed the inline budget");
            Assert.That(WrapperMetadata.Of(WrapperTestObjects.DirectWrapper(transactions)).Count, Is.EqualTo(18));
        }
    }

    // check_vectors.py: hints identify equal coverage despite different proof bytes, a shared dependency list does not identify
    // the transactions, and a changed scheme byte changes the dependency digest.
    [Test]
    public void Coverage_hints_identify_equal_transaction_and_dependency_sets()
    {
        Transaction[] transactions = [.. Enumerable.Range(0, 2).Select(WrapperTestObjects.FrameTransaction)];
        Transaction other = WrapperTestObjects.FrameTransaction(0);
        other.Nonce = 9;
        other.Hash = new Hash256(ValueKeccak.Compute(WrapperTestObjects.Envelope(other)));
        WrapperCoverage original = WrapperAnnouncement.Create(WrapperTestObjects.Wrapper(64, 1, transactions)).Coverage!.Value;
        WrapperCoverage reproven = WrapperAnnouncement.Create(WrapperTestObjects.Wrapper(64, 2, transactions)).Coverage!.Value;
        WrapperCoverage otherTransactions = WrapperAnnouncement.Create(WrapperTestObjects.Wrapper(64, 1, other, transactions[1])).Coverage!.Value;
        WrapperCoverage leanStark = WrapperAnnouncement.Create(WrapperTestObjects.Wrapper(64, 1,
            [.. Enumerable.Range(0, 2).Select(static i => WrapperTestObjects.FrameTransaction(i, Eip8288Constants.LeanStarkScheme))])).Coverage!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reproven, Is.EqualTo(original));
            Assert.That(otherTransactions.DepsHash, Is.EqualTo(original.DepsHash));
            Assert.That(otherTransactions.TxsHash, Is.Not.EqualTo(original.TxsHash));
            Assert.That(leanStark.DepsHash, Is.Not.EqualTo(original.DepsHash));
        }
    }

    [Test]
    public void Message_ids_follow_eth74_and_have_size_ceilings()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new[] { Eth75MessageCode.NewProofWrapperHashes, Eth75MessageCode.GetProofWrappers, Eth75MessageCode.ProofWrappers },
                Is.EqualTo(new[] { 0x18, 0x19, 0x1a }));
            Assert.That(Eth75Protocol.MessageIdSpaceSize, Is.EqualTo(0x1b));
            Assert.That(Eth75MessageCode.IsWrapperMessage(0x17), Is.False, "eth/74 DependencyProofs");
            Assert.That(Eth75Protocol.MaxWrapperBytes, Is.EqualTo(Eth75Protocol.SoftResponseLimit));
            // The largest valid response reaches just under the soft limit, then adds one maximum wrapper.
            Assert.That(ProofWrapperBroadcaster.ResponseLength(-1, Eth75Protocol.SoftResponseLimit - 1
                    + ProofWrapperBroadcaster.EntryLength(Eth75Protocol.MaxWrapperBytes) + (Eth75Protocol.MaxWrappersPerRequest - 1) * 37),
                Is.LessThanOrEqualTo(Eth75Protocol.LocalMaxResponseBytes));
            Assert.That(Eth75Protocol.ExceedsMessageLimit(Eth75MessageCode.ProofWrappers, Eth75Protocol.LocalMaxResponseBytes), Is.False);
            Assert.That(Eth75Protocol.ExceedsMessageLimit(Eth75MessageCode.ProofWrappers, Eth75Protocol.LocalMaxResponseBytes + 1), Is.True);
            Assert.That(Eth75Protocol.ExceedsMessageLimit(Eth75MessageCode.GetProofWrappers, Eth75Protocol.MaxGetProofWrappersBytes + 1), Is.True);
            Assert.That(Eth75Protocol.ExceedsMessageLimit(Eth75MessageCode.NewProofWrapperHashes, Eth75Protocol.MaxNewProofWrapperHashesBytes), Is.False);
            Assert.That(Eth75Protocol.ExceedsMessageLimit(Eth75MessageCode.NewProofWrapperHashes, Eth75Protocol.MaxNewProofWrapperHashesBytes + 1), Is.True);
            Assert.That(Eth75Protocol.ExceedsMessageLimit(0x0a, int.MaxValue), Is.False, "PooledTransactions keeps its eth limits");
            Assert.That(Eth75Protocol.ExceedsMessageLimit(0x17, int.MaxValue), Is.False, "eth/74 messages keep their own limits");
        }
    }

    // check_vectors.py: 64 announcements without metadata encode in 2,563 bytes, with the widest 17-transaction metadata in
    // 186,436, and 32 requested hashes in 1,071.
    [Test]
    public void Messages_at_their_entry_limits_match_eip8437_lengths()
    {
        byte[][] widestHashes = [.. Enumerable.Range(0, 17).Select(static i => ValueKeccak.Compute([(byte)i])).Order().Select(static h => Eth75Rlp.EncodeBytes(h.Bytes))];
        byte[] Repeat(byte[] item) => Eth75Rlp.EncodeList([.. Enumerable.Repeat(item, 17)]);
        byte[] widest = Eth75Rlp.EncodeList(Repeat(Eth75Rlp.EncodeUInt((Eth75Protocol.MaxWrapperBytes - 1) / 17)), Eth75Rlp.EncodeList(widestHashes),
            Repeat(Eth75Rlp.EncodeBytes(Enumerable.Repeat((byte)0xff, 20).ToArray())),
            Repeat(Eth75Rlp.EncodeList([.. Enumerable.Repeat(Eth75Rlp.EncodeBytes(Enumerable.Repeat((byte)0xff, 32).ToArray()), 3)])),
            Repeat(Eth75Rlp.EncodeUInt(ulong.MaxValue)));
        byte[] Announcements(params byte[][] metadata) => Eth75Rlp.EncodeList([.. Enumerable.Range(1, Eth75Protocol.MaxAnnouncements)
            .Select(i => Entry(Eth75Rlp.EncodeUInt(0), Eth75Protocol.MaxWrapperBytes, Eth75Rlp.EncodeBytes(Word(i)), metadata))]);
        ValueHash256[] requested = [.. Enumerable.Range(1, Eth75Protocol.MaxWrappersPerRequest).Select(static i => new ValueHash256(Word(i)))];
        NewProofWrapperHashesMessageSerializer serializer = new();
        using (Assert.EnterMultipleScope())
        {
            byte[] bare = Announcements();
            Assert.That(serializer.Encode(serializer.Decode(bare)), Has.Length.EqualTo(2563));
            byte[] full = Announcements(widest);
            Assert.That(full, Has.Length.EqualTo(186_436).And.Length.LessThan(Eth75Protocol.MaxNewProofWrapperHashesBytes));
            Assert.That(serializer.Encode(serializer.Decode(full)), Is.EqualTo(full));
            Assert.That(() => serializer.Decode(Eth75Rlp.EncodeList([.. Enumerable.Range(1, Eth75Protocol.MaxAnnouncements + 1)
                .Select(static i => Entry(Eth75Rlp.EncodeUInt(0), 1, Eth75Rlp.EncodeBytes(Word(i))))])), Throws.InstanceOf<RlpException>());
            Assert.That(new GetProofWrappersMessageSerializer().Encode(new(-1, false, requested)), Has.Length.EqualTo(Eth75Protocol.MaxGetProofWrappersBytes).And.Length.EqualTo(1071));
            Assert.That(new GetProofWrappersMessageSerializer().Encode(new(7, false, [default])).ToHexString(), Is.EqualTo("e307e1a0" + new string('0', 64)));
            Assert.That(() => new GetProofWrappersMessageSerializer().Encode(new(7, true, [default])), Throws.ArgumentException, "eth/75 has no compact form");
        }
    }

    // check_vectors.py: fee lists have no fixed length, but the whole announcement, metadata included and whatever its mode, is
    // bounded at 256 KiB.
    [Test]
    public void Announcement_payload_is_bounded_at_256_KiB([Values(0, 2)] int mode)
    {
        byte[] maxFee = Eth75Rlp.EncodeBytes(Enumerable.Repeat((byte)0xff, 32).ToArray());
        List<byte[]> fees = [.. Enumerable.Repeat(maxFee, Eth75Protocol.MaxNewProofWrapperHashesBytes / 66)];
        byte[] Payload() => Eth75Rlp.EncodeList(Entry(Eth75Rlp.EncodeUInt((ulong)mode), Eth75Protocol.MaxWrapperBytes, Hash32(),
            Eth75Rlp.EncodeList(Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(10)), Eth75Rlp.EncodeList(Hash32()),
                Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(new byte[20])), Eth75Rlp.EncodeList(Eth75Rlp.EncodeList([.. fees])),
                Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(1)))));
        int remaining = Eth75Protocol.MaxNewProofWrapperHashesBytes - Payload().Length;
        fees.AddRange(Enumerable.Repeat(maxFee, remaining / maxFee.Length));
        fees.AddRange(Enumerable.Repeat(Eth75Rlp.EncodeUInt(1), remaining % maxFee.Length));
        byte[] largest = Payload();
        fees.Add(Eth75Rlp.EncodeUInt(1));
        byte[] oversized = Payload();
        NewProofWrapperHashesMessageSerializer serializer = new();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(largest, Has.Length.EqualTo(1 << 18));
            Assert.That(serializer.Decode(largest).Announcements, Has.Length.EqualTo(mode == 0 ? 1 : 0));
            Assert.That(oversized, Has.Length.EqualTo(largest.Length + 1));
            Assert.That(() => serializer.Decode(oversized), Throws.InstanceOf<RlpException>());
        }
    }

    // check_vectors.py: a request ID may be any value below 2^64; the response echoes it.
    [Test]
    public void Request_ids_up_to_two_to_the_64_minus_one_are_accepted_and_echoed()
    {
        byte[] encoded = Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(ulong.MaxValue), Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(new byte[32])));
        GetProofWrappersMessage request = new GetProofWrappersMessageSerializer().Decode(encoded);
        byte[] response = new ProofWrappersMessageSerializer().Encode(new(request.RequestId, []));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Compact, Is.False);
            Assert.That(new GetProofWrappersMessageSerializer().Encode(request), Is.EqualTo(encoded));
            Assert.That(response, Is.EqualTo(Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(ulong.MaxValue), Eth75Rlp.EncodeList())));
            Assert.That(response.Length, Is.EqualTo(ProofWrapperBroadcaster.ResponseLength(request.RequestId, 0)));
        }
    }

    [Test]
    public void Messages_round_trip_canonically()
    {
        WrapperAnnouncement[] announcements = [.. Enumerable.Range(1, 3).Select(static i => WrapperAnnouncement.Create(new byte[i], 0, default)).OrderBy(d => d.Hash)];
        byte[] wrapper = Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(5));
        ProofWrapperEntry entry = new(ValueKeccak.Compute(wrapper), wrapper);
        using (Assert.EnterMultipleScope())
        {
            NewProofWrapperHashesMessageSerializer announce = new();
            Assert.That(announce.Decode(announce.Encode(new(announcements))).Announcements.Select(d => d.Hash), Is.EqualTo(announcements.Select(d => d.Hash)));
            GetProofWrappersMessageSerializer get = new();
            GetProofWrappersMessage request = get.Decode(get.Encode(new(42, false, [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256])));
            Assert.That((request.RequestId, request.Compact, request.Hashes.Length), Is.EqualTo((42L, false, 2)));
            ProofWrappersMessageSerializer wrappers = new();
            byte[] encoded = wrappers.Encode(new(42, [entry, entry]));
            Assert.That(encoded.Length, Is.EqualTo(ProofWrapperBroadcaster.ResponseLength(42, 2 * ProofWrapperBroadcaster.EntryLength(wrapper.Length))));
            Assert.That(wrappers.Decode(encoded).Entries, Is.EqualTo(new[] { entry, entry }).Using<ProofWrapperEntry>(static (a, b) => a.Hash == b.Hash && a.Wrapper.AsSpan().SequenceEqual(b.Wrapper)),
                "wrappers are embedded RLP items");
        }
    }

    // compact_response_rlp: a hash reference is an RLP list in place of a transaction string. With the cached encodings the
    // identical canonical wrapper is reconstructed; without them the item is discarded for a complete retry; a reconstruction
    // longer than the announced length is rejected.
    [Test]
    public void Compact_response_vector_reconstructs_the_canonical_wrapper()
    {
        ProofWrappersMessage message = new ProofWrappersMessageSerializer().Decode(Bytes.FromHexString(Mode1Compact));
        byte[] body = Bytes.FromHexString(Mode1Body);
        byte[] compact = message.Entries.Single().Wrapper;
        System.Collections.Generic.Dictionary<ValueHash256, byte[]> cache = WrapperCompaction.Encodings(body).ToDictionary(static e => e.Hash, static e => e.Encoding);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.RequestId, Is.EqualTo(7));
            Assert.That(message.Entries[0].Hash.ToString(false), Is.EqualTo("082aec04a3cd181c4a376032dc0819b192a8b39ab14839f3fffb09089d1f9638"));
            Assert.That(() => WrapperBodies.ParseWrapper(compact, out _), Throws.InstanceOf<RlpException>(), "references are unresolved");
            Assert.That(WrapperCompaction.Compact(body, cache.ContainsKey), Is.EqualTo(compact), "both encodings are replaced");
            Assert.That(WrapperCompaction.CompactLength(body, [true, true]), Is.EqualTo(compact.Length), "sized from headers alone");
            Assert.That(WrapperCompaction.CompactLength(body, [false, false]), Is.EqualTo(body.Length));
            Assert.That(WrapperCompaction.CountTransactions(body), Is.EqualTo(2));
            Assert.That(WrapperCompaction.Reconstruct(compact, body.Length, h => cache.GetValueOrDefault(h), out byte[]? rebuilt, out _),
                Is.EqualTo(WrapperCompaction.Outcome.Complete));
            Assert.That(rebuilt, Is.EqualTo(body));
            Assert.That(WrapperCompaction.Reconstruct(compact, body.Length, static _ => null, out _, out _), Is.EqualTo(WrapperCompaction.Outcome.Missing));
            Assert.That(WrapperCompaction.Reconstruct(compact, body.Length - 1, h => cache.GetValueOrDefault(h), out _, out string? error),
                Is.EqualTo(WrapperCompaction.Outcome.Malformed));
            Assert.That(error, Does.Contain("announced length"));
            Assert.That(WrapperCompaction.EncodingHashes(body).Select(static h => h.ToString(false)), Is.EqualTo(TransactionHashes),
                "without blobs, encoding hashes equal transaction hashes");
        }
    }

    [TestCase("unordered announcement")]
    [TestCase("empty announcement")]
    [TestCase("duplicate request")]
    [TestCase("too many requested")]
    [TestCase("compact flag")]
    [TestCase("request id over 64 bits")]
    [TestCase("string wrapper")]
    [TestCase("entry without hash")]
    [TestCase("short entry hash")]
    [TestCase("too many entries")]
    [TestCase("trailing bytes")]
    public void Malformed_messages_are_rejected_before_allocation(string scenario)
    {
        WrapperAnnouncement[] announcements = [.. Enumerable.Range(1, 2).Select(static i => WrapperAnnouncement.Create(new byte[i], 0, default)).OrderByDescending(d => d.Hash)];
        byte[] Id(ulong value) => Eth75Rlp.EncodeUInt(value);
        byte[] Hash(Hash256 hash) => Eth75Rlp.EncodeBytes(hash.Bytes);
        byte[] wrapper = Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(5));
        byte[] Entry() => Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(ValueKeccak.Compute(wrapper).Bytes), wrapper);
        Action decode = scenario switch
        {
            "unordered announcement" => () => new NewProofWrapperHashesMessageSerializer().Decode(Eth75Rlp.EncodeList(announcements[0].Encoded, announcements[1].Encoded)),
            "empty announcement" => () => new NewProofWrapperHashesMessageSerializer().Decode(Eth75Rlp.EncodeList()),
            "duplicate request" => () => new GetProofWrappersMessageSerializer().Decode(Eth75Rlp.EncodeList(Id(1), Eth75Rlp.EncodeList(Hash(TestItem.KeccakA), Hash(TestItem.KeccakA)))),
            "too many requested" => () => new GetProofWrappersMessageSerializer().Decode(Eth75Rlp.EncodeList(Id(1),
                Eth75Rlp.EncodeList([.. Enumerable.Range(0, Eth75Protocol.MaxWrappersPerRequest + 1).Select(i => Eth75Rlp.EncodeBytes(Keccak.Compute(i.ToString()).Bytes))]))),
            "compact flag" => () => new GetProofWrappersMessageSerializer().Decode(Eth75Rlp.EncodeList(Id(1), Id(0), Eth75Rlp.EncodeList(Hash(TestItem.KeccakA)))),
            "request id over 64 bits" => () => new GetProofWrappersMessageSerializer().Decode(Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes([1, .. new byte[8]]), Eth75Rlp.EncodeList(Hash(TestItem.KeccakA)))),
            "string wrapper" => () => new ProofWrappersMessageSerializer().Decode(Eth75Rlp.EncodeList(Id(1), Eth75Rlp.EncodeList(
                Eth75Rlp.EncodeList(Hash(TestItem.KeccakA), Eth75Rlp.EncodeBytes([1, 2]))))),
            "entry without hash" => () => new ProofWrappersMessageSerializer().Decode(Eth75Rlp.EncodeList(Id(1), Eth75Rlp.EncodeList(wrapper))),
            "short entry hash" => () => new ProofWrappersMessageSerializer().Decode(Eth75Rlp.EncodeList(Id(1), Eth75Rlp.EncodeList(
                Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(new byte[31]), wrapper)))),
            "too many entries" => () => new ProofWrappersMessageSerializer().Decode(Eth75Rlp.EncodeList(Id(1),
                Eth75Rlp.EncodeList([.. Enumerable.Range(0, Eth75Protocol.MaxWrappersPerRequest + 1).Select(_ => Entry())]))),
            _ => () => new ProofWrappersMessageSerializer().Decode([.. Eth75Rlp.EncodeList(Id(1), Eth75Rlp.EncodeList()), 0x80])
        };
        Assert.That(decode, Throws.InstanceOf<RlpException>());
    }

    // EIP-8288 Test Case 12: empty transaction lists, unresolved references and duplicate or reordered transactions are rejected
    // before validation, as are empty or unordered dependency lists and a mode-0 witness count other than the dependency count
    // (EIP-8437 wrapper encoding). A dependency list wider than the mode-0 limits parses: EIP-8437 leaves dependency-rule failures
    // to validation, which discards them without penalty.
    [TestCase("valid")]
    [TestCase("trailing")]
    [TestCase("empty")]
    [TestCase("reference")]
    [TestCase("duplicate")]
    [TestCase("reordered")]
    [TestCase("not a frame transaction")]
    [TestCase("aggregate proof")]
    [TestCase("wide dependency list")]
    [TestCase("unsorted dependencies")]
    [TestCase("no dependencies")]
    [TestCase("missing witness")]
    public void Canonical_wrapper_parser_rejects_noncanonical_bodies(string scenario)
    {
        Transaction[] transactions = [.. Enumerable.Range(0, 2).Select(WrapperTestObjects.FrameTransaction).OrderBy(static t => t.Hash!.ToString())];
        byte[] valid = WrapperTestObjects.Wrapper(64, transactions);
        byte[] Rebuilt(IEnumerable<byte[]> items, byte[]? content = null, ulong mode = 1)
        {
            Eth75RlpReader outer = new(valid);
            Eth75RlpReader fields = outer.ReadList();
            fields.ReadItem();
            fields.ReadItem();
            return Eth75Rlp.EncodeList(Eth75Rlp.EncodeList([.. items]), Eth75Rlp.EncodeUInt(mode), content ?? fields.ReadItem().ToArray());
        }
        byte[][] encoded = [.. transactions.Select(static t => Eth75Rlp.EncodeBytes(WrapperTestObjects.Envelope(t)))];
        byte[][] wide = [.. Enumerable.Range(0, Eip8288Constants.MaxLeanSigDepsPerWrapper + 1)
            .Select(static i => Eth75Rlp.EncodeBytes(Eip8288Dependencies.Serialize([new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute([(byte)i, (byte)(i >> 8)]), default)])))
            .OrderBy(static d => d.ToHexString())];
        byte[] wrapper = scenario switch
        {
            "valid" => valid,
            "trailing" => [.. valid, 0x80],
            "empty" => Rebuilt([]),
            "reference" => Rebuilt([Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(transactions[0].Hash!.Bytes)), encoded[1]]),
            "duplicate" => Rebuilt([encoded[0], encoded[0]]),
            "reordered" => Rebuilt([encoded[1], encoded[0]]),
            "not a frame transaction" => Rebuilt([Eth75Rlp.EncodeBytes([2, 0xc0])]),
            "aggregate proof" => Rebuilt(encoded, Eth75Rlp.EncodeList(Eth75Rlp.EncodeList([.. Eip8288Dependencies.Canonicalize(
                    transactions.SelectMany(static t => Eip8288Dependencies.ForTransaction(t, FrameTxSigHash.ComputeValue)))
                .Select(static d => Eth75Rlp.EncodeBytes(Eip8288Dependencies.Serialize([d])))]), Eth75Rlp.EncodeBytes(new byte[Eip8288Constants.MaxAggregateProofBytes + 1]))),
            "wide dependency list" => Rebuilt(encoded, Eth75Rlp.EncodeList(Eth75Rlp.EncodeList([.. wide]), Eth75Rlp.EncodeBytes([1]))),
            "unsorted dependencies" => Rebuilt(encoded, Eth75Rlp.EncodeList(Eth75Rlp.EncodeList([.. wide.AsEnumerable().Reverse()]), Eth75Rlp.EncodeBytes([1]))),
            "no dependencies" => Rebuilt(encoded, Eth75Rlp.EncodeList(Eth75Rlp.EncodeList(), Eth75Rlp.EncodeBytes([1]))),
            _ => Rebuilt(encoded, Eth75Rlp.EncodeList(Eth75Rlp.EncodeList([.. wide]), Eth75Rlp.EncodeList([.. wide.Skip(1).Select(static _ => Eth75Rlp.EncodeBytes([1]))])), 0)
        };
        if (scenario is "valid" or "wide dependency list")
            Assert.That(WrapperBodies.ParseWrapper(wrapper, out _), Is.EqualTo(transactions.Select(static t => t.Hash!.ValueHash256)));
        else
            Assert.That(() => WrapperBodies.ParseWrapper(wrapper, out _), Throws.InstanceOf<RlpException>());
    }

    /// <summary>The check_vectors.py synthetic blob-carrying frame transaction in its network form, blob data elided.</summary>
    private static byte[] BlobTransaction(int nonce, bool alterProof = false, string? scenario = null)
    {
        byte[][] commitments = [.. Enumerable.Range(0, 2).Select(static i => Enumerable.Repeat((byte)(i + 1), 48).ToArray())];
        byte[][] versioned = [.. commitments.Select(static c =>
        {
            byte[] hash = SHA256.HashData(c);
            hash[0] = 0x01;
            return hash;
        })];
        byte[][] proofs = [.. Enumerable.Range(0, 256).Select(static i => Enumerable.Repeat((byte)(i % 251), 48).ToArray())];
        if (alterProof) proofs[0] = Enumerable.Repeat((byte)42, 48).ToArray();
        byte[] Body() => Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeUInt((ulong)nonce), Eth75Rlp.EncodeBytes(new byte[20]),
            Eth75Rlp.EncodeList(Eth75Rlp.EncodeBytes(new byte[32])), Eth75Rlp.EncodeList(),
            Eth75Rlp.EncodeList(Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeUInt(1)),
            Eth75Rlp.EncodeList([.. versioned.Select(static v => Eth75Rlp.EncodeBytes(v))]));
        byte[] List(IEnumerable<byte[]> items) => Eth75Rlp.EncodeList([.. items.Select(static i => Eth75Rlp.EncodeBytes(i))]);
        byte[] payload = scenario switch
        {
            "missing network wrapper" => Body(),
            "blob data" => Eth75Rlp.EncodeList(Body(), Eth75Rlp.EncodeUInt(1), List([new byte[131072]]), List(commitments), List(proofs)),
            "version" => Eth75Rlp.EncodeList(Body(), Eth75Rlp.EncodeUInt(0), Eth75Rlp.EncodeList(), List(commitments), List(proofs)),
            "commitment count" => Eth75Rlp.EncodeList(Body(), Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeList(), List(commitments[..1]), List(proofs)),
            "commitment encoding" => Eth75Rlp.EncodeList(Body(), Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeList(), List([new byte[47], new byte[47]]), List(proofs)),
            "commitment hash" => Eth75Rlp.EncodeList(Body(), Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeList(), List([commitments[1], commitments[0]]), List(proofs)),
            "missing proof" => Eth75Rlp.EncodeList(Body(), Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeList(), List(commitments), List(proofs[..^1])),
            "extra proof" => Eth75Rlp.EncodeList(Body(), Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeList(), List(commitments), List([.. proofs, new byte[48]])),
            "proof encoding" => Eth75Rlp.EncodeList(Body(), Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeList(), List(commitments), List(proofs.Select(static _ => new byte[47]))),
            _ => Eth75Rlp.EncodeList(Body(), Eth75Rlp.EncodeUInt(1), Eth75Rlp.EncodeList(), List(commitments), List(proofs))
        };
        return [(byte)TxType.FrameTx, .. payload];
    }

    /// <summary>The blob_mode1 vector: the mode-1 vector's transactions and content with <paramref name="blob"/> added.</summary>
    private static byte[] BlobWrapper(byte[] blob)
    {
        byte[] mode1 = Bytes.FromHexString(Mode1Body);
        Eth75RlpReader outer = new(mode1);
        Eth75RlpReader fields = outer.ReadList();
        Eth75RlpReader transactions = fields.ReadList();
        List<byte[]> envelopes = [blob];
        while (transactions.HasMore) envelopes.Add(transactions.ReadBytes().ToArray());
        fields.ReadItem();
        byte[] content = fields.ReadItem().ToArray();
        return Eth75Rlp.EncodeList(Eth75Rlp.EncodeList([.. envelopes.OrderBy(static e => WrapperBodies.TransactionHash(e)).Select(static e => Eth75Rlp.EncodeBytes(e))]),
            Eth75Rlp.EncodeUInt(1), content);
    }
}
