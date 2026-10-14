// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

public class LeanTransportTests
{
    public record Vector(int Size, int Chunks, string ContentHash, string ObjectId, int LastBytes);
    private static readonly Vector[] Vectors =
    [
        new(1, 1, "bc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a", "9bb7ea577d603c98ba07c361fe0a9287172d89c69fbf5492b491f2dcc733ad0b", 1),
        new(65536, 1, "5c8672dff277ec0f0321f710ce2e8756362daec795f5ff9bf7cb8c4e2e6fb2ec", "ee6efbda749ffde5568553a8d88fe4d9d2148aed4e3c4c5aba171e7ba0ccbe5b", 65536),
        new(131073, 3, "35ae5a437a69fef01f453512998f4e90f1ba613a1d10962f80ebefcc836dbd09", "a6a0ad85350f6567eed1392e595b276214a6e98951df458c55640fcebb968efa", 1),
        new(9437184, 144, "590d37433aed0176311678dc79eeb5ed8ff77bfaa51e7dc27fac0ace14efef87", "1e0bf331d0b5204e7abaa0fdc38ff569a7f0f3ad3cac824570bef078beb4c7b6", 65536)
    ];

    internal static byte[] VectorBody(int size)
    {
        byte[] body = new byte[size];
        for (int i = 0; i < size; i++) body[i] = (byte)(i % 251);
        return body;
    }

