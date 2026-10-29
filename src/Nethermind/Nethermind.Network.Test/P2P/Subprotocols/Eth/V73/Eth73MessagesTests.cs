// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

public class Eth73MessagesTests
{
    public sealed record AnnouncementVector(int Mode, int Size, string Hash, string EntryRlp)
    {
        public override string ToString() => $"mode {Mode}, {Size} bytes";
    }

    // ethereum/EIPs#12423 at 877ebb177e, assets/eip-8437/announcement-vectors.json: wrapper[i] = i % 251, with the hints
    // keccak("dependency hint") and keccak("transaction hint").
    private const string HintSuffix = "a04021e96337602bed6a4fdc56dd92b9e72c94b1adf721a07302099f25a10e9a88"
        + "a0282f6556e2fd9030eef5265de0a1e830b4ce1656de39b731e3880c2545d01647";

    private static IEnumerable<AnnouncementVector> AnnouncementVectors() =>
    [
        new(0, 1, "bc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a",
            "f8658001a0bc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a" + HintSuffix),
        new(1, 65536, "5c8672dff277ec0f0321f710ce2e8756362daec795f5ff9bf7cb8c4e2e6fb2ec",
            "f8680183010000a05c8672dff277ec0f0321f710ce2e8756362daec795f5ff9bf7cb8c4e2e6fb2ec" + HintSuffix),
        new(0, 131073, "35ae5a437a69fef01f453512998f4e90f1ba613a1d10962f80ebefcc836dbd09",
            "f8688083020001a035ae5a437a69fef01f453512998f4e90f1ba613a1d10962f80ebefcc836dbd09" + HintSuffix),
        new(1, 2097152, "8b993b9a88858899c909e2e2873f952ec0d86539c64834bb768670c88e95c715",
            "f8680183200000a08b993b9a88858899c909e2e2873f952ec0d86539c64834bb768670c88e95c715" + HintSuffix),
    ];

    private static readonly WrapperCoverage Hints = new(ValueKeccak.Compute("dependency hint"u8), ValueKeccak.Compute("transaction hint"u8));

    private static byte[] Pattern(int size) => [.. Enumerable.Range(0, size).Select(i => (byte)(i % 251))];

