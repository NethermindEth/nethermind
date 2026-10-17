// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
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
    public sealed record AnnouncementVector(int Size, string Hash, string EntryRlp)
    {
        public override string ToString() => $"{Size} bytes";
    }

    // ethereum/EIPs#12423 at 12aee2c9, assets/eip-8437/announcement-vectors.json: wrapper[i] = i % 251.
    private static IEnumerable<AnnouncementVector> AnnouncementVectors() =>
    [
        new(1, "bc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a",
            "e201a0bc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a"),
        new(65536, "5c8672dff277ec0f0321f710ce2e8756362daec795f5ff9bf7cb8c4e2e6fb2ec",
            "e583010000a05c8672dff277ec0f0321f710ce2e8756362daec795f5ff9bf7cb8c4e2e6fb2ec"),
        new(131073, "35ae5a437a69fef01f453512998f4e90f1ba613a1d10962f80ebefcc836dbd09",
            "e583020001a035ae5a437a69fef01f453512998f4e90f1ba613a1d10962f80ebefcc836dbd09"),
        new(2097152, "8b993b9a88858899c909e2e2873f952ec0d86539c64834bb768670c88e95c715",
            "e583200000a08b993b9a88858899c909e2e2873f952ec0d86539c64834bb768670c88e95c715"),
    ];

    private static byte[] Pattern(int size) => [.. Enumerable.Range(0, size).Select(i => (byte)(i % 251))];

    [TestCaseSource(nameof(AnnouncementVectors))]
    public void Announcement_matches_eip8437_announcement_vectors(AnnouncementVector vector)
    {
        WrapperAnnouncement announcement = WrapperAnnouncement.Create(Pattern(vector.Size));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcement.Hash.ToString(false), Is.EqualTo(vector.Hash));
            Assert.That(announcement.Length, Is.EqualTo(vector.Size));
            Assert.That(announcement.Encoded.ToHexString(), Is.EqualTo(vector.EntryRlp));
            Assert.That(announcement.Encoded.Length, Is.LessThanOrEqualTo(WrapperAnnouncement.MaxEncodedBytes));
            Assert.That(WrapperAnnouncement.Decode(announcement.Encoded).Hash, Is.EqualTo(announcement.Hash));
        }
    }

    // assets/eip-8437/wrapper-vectors.json: hash-only mode-0 and mode-1 wrappers with 96-byte dependency encodings.
    [TestCase("f90122f842a0bbc4e20edfbc5a1b8431412172663b69b8292373920851b291ae94c3d04709c9a0e5ba95c82acef87b2d7fed961c6ef271d97882fad161a5d825539c756ba69dcf80f8dbf8c4b860000000000000000000000000000000000000000000000000000000000000001006eaaa302366a2b8e60080fe649cb019814b843b9bd681df41d60bde4badd3790000000000000000000000000000000000000000000000000000000000000001b8600000000000000000000000000000000000000000000000000000000000000010f3bc792f5a550e9e8271695b0d0c1ee9b8d42b9071976b6447658021b3d8099f0000000000000000000000000000000000000000000000000000000000000002d4897769746e6573732030897769746e6573732031",
        "f3b5c47a3e9d249451930191612f9572f941e98bc6abc76d62b3e10e4d64ebac", "e4820125a0f3b5c47a3e9d249451930191612f9572f941e98bc6abc76d62b3e10e4d64ebac")]
    [TestCase("f9011df842a0bbc4e20edfbc5a1b8431412172663b69b8292373920851b291ae94c3d04709c9a0e5ba95c82acef87b2d7fed961c6ef271d97882fad161a5d825539c756ba69dcf01f8d6f8c4b860000000000000000000000000000000000000000000000000000000000000001006eaaa302366a2b8e60080fe649cb019814b843b9bd681df41d60bde4badd3790000000000000000000000000000000000000000000000000000000000000001b8600000000000000000000000000000000000000000000000000000000000000010f3bc792f5a550e9e8271695b0d0c1ee9b8d42b9071976b6447658021b3d8099f00000000000000000000000000000000000000000000000000000000000000028f6167677265676174652070726f6f66",
        "5381ac954eb51bc87be2de451c6b1c0f90b506fb70a52fb6df3ad585ef7e7749", "e4820120a05381ac954eb51bc87be2de451c6b1c0f90b506fb70a52fb6df3ad585ef7e7749")]
    public void Wrapper_vectors_are_canonical_and_match_their_announcement(string body, string hash, string entryRlp)
    {
        byte[] wrapper = Bytes.FromHexString(body);
        WrapperAnnouncement announcement = WrapperAnnouncement.Create(wrapper);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcement.Hash.ToString(false), Is.EqualTo(hash));
            Assert.That(announcement.Encoded.ToHexString(), Is.EqualTo(entryRlp));
            Assert.That(WrapperBodies.ParseWrapper(wrapper).Select(h => h.ToString(false)), Is.EqualTo(new[]
            {
                "bbc4e20edfbc5a1b8431412172663b69b8292373920851b291ae94c3d04709c9",
                "e5ba95c82acef87b2d7fed961c6ef271d97882fad161a5d825539c756ba69dcf"
            }));
        }
    }

    [TestCase(0)]
    [TestCase(Eth73Protocol.MaxWrapperBytes + 1)]
    public void Wrapper_length_outside_one_to_max_wrapper_bytes_is_rejected(int length)
    {
        Assert.That(() => WrapperAnnouncement.Create(new byte[length]), Throws.ArgumentException);
        byte[] encoded = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)length), Eth73Rlp.EncodeBytes(new byte[32]));
        Assert.That(() => WrapperAnnouncement.Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Message_ids_follow_eth72_and_have_size_ceilings()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new[] { Eth73MessageCode.NewProofWrapperHashes, Eth73MessageCode.GetProofWrappers, Eth73MessageCode.ProofWrappers },
                Is.EqualTo(new[] { 0x16, 0x17, 0x18 }));
            Assert.That(Eth73Protocol.MessageIdSpaceSize, Is.EqualTo(0x19));
            Assert.That(Eth73MessageCode.IsWrapperMessage(0x19), Is.False, "envelopes travel in GetPooledTransactions");
            Assert.That(Eth73Protocol.MaxWrapperBytes, Is.LessThanOrEqualTo(Eth73Protocol.SoftResponseLimit));
            // Responses have no normative ceiling; the local cap admits one maximum wrapper with its framing.
            Assert.That(ProofWrapperTransport.ResponseLength(long.MaxValue, Eth73Protocol.MaxWrapperBytes), Is.LessThanOrEqualTo(Eth73Protocol.LocalMaxResponseBytes));
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.ProofWrappers, Eth73Protocol.LocalMaxResponseBytes), Is.False);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.ProofWrappers, Eth73Protocol.LocalMaxResponseBytes + 1), Is.True);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.GetProofWrappers, Eth73Protocol.LocalMaxRequestBytes + 1), Is.True);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.NewProofWrapperHashes, Eth73Protocol.LocalMaxRequestBytes), Is.False);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(0x0a, int.MaxValue), Is.False, "PooledTransactions keeps its eth limits");
            Assert.That(Eth73Protocol.ExceedsMessageLimit(0x15, int.MaxValue), Is.False, "eth/72 messages keep their own limits");
        }
    }

    // assets/eip-8437/check_vectors.py: GetProofWrappers and NewProofWrapperHashes at their 64-entry limits.
    [Test]
    public void Messages_at_their_64_entry_limits_match_eip8437_lengths()
    {
        WrapperAnnouncement[] announced = [.. Enumerable.Range(1, Eth73Protocol.MaxAnnouncements).Select(i => WrapperAnnouncement.Decode(
            Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(Eth73Protocol.MaxWrapperBytes), Eth73Rlp.EncodeBytes(Word(i)))))];
        ValueHash256[] requested = [.. Enumerable.Range(1, Eth73Protocol.MaxWrappersPerRequest).Select(i => new ValueHash256(Word(i)))];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new NewProofWrapperHashesMessageSerializer().Encode(new(announced)), Has.Length.EqualTo(2435));
            // EIP-8437 check_vectors.py also asserts request ID 2**63 - 1, the largest this client issues, at 2,127 bytes.
            Assert.That(new GetProofWrappersMessageSerializer().Encode(new(long.MaxValue, requested)), Has.Length.EqualTo(2127));
            Assert.That(new GetProofWrappersMessageSerializer().Encode(new(7, [default])).ToHexString(), Is.EqualTo("e307e1a0" + new string('0', 64)));
        }

        static byte[] Word(int value)
        {
            byte[] word = new byte[32];
            word[^1] = (byte)value;
            return word;
        }
    }

    [Test]
    public void Messages_round_trip_canonically()
    {
        WrapperAnnouncement[] announcements = [.. Enumerable.Range(1, 3).Select(i => WrapperAnnouncement.Create(new byte[i])).OrderBy(d => d.Hash)];
        byte[] wrapper = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(5));
        using (Assert.EnterMultipleScope())
        {
            NewProofWrapperHashesMessageSerializer announce = new();
            Assert.That(announce.Decode(announce.Encode(new(announcements))).Announcements.Select(d => d.Hash), Is.EqualTo(announcements.Select(d => d.Hash)));
            GetProofWrappersMessageSerializer get = new();
            GetProofWrappersMessage request = get.Decode(get.Encode(new(42, [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256])));
            Assert.That((request.RequestId, request.Hashes.Length), Is.EqualTo((42L, 2)));
            ProofWrappersMessageSerializer wrappers = new();
            byte[] encoded = wrappers.Encode(new(42, [wrapper, wrapper]));
            Assert.That(encoded.Length, Is.EqualTo(ProofWrapperTransport.ResponseLength(42, 2 * wrapper.Length)));
            Assert.That(wrappers.Decode(encoded).Wrappers, Is.EqualTo(new[] { wrapper, wrapper }), "wrappers are embedded RLP items");
        }
    }

    // assets/eip-8437/check_vectors.py: the existing PooledTransactions (0x0a) encoding for typed transaction envelopes.
    [Test]
    public void Pooled_items_are_read_whole()
    {
        byte[] legacy = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(1));
        (long requestId, byte[][] items) = PooledEnvelopes.Decode(Bytes.FromHexString("c507c38202c0"));
        (_, byte[][] mixed) = PooledEnvelopes.Decode(Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(7), Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes([2, 0xc0]), legacy)));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(requestId, Is.EqualTo(7));
            Assert.That(items, Is.EqualTo(new byte[][] { [0x82, 0x02, 0xc0] }), "a typed transaction keeps its string wrapping");
            Assert.That(mixed[1], Is.EqualTo(legacy), "a legacy transaction is its whole list item");
        }
    }

    [TestCase("unordered announcement")]
    [TestCase("empty announcement")]
    [TestCase("duplicate request")]
    [TestCase("too many requested")]
    [TestCase("string wrapper")]
    [TestCase("truncated item")]
    [TestCase("too many envelopes")]
    [TestCase("trailing bytes")]
    public void Malformed_messages_are_rejected_before_allocation(string scenario)
    {
        WrapperAnnouncement[] announcements = [.. Enumerable.Range(1, 2).Select(i => WrapperAnnouncement.Create(new byte[i])).OrderByDescending(d => d.Hash)];
        byte[] Id(ulong value) => Eth73Rlp.EncodeUInt(value);
        byte[] Hash(Hash256 hash) => Eth73Rlp.EncodeBytes(hash.Bytes);
        Action decode = scenario switch
        {
            "unordered announcement" => () => new NewProofWrapperHashesMessageSerializer().Decode(Eth73Rlp.EncodeList(announcements[0].Encoded, announcements[1].Encoded)),
            "empty announcement" => () => new NewProofWrapperHashesMessageSerializer().Decode(Eth73Rlp.EncodeList()),
            "duplicate request" => () => new GetProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList(Hash(TestItem.KeccakA), Hash(TestItem.KeccakA)))),
            "too many requested" => () => new GetProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1),
                Eth73Rlp.EncodeList([.. Enumerable.Range(0, Eth73Protocol.MaxWrappersPerRequest + 1).Select(i => Eth73Rlp.EncodeBytes(Keccak.Compute(i.ToString()).Bytes))]))),
            "string wrapper" => () => new ProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes([1, 2])))),
            "truncated item" => () => PooledEnvelopes.Decode([.. Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes([2, 0xc0])))[..^1]]),
            "too many envelopes" => () => PooledEnvelopes.Decode(Eth73Rlp.EncodeList(Id(1),
                Eth73Rlp.EncodeList([.. Enumerable.Range(0, Eth73Protocol.MaxEnvelopesPerRequest + 1).Select(_ => Eth73Rlp.EncodeBytes([2, 0xc0]))]))),
            _ => () => new ProofWrappersMessageSerializer().Decode([.. Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList()), 0x80])
        };
        Assert.That(decode, Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Canonical_wrapper_parser_rejects_noncanonical_bodies()
    {
        Transaction transaction = WrapperTestObjects.FrameTransaction(1);
        byte[] valid = WrapperTestObjects.Wrapper(64, transaction);
        Assert.That(WrapperBodies.ParseWrapper(valid), Is.EqualTo(new[] { transaction.Hash!.ValueHash256 }));
        Assert.That(() => WrapperBodies.ParseWrapper(WrapperTestObjects.Wrapper(Eip8288Constants.MaxAggregateProofBytes + 1, transaction)),
            Throws.InstanceOf<RlpException>(), "MAX_AGGREGATE_PROOF_BYTES applies before decoding");
        Assert.That(() => WrapperBodies.ParseWrapper([.. valid, 0x80]), Throws.InstanceOf<RlpException>());
    }
}
