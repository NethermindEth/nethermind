// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using DotNetty.Buffers;
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
    public sealed record DescriptorVector(int Size, string ContentHash, string DescriptorRlp)
    {
        public override string ToString() => $"{Size} bytes";
    }

    // ethereum/EIPs#12423 at c3dde52825, assets/eip-8437/descriptor-vectors.json: wrapper[i] = i % 251.
    private static IEnumerable<DescriptorVector> DescriptorVectors() =>
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

    [TestCaseSource(nameof(DescriptorVectors))]
    public void Descriptor_matches_eip8437_descriptor_vectors(DescriptorVector vector)
    {
        WrapperDescriptor descriptor = WrapperDescriptor.Create(Pattern(vector.Size));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(descriptor.ContentHash.ToString(false), Is.EqualTo(vector.ContentHash));
            Assert.That(descriptor.ObjectId, Is.EqualTo(descriptor.ContentHash), "the object ID is the content hash");
            Assert.That(descriptor.Encoded.ToHexString(), Is.EqualTo(vector.DescriptorRlp));
            Assert.That(descriptor.Encoded.Length, Is.LessThanOrEqualTo(WrapperDescriptor.MaxEncodedBytes));
            Assert.That(WrapperDescriptor.Decode(descriptor.Encoded).ObjectId, Is.EqualTo(descriptor.ObjectId));
        }
    }

    // assets/eip-8437/wrapper-vectors.json: hash-only mode-0 and mode-1 wrappers with 96-byte dependency encodings.
    [TestCase("f90122f842a0bbc4e20edfbc5a1b8431412172663b69b8292373920851b291ae94c3d04709c9a0e5ba95c82acef87b2d7fed961c6ef271d97882fad161a5d825539c756ba69dcf80f8dbf8c4b860000000000000000000000000000000000000000000000000000000000000001006eaaa302366a2b8e60080fe649cb019814b843b9bd681df41d60bde4badd3790000000000000000000000000000000000000000000000000000000000000001b8600000000000000000000000000000000000000000000000000000000000000010f3bc792f5a550e9e8271695b0d0c1ee9b8d42b9071976b6447658021b3d8099f0000000000000000000000000000000000000000000000000000000000000002d4897769746e6573732030897769746e6573732031",
        "f3b5c47a3e9d249451930191612f9572f941e98bc6abc76d62b3e10e4d64ebac", "e4820125a0f3b5c47a3e9d249451930191612f9572f941e98bc6abc76d62b3e10e4d64ebac")]
    [TestCase("f9011df842a0bbc4e20edfbc5a1b8431412172663b69b8292373920851b291ae94c3d04709c9a0e5ba95c82acef87b2d7fed961c6ef271d97882fad161a5d825539c756ba69dcf01f8d6f8c4b860000000000000000000000000000000000000000000000000000000000000001006eaaa302366a2b8e60080fe649cb019814b843b9bd681df41d60bde4badd3790000000000000000000000000000000000000000000000000000000000000001b8600000000000000000000000000000000000000000000000000000000000000010f3bc792f5a550e9e8271695b0d0c1ee9b8d42b9071976b6447658021b3d8099f00000000000000000000000000000000000000000000000000000000000000028f6167677265676174652070726f6f66",
        "5381ac954eb51bc87be2de451c6b1c0f90b506fb70a52fb6df3ad585ef7e7749", "e4820120a05381ac954eb51bc87be2de451c6b1c0f90b506fb70a52fb6df3ad585ef7e7749")]
    public void Wrapper_vectors_are_canonical_and_match_their_descriptor(string body, string contentHash, string descriptorRlp)
    {
        byte[] wrapper = Bytes.FromHexString(body);
        WrapperDescriptor descriptor = WrapperDescriptor.Create(wrapper);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(descriptor.ContentHash.ToString(false), Is.EqualTo(contentHash));
            Assert.That(descriptor.Encoded.ToHexString(), Is.EqualTo(descriptorRlp));
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
        Assert.That(() => WrapperDescriptor.Create(new byte[length]), Throws.ArgumentException);
        byte[] encoded = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt((ulong)length), Eth73Rlp.EncodeBytes(new byte[32]));
        Assert.That(() => WrapperDescriptor.Decode(encoded), Throws.InstanceOf<RlpException>());
    }

    [Test]
    public void Message_ids_follow_eth72_and_have_size_ceilings()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new[] { Eth73MessageCode.AnnounceProofWrappers, Eth73MessageCode.GetProofWrappers, Eth73MessageCode.ProofWrappers,
                Eth73MessageCode.GetWrapperEnvelopes, Eth73MessageCode.WrapperEnvelopes }, Is.EqualTo(new[] { 0x16, 0x17, 0x18, 0x19, 0x1a }));
            Assert.That(Eth73Protocol.MessageIdSpaceSize, Is.EqualTo(0x1b));
            Assert.That(Eth73Protocol.MaxWrapperBytes, Is.LessThanOrEqualTo(Eth73Protocol.SoftResponseLimit));
            // Responses have no normative ceiling; the local cap admits one maximum wrapper with its framing.
            Assert.That(ProofWrapperTransport.ResponseLength(long.MaxValue, Eth73Protocol.MaxWrapperBytes), Is.LessThanOrEqualTo(Eth73Protocol.LocalMaxResponseBytes));
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.ProofWrappers, Eth73Protocol.LocalMaxResponseBytes), Is.False);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.ProofWrappers, Eth73Protocol.LocalMaxResponseBytes + 1), Is.True);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.WrapperEnvelopes, Eth73Protocol.LocalMaxResponseBytes + 1), Is.True);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.GetProofWrappers, Eth73Protocol.MaxRequestBytes + 1), Is.True);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(Eth73MessageCode.AnnounceProofWrappers, Eth73Protocol.MaxRequestBytes), Is.False);
            Assert.That(Eth73Protocol.ExceedsMessageLimit(0x15, int.MaxValue), Is.False, "eth/72 messages keep their own limits");
        }
    }

    [Test]
    public void Messages_round_trip_canonically()
    {
        WrapperDescriptor[] descriptors = [.. Enumerable.Range(1, 3).Select(i => WrapperDescriptor.Create(new byte[i])).OrderBy(d => d.ObjectId)];
        byte[] wrapper = Eth73Rlp.EncodeList(Eth73Rlp.EncodeUInt(5));
        using (Assert.EnterMultipleScope())
        {
            AnnounceProofWrappersMessageSerializer announce = new();
            Assert.That(announce.Decode(announce.Encode(new(descriptors))).Descriptors.Select(d => d.ObjectId), Is.EqualTo(descriptors.Select(d => d.ObjectId)));
            GetProofWrappersMessageSerializer get = new();
            GetProofWrappersMessage request = get.Decode(get.Encode(new(42, [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256])));
            Assert.That((request.RequestId, request.ObjectIds.Length), Is.EqualTo((42L, 2)));
            ProofWrappersMessageSerializer wrappers = new();
            byte[] encoded = wrappers.Encode(new(42, [wrapper, wrapper]));
            Assert.That(encoded.Length, Is.EqualTo(ProofWrapperTransport.ResponseLength(42, 2 * wrapper.Length)));
            Assert.That(wrappers.Decode(encoded).Wrappers, Is.EqualTo(new[] { wrapper, wrapper }), "wrappers are embedded RLP items");
            GetWrapperEnvelopesMessageSerializer getEnvelopes = new();
            GetWrapperEnvelopesMessage envelopesRequest = getEnvelopes.Decode(getEnvelopes.Encode(new(7, TestItem.KeccakC.ValueHash256, [0, 3, 4095])));
            Assert.That(envelopesRequest.Indices, Is.EqualTo(new[] { 0, 3, 4095 }));
            WrapperEnvelopesMessageSerializer envelopes = new();
            Assert.That(envelopes.Decode(envelopes.Encode(new(7, [[1], [2, 3]]))).Envelopes, Is.EqualTo(new byte[][] { [1], [2, 3] }));
        }
    }

    [TestCase("unordered announcement")]
    [TestCase("empty announcement")]
    [TestCase("duplicate request")]
    [TestCase("too many requested")]
    [TestCase("string wrapper")]
    [TestCase("unordered indices")]
    [TestCase("index past MAX_TXS_PER_WRAPPER")]
    [TestCase("too many indices")]
    [TestCase("empty envelope")]
    [TestCase("trailing bytes")]
    public void Malformed_messages_are_rejected_before_allocation(string scenario)
    {
        WrapperDescriptor[] descriptors = [.. Enumerable.Range(1, 2).Select(i => WrapperDescriptor.Create(new byte[i])).OrderByDescending(d => d.ObjectId)];
        byte[] Id(ulong value) => Eth73Rlp.EncodeUInt(value);
        byte[] Hash(Hash256 hash) => Eth73Rlp.EncodeBytes(hash.Bytes);
        Action decode = scenario switch
        {
            "unordered announcement" => () => new AnnounceProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(descriptors[0].Encoded, descriptors[1].Encoded)),
            "empty announcement" => () => new AnnounceProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList()),
            "duplicate request" => () => new GetProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList(Hash(TestItem.KeccakA), Hash(TestItem.KeccakA)))),
            "too many requested" => () => new GetProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1),
                Eth73Rlp.EncodeList([.. Enumerable.Range(0, Eth73Protocol.MaxWrappersPerRequest + 1).Select(i => Eth73Rlp.EncodeBytes(Keccak.Compute(i.ToString()).Bytes))]))),
            "string wrapper" => () => new ProofWrappersMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes([1, 2])))),
            "unordered indices" => () => new GetWrapperEnvelopesMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Hash(TestItem.KeccakA), Eth73Rlp.EncodeList(Id(3), Id(2)))),
            "index past MAX_TXS_PER_WRAPPER" => () => new GetWrapperEnvelopesMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Hash(TestItem.KeccakA),
                Eth73Rlp.EncodeList(Id(Eip8288Constants.MaxTxsPerWrapper)))),
            "too many indices" => () => new GetWrapperEnvelopesMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Hash(TestItem.KeccakA),
                Eth73Rlp.EncodeList([.. Enumerable.Range(0, Eth73Protocol.MaxEnvelopesPerRequest + 1).Select(i => Id((ulong)i))]))),
            "empty envelope" => () => new WrapperEnvelopesMessageSerializer().Decode(Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList(Eth73Rlp.EncodeBytes([])))),
            _ => () => new WrapperEnvelopesMessageSerializer().Decode([.. Eth73Rlp.EncodeList(Id(1), Eth73Rlp.EncodeList()), 0x80])
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