    [TestCaseSource(nameof(AnnouncementVectors))]
    public void Announcement_matches_eip8437_announcement_vectors(AnnouncementVector vector)
    {
        WrapperAnnouncement announcement = WrapperAnnouncement.Create(Pattern(vector.Size), vector.Mode, Hints);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcement.Hash.ToString(false), Is.EqualTo(vector.Hash));
            Assert.That(announcement.Length, Is.EqualTo(vector.Size));
            Assert.That(announcement.Mode, Is.EqualTo(vector.Mode));
            Assert.That(announcement.Encoded.ToHexString(), Is.EqualTo(vector.EntryRlp));
            Assert.That(announcement.Encoded.Length, Is.LessThanOrEqualTo(WrapperAnnouncement.MaxEncodedBytes));
            WrapperAnnouncement decoded = WrapperAnnouncement.Decode(announcement.Encoded);
            Assert.That((decoded.Mode, decoded.Length, decoded.Hash, decoded.Coverage),
                Is.EqualTo((announcement.Mode, announcement.Length, announcement.Hash, Hints)));
        }
    }

    private const string Mode0Body = "f90168f888b84206f83f0180940000000000000000000000000000000000000000e1a00000000000000000000000000000000000000000000000000000000000000000c0c3010180c0b84206f83f0101940000000000000000000000000000000000000000e1a00000000000000000000000000000000000000000000000000000000000000000c0c3010180c080f8dbf8c4b860000000000000000000000000000000000000000000000000000000000000001006eaaa302366a2b8e60080fe649cb019814b843b9bd681df41d60bde4badd3790000000000000000000000000000000000000000000000000000000000000001b8600000000000000000000000000000000000000000000000000000000000000010f3bc792f5a550e9e8271695b0d0c1ee9b8d42b9071976b6447658021b3d8099f0000000000000000000000000000000000000000000000000000000000000002d4897769746e6573732030897769746e6573732031";
    private const string Mode1Body = "f90163f888b84206f83f0180940000000000000000000000000000000000000000e1a00000000000000000000000000000000000000000000000000000000000000000c0c3010180c0b84206f83f0101940000000000000000000000000000000000000000e1a00000000000000000000000000000000000000000000000000000000000000000c0c3010180c001f8d6f8c4b860000000000000000000000000000000000000000000000000000000000000001006eaaa302366a2b8e60080fe649cb019814b843b9bd681df41d60bde4badd3790000000000000000000000000000000000000000000000000000000000000001b8600000000000000000000000000000000000000000000000000000000000000010f3bc792f5a550e9e8271695b0d0c1ee9b8d42b9071976b6447658021b3d8099f00000000000000000000000000000000000000000000000000000000000000028f6167677265676174652070726f6f66";
    private const string Mode1Compact = "f9014a07f90146f90143a0082aec04a3cd181c4a376032dc0819b192a8b39ab14839f3fffb09089d1f9638f9011ff844e1a074a44b10cf2efcdcd904f1c13bc82f5862c6b8e0d89ed3ab9f3df5ac42b22e72e1a0a9627cc76ad0b92ebe067018f96cf3acc2d3411fea2c23441c019c780a88e39a01f8d6f8c4b860000000000000000000000000000000000000000000000000000000000000001006eaaa302366a2b8e60080fe649cb019814b843b9bd681df41d60bde4badd3790000000000000000000000000000000000000000000000000000000000000001b8600000000000000000000000000000000000000000000000000000000000000010f3bc792f5a550e9e8271695b0d0c1ee9b8d42b9071976b6447658021b3d8099f00000000000000000000000000000000000000000000000000000000000000028f6167677265676174652070726f6f66";
    private static readonly string[] TransactionHashes =
    [
        "74a44b10cf2efcdcd904f1c13bc82f5862c6b8e0d89ed3ab9f3df5ac42b22e72",
        "a9627cc76ad0b92ebe067018f96cf3acc2d3411fea2c23441c019c780a88e39a"
    ];

    // Both vectors share one dependency list and one transaction set, so their hints are equal.
    private const string VectorDepsHash = "f1a05553412fee67bfa84b3fa65697d8d9d752ca56158a416f53cf7664f79545";
    private const string VectorTxsHash = "831988e1a8f6f1d8e8a2c2dd71a88b04ece9817358f798778fef2c11a46ff5a7";

    // ethereum/EIPs#12423 at 877ebb177e, assets/eip-8437/wrapper-vectors.json: full-envelope mode-0 and mode-1 wrappers.
    [TestCase(Mode0Body, 0, "12d976e1d5cc5599f04fc56041652594d399e7a9b62ed268e7df3d01306cc455",
        "f8678082016ba012d976e1d5cc5599f04fc56041652594d399e7a9b62ed268e7df3d01306cc455a0" + VectorDepsHash + "a0" + VectorTxsHash,
        "f9019307f9018ff9018ca012d976e1d5cc5599f04fc56041652594d399e7a9b62ed268e7df3d01306cc455")]
    [TestCase(Mode1Body, 1, "082aec04a3cd181c4a376032dc0819b192a8b39ab14839f3fffb09089d1f9638",
        "f86701820166a0082aec04a3cd181c4a376032dc0819b192a8b39ab14839f3fffb09089d1f9638a0" + VectorDepsHash + "a0" + VectorTxsHash,
        "f9018e07f9018af90187a0082aec04a3cd181c4a376032dc0819b192a8b39ab14839f3fffb09089d1f9638")]
    public void Wrapper_vectors_are_canonical_and_match_their_announcement(string body, int mode, string hash, string entryRlp, string responsePrefix)
    {
        byte[] wrapper = Bytes.FromHexString(body);
        WrapperAnnouncement announcement = WrapperAnnouncement.Create(wrapper);
        ValueHash256[] transactions = WrapperBodies.ParseWrapper(wrapper, out WrapperShape shape);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcement.Hash.ToString(false), Is.EqualTo(hash), "the hash covers the complete wrapper, envelopes included");
            Assert.That(announcement.Encoded.ToHexString(), Is.EqualTo(entryRlp), "the announcement carries the wrapper's own mode and hints");
            Assert.That(announcement.Coverage.DepsHash.ToString(false), Is.EqualTo(VectorDepsHash));
            Assert.That(announcement.Coverage.TxsHash.ToString(false), Is.EqualTo(VectorTxsHash));
            Assert.That(shape.Mode, Is.EqualTo(mode));
            // response_rlp: ProofWrappers [7, [[hash, wrapper]]] embeds the wrapper as an RLP item after its hash.
            Assert.That(new ProofWrappersMessageSerializer().Encode(new(7, [new(announcement.Hash, wrapper)])).ToHexString(),
                Is.EqualTo(responsePrefix + body));
            // compact_request_rlp: GetProofWrappers [7, 1, [hash]]; full_request_rlp: [8, 0, [hash]].
            Assert.That(new GetProofWrappersMessageSerializer().Encode(new(7, true, [announcement.Hash])).ToHexString(),
                Is.EqualTo("e40701e1a0" + hash));
            Assert.That(new GetProofWrappersMessageSerializer().Encode(new(8, false, [announcement.Hash])).ToHexString(),
                Is.EqualTo("e40880e1a0" + hash));
            Assert.That(transactions.Select(static h => h.ToString(false)), Is.EqualTo(TransactionHashes));
        }
    }

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
            Assert.That(WrapperAnnouncement.Create(body).Encoded.ToHexString(),
                Is.EqualTo("f8670182335ba0292a0168039d849bb2911f277cd4b4eb67e9bef42d282eb530ed6e0e5c4ea9c8a0" + VectorDepsHash
                    + "a04c6e5a209bf8767ceecfc626ca7e837f6da15c865df639f41510d7da1df7f914"), "the transaction digest uses consensus hashes");
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
    [TestCase(Eth73Protocol.MaxWrapperBytes + 1)]
    public void Wrapper_length_outside_one_to_max_wrapper_bytes_is_rejected(int length)
    {
        Assert.That(() => WrapperAnnouncement.Create(new byte[length], 0, default), Throws.ArgumentException);
        byte[] encoded = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(0), Eth73Rlp.EncodeUInt((ulong)length), Hash32(), Hash32(), Hash32());
        Assert.That(() => WrapperAnnouncement.Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    private static byte[] Hash32(int length = 32) => Eth73Rlp.EncodeBytes(new byte[length]);

    // check_vectors.py: modes other than 0 and 1, a mode encoded as a byte string, entries without exactly five fields and hints
    // other than 32 bytes fail.
    [TestCase("mode 2")]
    [TestCase("mode 256")]
    [TestCase("string mode")]
    [TestCase("noncanonical mode")]
    [TestCase("two fields")]
    [TestCase("three fields")]
    [TestCase("four fields")]
    [TestCase("six fields")]
    [TestCase("short dependency hash")]
    [TestCase("short transaction hash")]
    public void Announcement_fields_and_mode_are_checked(string scenario)
    {
        static byte[] Length() => Eth73Rlp.EncodeUInt(1);
        byte[] encoded = scenario switch
        {
            "mode 2" => Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(2), Length(), Hash32(), Hash32(), Hash32()),
            "mode 256" => Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(256), Length(), Hash32(), Hash32(), Hash32()),
            "string mode" => Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes([1, 0]), Length(), Hash32(), Hash32(), Hash32()),
            "noncanonical mode" => Eth73Rlp.EncodeList([0x81, 0x01], Length(), Hash32(), Hash32(), Hash32()),
            "two fields" => Eth73Rlp.EncodeList(Length(), Hash32()),
            "three fields" => Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(0), Length(), Hash32()),
            "four fields" => Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(0), Length(), Hash32(), Eth73Rlp.EncodeBytes([])),
            "six fields" => Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(0), Length(), Hash32(), Hash32(), Hash32(), Hash32()),
            "short dependency hash" => Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(0), Length(), Hash32(), Hash32(31), Hash32()),
            _ => Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(0), Length(), Hash32(), Hash32(), Hash32(31))
        };
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => WrapperAnnouncement.Decode(encoded), Throws.InstanceOf<RlpException>());
            Assert.That(() => WrapperAnnouncement.Create(new byte[1], 2, default), Throws.ArgumentException);
            Assert.That(WrapperAnnouncement.Decode(Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(1), Length(), Hash32(), Hash32(), Hash32())).Mode, Is.EqualTo(1));
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
        WrapperAnnouncement original = WrapperAnnouncement.Create(WrapperTestObjects.Wrapper(64, 1, transactions));
        WrapperAnnouncement reproven = WrapperAnnouncement.Create(WrapperTestObjects.Wrapper(64, 2, transactions));
        WrapperAnnouncement otherTransactions = WrapperAnnouncement.Create(WrapperTestObjects.Wrapper(64, 1, other, transactions[1]));
        WrapperAnnouncement leanStark = WrapperAnnouncement.Create(WrapperTestObjects.Wrapper(64, 1,
            [.. Enumerable.Range(0, 2).Select(static i => WrapperTestObjects.FrameTransaction(i, Eip8288Constants.LeanStarkScheme))]));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(reproven.Hash, Is.Not.EqualTo(original.Hash));
            Assert.That(reproven.Coverage, Is.EqualTo(original.Coverage));
            Assert.That(otherTransactions.Coverage.DepsHash, Is.EqualTo(original.Coverage.DepsHash));
            Assert.That(otherTransactions.Coverage.TxsHash, Is.Not.EqualTo(original.Coverage.TxsHash));
            Assert.That(leanStark.Coverage.DepsHash, Is.Not.EqualTo(original.Coverage.DepsHash));
        }
    }

    [Test]
    public void Message_ids_follow_eth72_and_have_size_ceilings()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new[] { Eth73MessageCode.NewProofWrapperHashes, Eth73MessageCode.GetProofWrappers, Eth73MessageCode.ProofWrappers },
                Is.EqualTo(new[] { 0x16, 0x17, 0x18 }));
            Assert.That(Eth73Protocol.MessageIdSpaceSize, Is.EqualTo(0x19));
            Assert.That(Eth73MessageCode.IsWrapperMessage(0x19), Is.False);
            Assert.That(Eth73Protocol.MaxWrapperBytes, Is.EqualTo(Eth73Protocol.SoftResponseLimit));
            // The largest valid response reaches just under the soft limit, then adds one maximum wrapper.
            Assert.That(ProofWrapperTransport.ResponseLength(-1, Eth73Protocol.SoftResponseLimit - 1
                    + ProofWrapperTransport.EntryLength(Eth73Protocol.MaxWrapperBytes) + (Eth73Protocol.MaxWrappersPerRequest - 1) * 37),
                Is.LessThanOrEqualTo(Eth73Protocol.LocalMaxResponseBytes));
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.ProofWrappers, Eth73Protocol.LocalMaxResponseBytes), Is.False);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.ProofWrappers, Eth73Protocol.LocalMaxResponseBytes + 1), Is.True);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.GetProofWrappers, Eth73Protocol.MaxGetProofWrappersBytes + 1), Is.True);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.NewProofWrapperHashes, Eth73Protocol.MaxNewProofWrapperHashesBytes), Is.False);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.NewProofWrapperHashes, Eth73Protocol.MaxNewProofWrapperHashesBytes + 1), Is.True);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(0x0a, int.MaxValue), Is.False, "PooledTransactions keeps its eth limits");
            Assert.That(Eth73Protocol.ExceedsMessageLimit(0x15, int.MaxValue), Is.False, "eth/72 messages keep their own limits");
        }
    }

    // assets/eip-8437/check_vectors.py: 64 announcements encode in 6,787 bytes and 32 requested hashes in 1,072.
    [Test]
    public void Messages_at_their_entry_limits_match_eip8437_lengths()
    {
        WrapperAnnouncement[] announced = [.. Enumerable.Range(1, Eth73Protocol.MaxAnnouncements).Select(i => WrapperAnnouncement.Decode(
            Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)(i % 2)), Eth73Rlp.EncodeUInt(Eth73Protocol.MaxWrapperBytes), Eth73Rlp.EncodeBytes(Word(i)),
                Hash32(), Hash32())))];
        ValueHash256[] requested = [.. Enumerable.Range(1, Eth73Protocol.MaxWrappersPerRequest).Select(i => new ValueHash256(Word(i)))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new NewProofWrapperHashesMessageSerializer().Encode(new(announced)), Has.Length.EqualTo(Eth73Protocol.MaxNewProofWrapperHashesBytes).And.Length.EqualTo(6787));
            // A maximum-length entry encodes in 106 bytes in either mode.
            Assert.That(announced.Select(a => a.Encoded.Length).Distinct(), Is.EqualTo(new[] { WrapperAnnouncement.MaxEncodedBytes }));
            Assert.That(new GetProofWrappersMessageSerializer().Encode(new(-1, true, requested)), Has.Length.EqualTo(Eth73Protocol.MaxGetProofWrappersBytes).And.Length.EqualTo(1072));
            Assert.That(new GetProofWrappersMessageSerializer().Encode(new(7, false, [default])).ToHexString(), Is.EqualTo("e40780e1a0" + new string('0', 64)));
        }

        static byte[] Word(int value)
        {
            byte[] word = new byte[32];
            word[^1] = (byte)value;
            return word;
        }
    }

    // check_vectors.py: a request ID may be any value below 2^64; the response echoes it.
    [Test]
    public void Request_ids_up_to_two_to_the_64_minus_one_are_accepted_and_echoed()
    {
        byte[] encoded = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(ulong.MaxValue), Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes(new byte[32])));
        GetProofWrappersMessage request = new GetProofWrappersMessageSerializer().Decode(encoded);
        byte[] response = new ProofWrappersMessageSerializer().Encode(new(request.RequestId, []));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Compact, Is.True);
            Assert.That(new GetProofWrappersMessageSerializer().Encode(request), Is.EqualTo(encoded));
            Assert.That(response, Is.EqualTo(Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(ulong.MaxValue), Eth73Rlp.EncodeList())));
            Assert.That(response.Length, Is.EqualTo(ProofWrapperTransport.ResponseLength(request.RequestId, 0)));
        }
    }

    [Test]
    public void Messages_round_trip_canonically()
    {
        WrapperAnnouncement[] announcements = [.. Enumerable.Range(1, 3).Select(i => WrapperAnnouncement.Create(new byte[i], i % 2, default)).OrderBy(d => d.Hash)];
        byte[] wrapper = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(5));
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
            Assert.That(encoded.Length, Is.EqualTo(ProofWrapperTransport.ResponseLength(42, 2 * ProofWrapperTransport.EntryLength(wrapper.Length))));
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
    [TestCase("missing compact flag")]
    [TestCase("compact flag 2")]
    [TestCase("string wrapper")]
    [TestCase("entry without hash")]
    [TestCase("short entry hash")]
    [TestCase("too many entries")]
    [TestCase("trailing bytes")]
    public void Malformed_messages_are_rejected_before_allocation(string scenario)
    {
        WrapperAnnouncement[] announcements = [.. Enumerable.Range(1, 2).Select(i => WrapperAnnouncement.Create(new byte[i], i % 2, default)).OrderByDescending(d => d.Hash)];
        byte[] Id(ulong value) => Eth73Rlp.EncodeUInt(value);
        byte[] Hash(Hash256 hash) => Eth73Rlp.EncodeBytes(hash.Bytes);
        byte[] wrapper = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(5));
        byte[] Entry() => Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes(ValueKeccak.Compute(wrapper).Bytes), wrapper);
        Action decode = scenario switch
        {
            "unordered announcement" => () => new NewProofWrapperHashesMessageSerializer().Decode(Eth73Rlp.EncodeList(announcements[0].Encoded, announcements[1].Encoded)),
            "empty announcement" => () => new NewProofWrapperHashesMessageSerializer().Decode(Eth73Rlp.EncodeList()),
            "duplicate request" => () => new GetProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Id(1), Eth73Rlp.EncodeList(Hash(TestItem.KeccakA), Hash(TestItem.KeccakA)))),
            "too many requested" => () => new GetProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Id(1),
                Eth73Rlp.EncodeList([.. Enumerable.Range(0, Eth73Protocol.MaxWrappersPerRequest + 1).Select(i => Eth73Rlp.EncodeBytes(Keccak.Compute(i.ToString()).Bytes))]))),
            "missing compact flag" => () => new GetProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList(Hash(TestItem.KeccakA)))),
            "compact flag 2" => () => new GetProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Id(2), Eth73Rlp.EncodeList(Hash(TestItem.KeccakA)))),
            "string wrapper" => () => new ProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList(
                Eth73Rlp.EncodeList(Hash(TestItem.KeccakA), Eth73Rlp.EncodeBytes([1, 2]))))),
            "entry without hash" => () => new ProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList(wrapper))),
            "short entry hash" => () => new ProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList(
                Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes(new byte[31]), wrapper)))),
            "too many entries" => () => new ProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1),
                Eth73Rlp.EncodeList([.. Enumerable.Range(0, Eth73Protocol.MaxWrappersPerRequest + 1).Select(_ => Entry())]))),
            _ => () => new ProofWrappersMessageSerializer().Decode([.. Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList()), 0x80])
        };
        Assert.That(decode, Throws.InstanceOf<RlpException>());
    }

    // EIP-8288 Test Case 12: empty transaction lists, unresolved references and duplicate or reordered transactions are rejected
    // before validation. A dependency list wider than the mode-0 limits or out of order parses: EIP-8437 leaves dependency-rule
    // failures to validation, which discards them without penalty.
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
    public void Canonical_wrapper_parser_rejects_noncanonical_bodies(string scenario)
    {
        Transaction[] transactions = [.. Enumerable.Range(0, 2).Select(WrapperTestObjects.FrameTransaction).OrderBy(static t => t.Hash!.ToString())];
        byte[] valid = WrapperTestObjects.Wrapper(64, transactions);
        byte[] Rebuilt(IEnumerable<byte[]> items, byte[]? content = null)
        {
            Eth73RlpReader outer = new(valid);
            Eth73RlpReader fields = outer.ReadList();
            fields.ReadItem();
            fields.ReadItem();
            return Eth73Rlp.EncodeList(Eth73Rlp.EncodeList([.. items]), Eth73Rlp.EncodeUInt(1), content ?? fields.ReadItem().ToArray());
        }
        byte[][] encoded = [.. transactions.Select(static t => Eth73Rlp.EncodeBytes(WrapperTestObjects.Envelope(t)))];
        byte[][] wide = [.. Enumerable.Range(0, Eip8288Constants.MaxProofDependencies + 1)
            .Select(static i => Eth73Rlp.EncodeBytes(Eip8288Dependencies.Serialize([new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute([(byte)i, (byte)(i >> 8)]), default)])))
            .OrderBy(static d => d.ToHexString())];
        byte[] wrapper = scenario switch
        {
            "valid" => valid,
            "trailing" => [.. valid, 0x80],
            "empty" => Rebuilt([]),
            "reference" => Rebuilt([Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes(transactions[0].Hash!.Bytes)), encoded[1]]),
            "duplicate" => Rebuilt([encoded[0], encoded[0]]),
            "reordered" => Rebuilt([encoded[1], encoded[0]]),
            "not a frame transaction" => Rebuilt([Eth73Rlp.EncodeBytes([2, 0xc0])]),
            "aggregate proof" => Rebuilt(encoded, Eth73Rlp.EncodeList(Eth73Rlp.EncodeList([.. Eip8288Dependencies.Canonicalize(
                    transactions.SelectMany(static t => Eip8288Dependencies.ForTransaction(t, FrameTxSigHash.ComputeValue)))
                .Select(static d => Eth73Rlp.EncodeBytes(Eip8288Dependencies.Serialize([d])))]), Eth73Rlp.EncodeBytes(new byte[Eip8288Constants.MaxAggregateProofBytes + 1]))),
            "wide dependency list" => Rebuilt(encoded, Eth73Rlp.EncodeList(Eth73Rlp.EncodeList([.. wide]), Eth73Rlp.EncodeBytes([1]))),
            _ => Rebuilt(encoded, Eth73Rlp.EncodeList(Eth73Rlp.EncodeList([.. wide.AsEnumerable().Reverse()]), Eth73Rlp.EncodeBytes([1])))
        };
        if (scenario is "valid" or "wide dependency list" or "unsorted dependencies")
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
        byte[] Body() => Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeUInt((ulong)nonce), Eth73Rlp.EncodeBytes(new byte[20]),
            Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes(new byte[32])), Eth73Rlp.EncodeList(),
            Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeUInt(1)),
            Eth73Rlp.EncodeList([.. versioned.Select(static v => Eth73Rlp.EncodeBytes(v))]));
        byte[] List(IEnumerable<byte[]> items) => Eth73Rlp.EncodeList([.. items.Select(static i => Eth73Rlp.EncodeBytes(i))]);
        byte[] payload = scenario switch
        {
            "missing network wrapper" => Body(),
            "blob data" => Eth73Rlp.EncodeList(Body(), Eth73Rlp.EncodeUInt(1), List([new byte[131072]]), List(commitments), List(proofs)),
            "version" => Eth73Rlp.EncodeList(Body(), Eth73Rlp.EncodeUInt(0), Eth73Rlp.EncodeList(), List(commitments), List(proofs)),
            "commitment count" => Eth73Rlp.EncodeList(Body(), Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeList(), List(commitments[..1]), List(proofs)),
            "commitment encoding" => Eth73Rlp.EncodeList(Body(), Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeList(), List([new byte[47], new byte[47]]), List(proofs)),
            "commitment hash" => Eth73Rlp.EncodeList(Body(), Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeList(), List([commitments[1], commitments[0]]), List(proofs)),
            "missing proof" => Eth73Rlp.EncodeList(Body(), Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeList(), List(commitments), List(proofs[..^1])),
            "extra proof" => Eth73Rlp.EncodeList(Body(), Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeList(), List(commitments), List([.. proofs, new byte[48]])),
            "proof encoding" => Eth73Rlp.EncodeList(Body(), Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeList(), List(commitments), List(proofs.Select(static _ => new byte[47]))),
            _ => Eth73Rlp.EncodeList(Body(), Eth73Rlp.EncodeUInt(1), Eth73Rlp.EncodeList(), List(commitments), List(proofs))
        };
        return [(byte)TxType.FrameTx, .. payload];
    }

    /// <summary>The blob_mode1 vector: the mode-1 vector's transactions and content with <paramref name="blob"/> added.</summary>
    private static byte[] BlobWrapper(byte[] blob)
    {
        byte[] mode1 = Bytes.FromHexString(Mode1Body);
        Eth73RlpReader outer = new(mode1);
        Eth73RlpReader fields = outer.ReadList();
        Eth73RlpReader transactions = fields.ReadList();
        List<byte[]> envelopes = [blob];
        while (transactions.HasMore) envelopes.Add(transactions.ReadBytes().ToArray());
        fields.ReadItem();
        byte[] content = fields.ReadItem().ToArray();
        return Eth73Rlp.EncodeList(Eth73Rlp.EncodeList([.. envelopes.OrderBy(static e => WrapperBodies.TransactionHash(e)).Select(static e => Eth73Rlp.EncodeBytes(e))]),
            Eth73Rlp.EncodeUInt(1), content);
    }
}
