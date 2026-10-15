// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using DotNetty.Buffers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

public class LeanTransportTests
{
    public record Vector(int Size, int Chunks, string ContentHash, string DescriptorRlp, string ObjectId, int LastBytes);

    // assets/eip-8437/commitment-vectors.json: a zero profile ID and body[i] = i % 251.
    private static readonly Vector[] Vectors =
    [
        new(1, 1, "bc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a",
            "f843a0000000000000000000000000000000000000000000000000000000000000000001a0bc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a",
            "01889b9f0aeb669571a9968ddf16370e4d77d61f1c681892a7f14239cac75af1", 1),
        new(65536, 1, "5c8672dff277ec0f0321f710ce2e8756362daec795f5ff9bf7cb8c4e2e6fb2ec",
            "f846a0000000000000000000000000000000000000000000000000000000000000000083010000a05c8672dff277ec0f0321f710ce2e8756362daec795f5ff9bf7cb8c4e2e6fb2ec",
            "3ce1578469b5e4fe5da2ad9be1d3774f91661e812e3799d06dd8371cf84cb88a", 65536),
        new(131073, 3, "35ae5a437a69fef01f453512998f4e90f1ba613a1d10962f80ebefcc836dbd09",
            "f846a0000000000000000000000000000000000000000000000000000000000000000083020001a035ae5a437a69fef01f453512998f4e90f1ba613a1d10962f80ebefcc836dbd09",
            "bf6c995460afd9c7e1d84bd5890be93917e7125d6cafd97ca349d6345676e7f4", 1),
        new(9437184, 144, "590d37433aed0176311678dc79eeb5ed8ff77bfaa51e7dc27fac0ace14efef87",
            "f846a0000000000000000000000000000000000000000000000000000000000000000083900000a0590d37433aed0176311678dc79eeb5ed8ff77bfaa51e7dc27fac0ace14efef87",
            "34d7d295e59a5a34d8b6ed0320ea70f679a0aadc8f5600215059fe52e697c47d", 65536)
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
        LeanDescriptor descriptor = LeanDescriptor.Create(default, VectorBody(vector.Size));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(descriptor.ContentHash.ToString(false), Is.EqualTo(vector.ContentHash));
            Assert.That(Convert.ToHexStringLower(descriptor.Encoded), Is.EqualTo(vector.DescriptorRlp));
            Assert.That(descriptor.ObjectId.ToString(false), Is.EqualTo(vector.ObjectId));
            Assert.That(descriptor.ChunkCount, Is.EqualTo(vector.Chunks));
            Assert.That(descriptor.ChunkLength(vector.Chunks - 1), Is.EqualTo(vector.LastBytes));
            Assert.That(LeanDescriptor.Decode(Convert.FromHexString(vector.DescriptorRlp)).ObjectId, Is.EqualTo(descriptor.ObjectId));
            Assert.That(() => descriptor.ChunkLength(vector.Chunks), Throws.TypeOf<ArgumentOutOfRangeException>());
        }
    }

    [TestCase(1)]
    [TestCase(65535)]
    [TestCase(65536)]
    [TestCase(65537)]
    [TestCase(131072)]
    [TestCase(131073)]
    [TestCase((int)LeanProtocol.MaxObjectBytes)]
    public void Chunk_boundaries_reassemble_to_the_content_hash_and_every_field_changes_identity(int size)
    {
        byte[] body = VectorBody(size);
        LeanDescriptor descriptor = LeanDescriptor.Create(default, body);
        byte[] reassembled = new byte[size];
        for (int i = 0; i < descriptor.ChunkCount; i++)
            body.AsSpan(i * LeanProtocol.ChunkBytes, descriptor.ChunkLength(i)).CopyTo(reassembled.AsSpan(i * LeanProtocol.ChunkBytes));
        byte[] corrupted = (byte[])body.Clone();
        corrupted[0] ^= 1;
        ValueHash256 profile = ValueKeccak.Compute("profile"u8);
        byte[][] mutations =
        [
            Descriptor(profile, (ulong)size, descriptor.ContentHash),
            Descriptor(default, (ulong)size + 1, descriptor.ContentHash),
            Descriptor(default, (ulong)size, default)
        ];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(descriptor.ChunkCount, Is.InRange(1, LeanProtocol.MaxChunkCount));
            Assert.That(ValueKeccak.Compute(reassembled), Is.EqualTo(descriptor.ContentHash));
            Assert.That(ValueKeccak.Compute(corrupted), Is.Not.EqualTo(descriptor.ContentHash));
            foreach (byte[] mutation in mutations) Assert.That(LeanCommitment.ObjectId(mutation), Is.Not.EqualTo(descriptor.ObjectId));
        }
    }

    [Test]
    public void Object_length_outside_one_to_max_object_bytes_is_rejected()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => LeanDescriptor.Create(default, []), Throws.TypeOf<RlpException>());
            Assert.That(() => LeanDescriptor.Create(default, new byte[LeanProtocol.MaxObjectBytes + 1]), Throws.TypeOf<RlpException>());
        }
    }

    [Test]
    public void Profile_matches_domain_separated_canonical_key()
        => Assert.That(LeanCommitment.ProfileId(Eip8288Constants.AggregatedVk),
            Is.EqualTo(ValueKeccak.Compute([.. "lean/1/profile"u8, 0, .. Eip8288Constants.AggregatedVk])));

    private static byte[] Descriptor(in ValueHash256 profile, ulong length, in ValueHash256 contentHash) =>
        LeanRlp.EncodeList(LeanRlp.EncodeBytes(profile.Bytes), LeanRlp.EncodeUInt(length), LeanRlp.EncodeBytes(contentHash.Bytes));

    private static IEnumerable<TestCaseData> RoundTrips()
    {
        LeanDescriptor descriptor = LeanDescriptor.Create(default, VectorBody(65537));
        yield return new TestCaseData(new LeanStatusMessage(1, default, [default], LeanProtocol.MaxObjectBytes), new LeanStatusMessageSerializer());
        yield return new TestCaseData(new AnnounceObjectsMessage([descriptor]), new AnnounceObjectsMessageSerializer());
        yield return new TestCaseData(new GetChunksMessage(7, descriptor.ObjectId, [0, 1]), new GetChunksMessageSerializer());
        yield return new TestCaseData(new ChunkMessage(7, descriptor.ObjectId, 1, new byte[] { 1 }), new ChunkMessageSerializer());
        yield return new TestCaseData(new CompleteMessage(7, descriptor.ObjectId, LeanCompleteStatus.Served), new CompleteMessageSerializer());
        yield return new TestCaseData(new CancelMessage(7), new CancelMessageSerializer());
    }

    [TestCaseSource(nameof(RoundTrips))]
    public void Six_messages_round_trip_without_legacy_fields(LeanMessage message, object serializer)
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
    public void Published_request_and_chunk_encodings_match_eip8437()
    {
        string zeros = new('0', 64);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Convert.ToHexStringLower(new CancelMessageSerializer().Encode(new(7))), Is.EqualTo("c107"));
            Assert.That(Convert.ToHexStringLower(new GetChunksMessageSerializer().Encode(new(7, default, [0, 2]))),
                Is.EqualTo("e507a0" + zeros + "c28002"));
            Assert.That(Convert.ToHexStringLower(new ChunkMessageSerializer().Encode(new(7, default, 0, "a"u8.ToArray()))),
                Is.EqualTo("e407a0" + zeros + "8061"));
        }
    }

    [Test]
    public void Six_messages_are_numbered_consecutively()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanProtocol.MessageCount, Is.EqualTo(6));
            Assert.That(new int[] { LeanMessageCode.Status, LeanMessageCode.AnnounceObjects, LeanMessageCode.GetChunks,
                LeanMessageCode.Chunk, LeanMessageCode.Complete, LeanMessageCode.Cancel }, Is.EqualTo(new[] { 0, 1, 2, 3, 4, 5 }));
        }
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

    [TestCase("old kind")]
    [TestCase("old context")]
    [TestCase("extra field")]
    [TestCase("zero length")]
    [TestCase("oversize")]
    [TestCase("short hash")]
    public void Invalid_descriptor_is_rejected(string invalid)
    {
        byte[] hash = LeanRlp.EncodeBytes(new byte[32]);
        byte[] length = LeanRlp.EncodeUInt(invalid == "zero length" ? 0 : invalid == "oversize" ? LeanProtocol.MaxObjectBytes + 1 : 1);
        byte[] encoded = invalid switch
        {
            "old kind" => LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), hash, LeanRlp.EncodeList(), length, hash),
            "old context" => LeanRlp.EncodeList(hash, LeanRlp.EncodeList(), length, hash),
            "extra field" => LeanRlp.EncodeList(hash, length, hash, hash),
            "short hash" => LeanRlp.EncodeList(LeanRlp.EncodeBytes(new byte[31]), length, hash),
            _ => LeanRlp.EncodeList(hash, length, hash)
        };
        Assert.That(() => LeanDescriptor.Decode(encoded), Throws.TypeOf<RlpException>());
    }

    [TestCase("old kinds")]
    [TestCase("version")]
    [TestCase("profile duplicates")]
    [TestCase("zero ceiling")]
    [TestCase("oversize ceiling")]
    public void Malformed_status_is_rejected(string invalid)
    {
        byte[] ceiling = LeanRlp.EncodeUInt(invalid switch
        {
            "zero ceiling" => 0,
            "oversize ceiling" => LeanProtocol.MaxObjectBytes + 1,
            _ => LeanProtocol.MaxObjectBytes
        });
        byte[] profiles = invalid == "profile duplicates"
            ? LeanRlp.EncodeList(LeanRlp.EncodeBytes(new byte[32]), LeanRlp.EncodeBytes(new byte[32]))
            : LeanRlp.EncodeList(LeanRlp.EncodeBytes(new byte[32]));
        byte[][] fields = invalid == "old kinds"
            ? [LeanRlp.EncodeUInt(1), LeanRlp.EncodeUInt(1), LeanRlp.EncodeBytes(new byte[32]), profiles, LeanRlp.EncodeUInt(1), ceiling]
            : [LeanRlp.EncodeUInt(invalid == "version" ? 2UL : 1), LeanRlp.EncodeUInt(1), LeanRlp.EncodeBytes(new byte[32]), profiles, ceiling];
        Assert.That(() => new LeanStatusMessageSerializer().Decode(LeanRlp.EncodeList(fields)), Throws.TypeOf<RlpException>());
    }

    [Test]
    public void Maximum_chunk_and_announcement_fit_message_bounds()
    {
        byte[] body = new byte[LeanProtocol.ChunkBytes];
        byte[] chunk = new ChunkMessageSerializer().Encode(new(ulong.MaxValue, default, LeanProtocol.MaxChunkCount - 1, body));
        byte[] largest = LeanRlp.EncodeList(LeanRlp.EncodeBytes(new byte[32]), LeanRlp.EncodeUInt(LeanProtocol.MaxObjectBytes),
            LeanRlp.EncodeBytes(new byte[32]));
        byte[][] descriptors = new byte[LeanProtocol.MaxAnnouncements][];
        Array.Fill(descriptors, largest);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(chunk.Length, Is.LessThan(LeanProtocol.MaxMessageBytes));
            Assert.That(LeanRlp.EncodeList(descriptors).Length, Is.LessThan(LeanProtocol.MaxMessageBytes));
        }
    }

    [Test]
    public void Maximum_aggregate_wrapper_fits_without_transaction_envelopes()
    {
        byte[][] hashes = new byte[Eip8288Constants.MaxTxsPerWrapper][];
        byte[][] dependencies = new byte[Eip8288Constants.MaxDepsPerAggregate][];
        for (int i = 0; i < hashes.Length; i++)
        {
            byte[] hash = new byte[32];
            BinaryPrimitives.WriteInt32BigEndian(hash.AsSpan(28), i);
            hashes[i] = LeanRlp.EncodeBytes(hash);
        }
        for (int i = 0; i < dependencies.Length; i++)
        {
            byte[] dependency = new byte[Eip8288Constants.DependencyTripleLength];
            dependency[31] = Eip8288Constants.LeanSphincsScheme;
            BinaryPrimitives.WriteInt32BigEndian(dependency.AsSpan(60), i);
            dependencies[i] = LeanRlp.EncodeBytes(dependency);
        }
        byte[] wrapper = LeanRlp.EncodeList(LeanRlp.EncodeList(hashes), LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(LeanRlp.EncodeList(dependencies), LeanRlp.EncodeBytes(new byte[Eip8288Constants.MaxAggregateProofBytes])));
        using (Assert.EnterMultipleScope())
        {
            Assert.That((ulong)wrapper.Length, Is.LessThanOrEqualTo(LeanProtocol.MaxObjectBytes));
            Assert.That(LeanBodies.ParseWrapper(wrapper), Has.Count.EqualTo(Eip8288Constants.MaxTxsPerWrapper));
        }
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
}