    [TestCaseSource(nameof(Vectors))]
    public void Descriptor_matches_current_eip8437_vectors(Vector vector)
    {
        LeanDescriptor descriptor = LeanDescriptor.Create(1, default, LeanDescriptor.WrapperContext(), VectorBody(vector.Size));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(descriptor.ContentHash.ToString(false), Is.EqualTo(vector.ContentHash));
            Assert.That(descriptor.ObjectId.ToString(false), Is.EqualTo(vector.ObjectId));
            Assert.That(descriptor.ChunkCount, Is.EqualTo(vector.Chunks));
            Assert.That(descriptor.ChunkLength(vector.Chunks - 1), Is.EqualTo(vector.LastBytes));
            Assert.That(LeanDescriptor.Decode(descriptor.Encoded).ObjectId, Is.EqualTo(descriptor.ObjectId));
            Assert.That(() => descriptor.ChunkLength(vector.Chunks), Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }

    [Test]
    public void Every_descriptor_field_changes_the_object_identity()
    {
        byte[] body = [1];
        LeanDescriptor original = LeanDescriptor.Create(1, default, LeanDescriptor.WrapperContext(), body);
        LeanDescriptor[] changed =
        [
            LeanDescriptor.Create(1, TestItem.KeccakA.ValueHash256, LeanDescriptor.WrapperContext(), body),
            LeanDescriptor.Create(2, default, LeanDescriptor.BlockProofContext(TestItem.KeccakA.ValueHash256), body),
            LeanDescriptor.Create(1, default, LeanDescriptor.WrapperContext(), [1, 2]),
            LeanDescriptor.Create(1, default, LeanDescriptor.WrapperContext(), [2])
        ];
        foreach (LeanDescriptor descriptor in changed) Assert.That(descriptor.ObjectId, Is.Not.EqualTo(original.ObjectId));
    }

    [Test]
    public void Profile_matches_domain_separated_canonical_key()
        => Assert.That(LeanCommitment.ProfileId(Eip8288Constants.AggregatedVk),
            Is.EqualTo(ValueKeccak.Compute([.. "lean/1/profile"u8, 0, .. Eip8288Constants.AggregatedVk])));

    private static IEnumerable<TestCaseData> RoundTrips()
    {
        LeanDescriptor descriptor = LeanDescriptor.Create(1, default, LeanDescriptor.WrapperContext(), VectorBody(65537));
        yield return new TestCaseData(new LeanStatusMessage(1, default, [default], 3, LeanProtocol.MaxObjectBytes), new LeanStatusMessageSerializer());
        yield return new TestCaseData(new AnnounceObjectsMessage([descriptor]), new AnnounceObjectsMessageSerializer());
        yield return new TestCaseData(new GetObjectsMessage(7, [new(1, default, descriptor.ObjectId)]), new GetObjectsMessageSerializer());
        yield return new TestCaseData(new ObjectsMessage(7, [new(LeanResultStatus.Ok, descriptor), LeanObjectResult.Of(LeanResultStatus.Busy)]), new ObjectsMessageSerializer());
        yield return new TestCaseData(new GetChunksMessage(7, descriptor.ObjectId, [0, 1]), new GetChunksMessageSerializer());
        yield return new TestCaseData(new ChunkMessage(7, descriptor.ObjectId, 1, new byte[] { 1 }), new ChunkMessageSerializer());
        yield return new TestCaseData(new CompleteMessage(7, descriptor.ObjectId, LeanCompleteStatus.Served), new CompleteMessageSerializer());
        yield return new TestCaseData(new CancelMessage(7), new CancelMessageSerializer());
    }

    [TestCaseSource(nameof(RoundTrips))]
    public void Eight_messages_round_trip_without_legacy_fields(LeanMessage message, object serializer)
    {
        dynamic codec = serializer;
        byte[] encoded = codec.Encode((dynamic)message);
        IByteBuffer buffer = Unpooled.WrappedBuffer(encoded);
        try
        {
            LeanMessage decoded = codec.Deserialize(buffer);
            byte[] roundTrip = codec.Encode((dynamic)decoded);
            Assert.That(roundTrip, Is.EqualTo(encoded));
            Assert.That(buffer.ReadableBytes, Is.Zero);
        }
        finally { buffer.Release(); }
    }

    [Test]
    public void Chunk_has_no_branch_and_obsolete_codec_is_rejected()
    {
        ValueHash256 objectId = TestItem.KeccakA.ValueHash256;
        byte[] encoded = new ChunkMessageSerializer().Encode(new(7, objectId, 0, new byte[] { 1 }));
        LeanChunkView view = LeanChunkView.Parse(encoded);
        Assert.That(view.Data.ToArray(), Is.EqualTo(new byte[] { 1 }));
        byte[] old = LeanRlp.EncodeList(LeanRlp.EncodeUInt(7), LeanRlp.EncodeBytes(objectId.Bytes), LeanRlp.EncodeUInt(0),
            LeanRlp.EncodeBytes([1]), LeanRlp.EncodeList());
        Assert.That(() => LeanChunkView.Parse(old), Throws.TypeOf<RlpException>());
    }

    [TestCase("kind")]
    [TestCase("extra field")]
    [TestCase("context")]
    [TestCase("zero length")]
    [TestCase("oversize")]
    public void Invalid_descriptor_is_rejected(string invalid)
    {
        byte[] encoded = LeanRlp.EncodeList(LeanRlp.EncodeUInt(invalid == "kind" ? 3UL : 1), LeanRlp.EncodeBytes(new byte[32]),
            invalid == "context" ? LeanDescriptor.BlockProofContext(default) : LeanDescriptor.WrapperContext(),
            LeanRlp.EncodeUInt(invalid == "zero length" ? 0 : invalid == "oversize" ? LeanProtocol.MaxObjectBytes + 1 : 1),
            LeanRlp.EncodeBytes(new byte[32]));
        if (invalid == "extra field")
            encoded = LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), LeanRlp.EncodeBytes(new byte[32]), LeanDescriptor.WrapperContext(),
                LeanRlp.EncodeUInt(1), LeanRlp.EncodeBytes(new byte[32]), LeanRlp.EncodeBytes(new byte[32]));
        Assert.That(() => LeanDescriptor.Decode(encoded), Throws.TypeOf<RlpException>());
    }

    [TestCase("old kinds")]
    [TestCase("version")]
    [TestCase("profile duplicates")]
    [TestCase("zero ceiling")]
    public void Malformed_status_is_rejected(string invalid)
    {
        byte[] encoded = LeanRlp.EncodeList(LeanRlp.EncodeUInt(invalid == "version" ? 2UL : 1), LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeBytes(new byte[32]), invalid == "profile duplicates"
                ? LeanRlp.EncodeList(LeanRlp.EncodeBytes(new byte[32]), LeanRlp.EncodeBytes(new byte[32]))
                : LeanRlp.EncodeList(LeanRlp.EncodeBytes(new byte[32])),
            LeanRlp.EncodeUInt(invalid == "old kinds" ? 7UL : 3), LeanRlp.EncodeUInt(invalid == "zero ceiling" ? 0 : LeanProtocol.MaxObjectBytes));
        Assert.That(() => new LeanStatusMessageSerializer().Decode(encoded), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void Maximum_chunk_and_announcement_fit_message_bounds()
    {
        byte[] body = new byte[LeanProtocol.ChunkBytes];
        LeanDescriptor descriptor = LeanTestObjects.Describe(body);
        byte[] chunk = new ChunkMessageSerializer().Encode(new(ulong.MaxValue, descriptor.ObjectId, 0, body));
        Assert.That(chunk.Length, Is.LessThan(LeanProtocol.MaxMessageBytes));
        LeanDescriptor[] descriptors = new LeanDescriptor[LeanProtocol.MaxAnnouncements];
        for (int i = 0; i < descriptors.Length; i++) descriptors[i] = LeanTestObjects.Describe([(byte)i]);
        Array.Sort(descriptors, (a, b) => a.ObjectId.CompareTo(b.ObjectId));
        Assert.That(new AnnounceObjectsMessageSerializer().Encode(new(descriptors)).Length, Is.LessThan(LeanProtocol.MaxMessageBytes));
    }

    [Test]
    public void Canonical_hash_only_wrapper_rejects_full_entries_and_empty_dependencies()
    {
        Transaction transaction = LeanTestObjects.FrameTransaction(1);
        byte[] wrapper = LeanTestObjects.Wrapper(1, true, transaction);
        Assert.That(LeanBodies.ParseWrapper(wrapper), Is.EqualTo(new[] { transaction.Hash!.ValueHash256 }));
        byte[] old = LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(0), LeanRlp.EncodeBytes(LeanTestObjects.Envelope(transaction)))),
            LeanRlp.EncodeUInt(1), LeanRlp.EncodeList(LeanRlp.EncodeList(), LeanRlp.EncodeBytes([1])));
        Assert.That(() => LeanBodies.ParseWrapper(old), Throws.TypeOf<RlpException>());
    }

    internal static BlockHeader ProofHeader(byte[] proof, Hash256? depsHash = null, ulong number = 7)
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(number).WithTimestamp(1000).TestObject;
        header.RecursiveStark = new(new Hash256(ValueKeccak.Compute(proof)), depsHash ?? new(Eip8288Dependencies.ComputeDepsHash([])));
        header.Hash = header.CalculateHash();
        return header;
    }
}
