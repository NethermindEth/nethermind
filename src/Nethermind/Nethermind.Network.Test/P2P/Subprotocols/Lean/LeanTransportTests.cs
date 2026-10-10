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
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

/// <summary>EIP-8437 encodings, commitments and header skeletons, including the EIP's test vectors.</summary>
public class LeanTransportTests
{
    public record CommitmentVector(int Size, int ChunkCount, string ContentHash, string LastLeaf, string? PaddingLeaf,
        string ChunkRoot, string ObjectId, string[] Branch);

    private const string EmptyContextHash = "c0f2b9dd6c5fa856eedea5db76f1e33f1c263f5ebf391755b7d6cb2216100f3f";

    private static readonly CommitmentVector[] CommitmentVectors =
    [
        new(1, 1, "bc36789e7a1e281436464229828f817d6612f7b477d66591ff96a9e064bcc98a",
            "21738102e26669e761d6e6828f6e7642b560948e39e89efd18a05cf1ac9f9788", null,
            "1deb89d1898605e8b1bbf8af186a1efeaa5d828f3587a30cf015ac684e692484",
            "d39b2e3efc881ed2746601367bd3985a6707d29426ea14ed339ca992e8eb94e6", []),
        new(65537, 2, "4faa2deae4c869a3cddf91ae8646575699f5d568e57df4914b0536d59bf6a4c9",
            "c650f7216ac9af040a4d7186f70cd12d0e7c8183e72a42dbcb9af09a9f7451ec", null,
            "ff33e4b630c69ecd67518e1213f5a648df50fc1b5c7a8592439e2f4c5ca21e7e",
            "ee5cedb6e340e576b1d4b7d6be2002bbac911e1ee26db7a18587b7f8ee616028",
            ["b17ef669920b42856d351293191ee28da75c15de4d483c535d333d86b05adaea"]),
        new(131073, 3, "35ae5a437a69fef01f453512998f4e90f1ba613a1d10962f80ebefcc836dbd09",
            "c733e3dfb0db3dd59d8b5e30b1a8efda92b67ac886c0835c47ccc7d5a169b0c3",
            "95da608e046232746cce65132a6525b04d1104a68ef4eb2cdb6787d2493baf7b",
            "c648bf887bb5c49d3eba6a968f6e21507ea3ca0a61120aa16e7c980baf7434cd",
            "f0d141fc8ee27459e661bd8459f51199967afdcb76b0155459b660954be94fe2",
            ["95da608e046232746cce65132a6525b04d1104a68ef4eb2cdb6787d2493baf7b",
                "0d2a51ca27b23b2e78b66c81dd790fccc2fb1fe65e01a41c867f62798ee4d181"])
    ];

    internal static byte[] VectorBody(int size)
    {
        byte[] body = new byte[size];
        for (int i = 0; i < size; i++) body[i] = (byte)(i % 251);
        return body;
    }

    private static LeanDescriptor VectorDescriptor(byte[] body, out LeanChunkTree tree) =>
        LeanDescriptor.Create(LeanProtocol.KindWrapper, default, LeanDescriptor.WrapperContext(), body, out tree);

    [TestCaseSource(nameof(CommitmentVectors))]
    public void Commitment_matches_the_eip_vectors(CommitmentVector vector)
    {
        byte[] body = VectorBody(vector.Size);
        LeanDescriptor descriptor = VectorDescriptor(body, out LeanChunkTree tree);
        int last = vector.ChunkCount - 1;
        ValueHash256[] branch = tree.GetBranch(last);
        byte[] lastChunk = body.AsSpan(last * LeanProtocol.ChunkBytes).ToArray();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(descriptor.ChunkCount, Is.EqualTo(vector.ChunkCount));
            Assert.That(descriptor.ContentHash.ToString(false), Is.EqualTo(vector.ContentHash));
            Assert.That(descriptor.ContextHash.ToString(false), Is.EqualTo(EmptyContextHash));
            Assert.That(LeanCommitment.Leaf(descriptor.Seed, last, lastChunk).ToString(false), Is.EqualTo(vector.LastLeaf));
            if (vector.PaddingLeaf is not null)
                Assert.That(LeanCommitment.EmptyLeaf(descriptor.Seed, vector.ChunkCount).ToString(false), Is.EqualTo(vector.PaddingLeaf));
            Assert.That(descriptor.ChunkRoot.ToString(false), Is.EqualTo(vector.ChunkRoot));
            Assert.That(descriptor.ObjectId.ToString(false), Is.EqualTo(vector.ObjectId));
            Assert.That(Array.ConvertAll(branch, static h => h.ToString(false)), Is.EqualTo(vector.Branch));
            Assert.That(new LeanBranchVerifier(descriptor).Verify(last, lastChunk, branch), Is.True);
        }
    }

    [Test]
    public void Message_data_matches_the_eip_vectors()
    {
        LeanDescriptor threeChunks = VectorDescriptor(VectorBody(131073), out _);
        byte[] kindOneBody = LeanRlp.EncodeList(
            LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(0), LeanRlp.EncodeBytes([0x7f, 0x00]))),
            LeanRlp.EncodeUInt(0),
            LeanRlp.EncodeList(LeanRlp.EncodeList(), LeanRlp.EncodeList()));
        List<LeanBodies.WrapperEntry> entries = LeanBodies.ParseWrapper(kindOneBody);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new GetChunksMessageSerializer().Encode(new GetChunksMessage(7, threeChunks.ObjectId, [0, 2])).ToHexString(),
                Is.EqualTo("e507a0f0d141fc8ee27459e661bd8459f51199967afdcb76b0155459b660954be94fe2c28002"));
            Assert.That(new CancelMessageSerializer().Encode(new CancelMessage(7)).ToHexString(), Is.EqualTo("c107"));
            Assert.That(kindOneBody.ToHexString(), Is.EqualTo("cac5c480827f0080c2c0c0"));
            Assert.That(entries, Has.Count.EqualTo(1));
            Assert.That(entries[0].IsFull, Is.True);
            Assert.That(entries[0].Hash, Is.EqualTo(ValueKeccak.Compute([0x7f, 0x00])));
        }
    }

    [Test]
    public void Profile_id_is_the_domain_separated_keccak_of_the_aggregated_key()
    {
        byte[] preimage = [.. "lean/1/profile"u8, 0, .. Eip8288Constants.AggregatedVk];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanObjectTransport.LocalProfile, Is.EqualTo(ValueKeccak.Compute(preimage)));
            // Pinned with the LeanVM guest: a different enabled_schemes set needs a new key and so a new profile.
            Assert.That(LeanObjectTransport.LocalProfile.ToString(false),
                Is.EqualTo("d4822e0deca6b14894dcfbd137b2bcaf9dd225f869afd47fc942afc015e0d370"));
        }
    }

    [TestCase(1)]
    [TestCase(LeanProtocol.ChunkBytes)]
    [TestCase(5 * LeanProtocol.ChunkBytes + 17)]
    [TestCase(17 * LeanProtocol.ChunkBytes)]
    public void Every_branch_verifies_and_a_changed_byte_or_index_does_not(int size)
    {
        byte[] body = VectorBody(size);
        LeanDescriptor descriptor = VectorDescriptor(body, out LeanChunkTree tree);
        LeanBranchVerifier verifier = new(descriptor);
        for (int index = 0; index < descriptor.ChunkCount; index++)
        {
            byte[] chunk = body.AsSpan(index * LeanProtocol.ChunkBytes, descriptor.ChunkLength(index)).ToArray();
            ValueHash256[] branch = tree.GetBranch(index);
            Assert.That(verifier.Verify(index, chunk, branch), Is.True, $"chunk {index}");
            chunk[^1] ^= 1;
            Assert.That(verifier.Verify(index, chunk, branch), Is.False, $"corrupt chunk {index}");
        }
        Assert.That(verifier.Verify(descriptor.ChunkCount, [1], tree.GetBranch(0)), Is.False, "index equal to N");
        Assert.That(verifier.Verify(0, body.AsSpan(0, descriptor.ChunkLength(0)), [.. tree.GetBranch(0), default]), Is.False, "wrong depth");
    }

    [Test]
    public void Noncanonical_padding_sibling_is_rejected_even_when_it_hashes_to_the_root()
    {
        byte[] body = VectorBody(2 * LeanProtocol.ChunkBytes + 1);
        LeanDescriptor canonical = VectorDescriptor(body, out _);
        byte[] seed = canonical.Seed;
        // Build a three-chunk tree whose padding leaf is not the canonical empty leaf, and a descriptor committing to it.
        ValueHash256[] leaves = new ValueHash256[4];
        for (int i = 0; i < 3; i++) leaves[i] = LeanCommitment.Leaf(seed, i, body.AsSpan(i * LeanProtocol.ChunkBytes, canonical.ChunkLength(i)));
        leaves[3] = ValueKeccak.Compute("not padding"u8);
        ValueHash256 left = LeanCommitment.Node(0, leaves[0], leaves[1]);
        ValueHash256 right = LeanCommitment.Node(0, leaves[2], leaves[3]);
        ValueHash256 root = LeanCommitment.Root(seed, LeanCommitment.Node(1, left, right));
        LeanDescriptor forged = LeanDescriptor.Decode(LeanRlp.EncodeList(LeanRlp.EncodeUInt(LeanProtocol.KindWrapper),
            LeanRlp.EncodeBytes(canonical.ProfileId.Bytes), LeanDescriptor.WrapperContext(), LeanRlp.EncodeUInt(canonical.ByteLength),
            LeanRlp.EncodeBytes(canonical.ContentHash.Bytes), LeanRlp.EncodeBytes(root.Bytes)));
        byte[] lastChunk = body.AsSpan(2 * LeanProtocol.ChunkBytes).ToArray();
        Assert.That(new LeanBranchVerifier(forged).Verify(2, lastChunk, [leaves[3], left]), Is.False);
    }

    [TestCase("kind")]
    [TestCase("profile")]
    [TestCase("content")]
    [TestCase("length")]
    public void Changed_descriptor_fields_break_the_commitment(string field)
    {
        byte[] body = VectorBody(3 * LeanProtocol.ChunkBytes);
        LeanDescriptor descriptor = VectorDescriptor(body, out LeanChunkTree tree);
        ValueHash256 other = ValueKeccak.Compute("other"u8);
        LeanDescriptor changed = LeanDescriptor.Decode(LeanRlp.EncodeList(
            LeanRlp.EncodeUInt(field == "kind" ? LeanProtocol.KindInclusionList : LeanProtocol.KindWrapper),
            LeanRlp.EncodeBytes((field == "profile" ? other : descriptor.ProfileId).Bytes),
            field == "kind" ? LeanDescriptor.InclusionListContext(descriptor.ContentHash) : LeanDescriptor.WrapperContext(),
            LeanRlp.EncodeUInt(field == "length" ? descriptor.ByteLength - 1 : descriptor.ByteLength),
            LeanRlp.EncodeBytes((field == "content" ? other : descriptor.ContentHash).Bytes),
            LeanRlp.EncodeBytes(descriptor.ChunkRoot.Bytes)));
        Assert.That(new LeanBranchVerifier(changed).Verify(0, body.AsSpan(0, LeanProtocol.ChunkBytes), tree.GetBranch(0)), Is.False);
    }

    /// <summary>The boundary sizes of the EIP's <c>check_vectors.py</c>, both sides of chunk and tree boundaries up to the maximum geometry.</summary>
    [TestCase(1)]
    [TestCase(LeanProtocol.ChunkBytes - 1)]
    [TestCase(LeanProtocol.ChunkBytes)]
    [TestCase(LeanProtocol.ChunkBytes + 1)]
    [TestCase(2 * LeanProtocol.ChunkBytes)]
    [TestCase(2 * LeanProtocol.ChunkBytes + 1)]
    [TestCase(4 * LeanProtocol.ChunkBytes + 1)]
    [TestCase((int)LeanProtocol.MaxObjectBytes)]
    public void Boundary_geometry_verifies_and_tampering_does_not(int size)
    {
        byte[] body = VectorBody(size);
        LeanDescriptor descriptor = VectorDescriptor(body, out LeanChunkTree tree);
        LeanBranchVerifier verifier = new(descriptor);
        int count = descriptor.ChunkCount;
        Assert.That(descriptor.Encoded.Length, Is.LessThanOrEqualTo(LeanProtocol.MaxDescriptorBytes));
        foreach (int index in new SortedSet<int> { 0, count / 2, count - 1 })
        {
            byte[] chunk = body.AsSpan(index * LeanProtocol.ChunkBytes, descriptor.ChunkLength(index)).ToArray();
            ValueHash256[] branch = tree.GetBranch(index);
            byte[] changed = [.. chunk];
            changed[0] ^= 1;
            using (Assert.EnterMultipleScope())
            {
                Assert.That(verifier.Verify(index, chunk, branch), Is.True, $"chunk {index}");
                Assert.That(verifier.Verify(count, chunk, branch), Is.False, "index equal to N");
                Assert.That(verifier.Verify(index, chunk.AsSpan(0, chunk.Length - 1), branch), Is.False, "short chunk");
                Assert.That(verifier.Verify(index, changed, branch), Is.False, "changed chunk");
                Assert.That(verifier.Verify(index, chunk, [.. branch, default]), Is.False, "extra sibling");
                if (branch.Length > 0)
                {
                    ValueHash256[] bad = [.. branch];
                    byte[] sibling = bad[0].ToByteArray();
                    sibling[0] ^= 1;
                    bad[0] = new ValueHash256(sibling);
                    Assert.That(verifier.Verify(index, chunk, bad), Is.False, "changed sibling");
                }
            }
        }
    }

    [Test]
    public void Every_descriptor_field_is_bound_to_the_commitment()
    {
        int size = 2 * LeanProtocol.ChunkBytes + 1;
        byte[] body = VectorBody(size);
        LeanDescriptor descriptor = VectorDescriptor(body, out LeanChunkTree tree);
        int last = descriptor.ChunkCount - 1;
        byte[] chunk = body.AsSpan(last * LeanProtocol.ChunkBytes).ToArray();
        ValueHash256[] branch = tree.GetBranch(last);
        byte[] profile = new byte[32];
        profile[0] = 1;
        static byte[] Encode(byte kind, ReadOnlySpan<byte> profileId, byte[] context, ulong length, in ValueHash256 content, in ValueHash256 root) =>
            LeanRlp.EncodeList(LeanRlp.EncodeUInt(kind), LeanRlp.EncodeBytes(profileId), context, LeanRlp.EncodeUInt(length),
                LeanRlp.EncodeBytes(content.Bytes), LeanRlp.EncodeBytes(root.Bytes));
        byte[] wrapper = LeanDescriptor.WrapperContext();
        ValueHash256 content = descriptor.ContentHash, root = descriptor.ChunkRoot;
        byte[][] altered =
        [
            Encode(LeanProtocol.KindBlockProof, descriptor.ProfileId.Bytes,
                LeanDescriptor.BlockProofContext(default, 0, default, default, default), (ulong)size, content, root),
            Encode(LeanProtocol.KindWrapper, profile, wrapper, (ulong)size, content, root),
            Encode(LeanProtocol.KindWrapper, descriptor.ProfileId.Bytes, LeanDescriptor.InclusionListContext(content), (ulong)size, content, root),
            Encode(LeanProtocol.KindWrapper, descriptor.ProfileId.Bytes, wrapper, (ulong)size + 1, content, root),
            Encode(LeanProtocol.KindWrapper, descriptor.ProfileId.Bytes, wrapper, (ulong)size + LeanProtocol.ChunkBytes, content, root),
            Encode(LeanProtocol.KindWrapper, descriptor.ProfileId.Bytes, wrapper, (ulong)size, default, root),
            Encode(LeanProtocol.KindWrapper, descriptor.ProfileId.Bytes, wrapper, (ulong)size, content, default)
        ];
        foreach (byte[] encoded in altered)
        {
            LeanDescriptor changed;
            try { changed = LeanDescriptor.Decode(encoded); }
            catch (RlpException) { continue; }
            Assert.That(new LeanBranchVerifier(changed).Verify(last, chunk, branch), Is.False, encoded.ToHexString());
        }
        Assert.Throws<RlpException>(() => LeanDescriptor.Decode(Encode(LeanProtocol.KindWrapper, descriptor.ProfileId.Bytes, wrapper, 0, content, root)));
        Assert.Throws<RlpException>(() => LeanDescriptor.Decode(Encode(LeanProtocol.KindWrapper, descriptor.ProfileId.Bytes, wrapper,
            LeanProtocol.MaxObjectBytes + 1, content, root)));
    }

    [Test]
    public void Other_kinds_and_profiles_use_the_same_commitment()
    {
        byte[] body = VectorBody(2 * LeanProtocol.ChunkBytes + 1);
        ValueHash256 profile = ValueKeccak.Compute("profile"u8);
        (byte Kind, byte[] Context)[] contexts =
        [
            (LeanProtocol.KindBlockProof, LeanDescriptor.BlockProofContext(default, 42, default, default, default)),
            (LeanProtocol.KindInclusionList, LeanDescriptor.InclusionListContext(ValueKeccak.Compute(body)))
        ];
        foreach ((byte kind, byte[] context) in contexts)
        {
            LeanDescriptor descriptor = LeanDescriptor.Create(kind, profile, context, body, out LeanChunkTree tree);
            Assert.That(new LeanBranchVerifier(descriptor).Verify(0, body.AsSpan(0, LeanProtocol.ChunkBytes), tree.GetBranch(0)), Is.True);
        }
    }

    [Test]
    public void Maximum_chunk_and_announcement_fit_the_message_ceiling()
    {
        ChunkMessage chunk = new(ulong.MaxValue, default, LeanProtocol.MaxChunkCount - 1, new byte[LeanProtocol.ChunkBytes],
            new ValueHash256[LeanCommitment.Depth(LeanProtocol.MaxChunkCount)]);
        byte[] context = LeanDescriptor.BlockProofContext(default, ulong.MaxValue, default, default, default);
        byte[] descriptor = LeanRlp.EncodeList(LeanRlp.EncodeUInt(LeanProtocol.KindBlockProof), LeanRlp.EncodeBytes(new byte[32]), context,
            LeanRlp.EncodeUInt(LeanProtocol.MaxObjectBytes), LeanRlp.EncodeBytes(new byte[32]), LeanRlp.EncodeBytes(new byte[32]));
        LeanDescriptor decoded = LeanDescriptor.Decode(descriptor);
        LeanDescriptor[] announced = new LeanDescriptor[LeanProtocol.MaxAnnouncements];
        Array.Fill(announced, decoded);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(new ChunkMessageSerializer().Encode(chunk).Length, Is.LessThan(LeanProtocol.MaxMessageBytes));
            Assert.That(descriptor.Length, Is.LessThanOrEqualTo(LeanProtocol.MaxDescriptorBytes));
            Assert.That(decoded.Depth, Is.EqualTo(10));
            Assert.That(new AnnounceObjectsMessageSerializer().Encode(new AnnounceObjectsMessage(announced)).Length,
                Is.LessThan(LeanProtocol.MaxMessageBytes));
        }
    }

    private static IEnumerable<TestCaseData> MessageRoundTrips()
    {
        LeanDescriptor descriptor = VectorDescriptor(VectorBody(70_000), out LeanChunkTree tree);
        ValueHash256[] hashes = [TestItem.KeccakA.ValueHash256, TestItem.KeccakB.ValueHash256];
        Array.Sort(hashes, static (a, b) => a.Bytes.SequenceCompareTo(b.Bytes));
        yield return new TestCaseData(new LeanStatusMessage(10088289, TestItem.KeccakA.ValueHash256, [LeanObjectTransport.LocalProfile], 7, 1 << 24),
            new LeanStatusMessageSerializer()).SetName("Status");
        yield return new TestCaseData(new AnnounceObjectsMessage([descriptor]), new AnnounceObjectsMessageSerializer()).SetName("AnnounceObjects");
        yield return new TestCaseData(new GetObjectsMessage(1, [new(1, LeanObjectTransport.LocalProfile, 0, descriptor.ObjectId),
            new(1, LeanObjectTransport.LocalProfile, 1, hashes[0])]), new GetObjectsMessageSerializer()).SetName("GetObjects");
        yield return new TestCaseData(new ObjectsMessage(1, [new(LeanResultStatus.Ok, descriptor, null), LeanObjectResult.Of(LeanResultStatus.Busy)]),
            new ObjectsMessageSerializer()).SetName("Objects");
        yield return new TestCaseData(new GetChunksMessage(2, descriptor.ObjectId, [0, 1]), new GetChunksMessageSerializer()).SetName("GetChunks");
        yield return new TestCaseData(new ChunkMessage(2, descriptor.ObjectId, 1, VectorBody(70_000).AsMemory(LeanProtocol.ChunkBytes), tree.GetBranch(1)),
            new ChunkMessageSerializer()).SetName("Chunk");
        yield return new TestCaseData(new CompleteMessage(2, descriptor.ObjectId, LeanCompleteStatus.Cancelled), new CompleteMessageSerializer()).SetName("Complete");
        yield return new TestCaseData(new CancelMessage(ulong.MaxValue), new CancelMessageSerializer()).SetName("Cancel");
        yield return new TestCaseData(new GetTransactionsMessage(3, hashes), new GetTransactionsMessageSerializer()).SetName("GetTransactions");
        yield return new TestCaseData(new TransactionsMessage(3, [(LeanResultStatus.Ok, [0x7f, 0x00]), (LeanResultStatus.TooLarge, [])]),
            new TransactionsMessageSerializer()).SetName("Transactions");
    }

    [TestCaseSource(nameof(MessageRoundTrips))]
    public void Messages_round_trip_canonically(LeanMessage message, object serializer)
    {
        dynamic typed = serializer;
        byte[] encoded = typed.Encode((dynamic)message);
        using DisposableByteBuffer buffer = Unpooled.Buffer().AsDisposable();
        ((dynamic)serializer).Serialize(buffer, (dynamic)message);
        byte[] serialized = new byte[buffer.ReadableBytes];
        buffer.GetBytes(buffer.ReaderIndex, serialized);
        LeanMessage decoded = typed.Deserialize(buffer);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(serialized, Is.EqualTo(encoded));
            Assert.That((byte[])typed.Encode((dynamic)decoded), Is.EqualTo(encoded));
            Assert.That(buffer.ReadableBytes, Is.Zero);
            Assert.That(decoded.PacketType, Is.EqualTo(message.PacketType));
        }
    }

    private static IEnumerable<TestCaseData> MalformedMessages()
    {
        LeanDescriptor small = VectorDescriptor(VectorBody(10), out _);
        LeanDescriptor large = VectorDescriptor(VectorBody(70_000), out _);
        byte[] profile = LeanRlp.EncodeBytes(LeanObjectTransport.LocalProfile.Bytes);
        byte[] hash = LeanRlp.EncodeBytes(TestItem.KeccakA.Bytes);
        byte[] Selector(ulong kind, ulong lookup, byte[]? key = null) =>
            LeanRlp.EncodeList(LeanRlp.EncodeUInt(kind), profile, LeanRlp.EncodeUInt(lookup), key ?? hash);
        byte[] Status(ulong version, ulong kinds, ulong max, params byte[][] profiles) => LeanRlp.EncodeList(LeanRlp.EncodeUInt(version),
            LeanRlp.EncodeUInt(1), hash, LeanRlp.EncodeList(profiles), LeanRlp.EncodeUInt(kinds), LeanRlp.EncodeUInt(max));
        byte[][] Many(int count, Func<int, byte[]> item)
        {
            byte[][] items = new byte[count][];
            for (int i = 0; i < count; i++) items[i] = item(i);
            return items;
        }

        yield return new TestCaseData(new CancelMessageSerializer(), "c3820007").SetName("Nonminimal integer");
        yield return new TestCaseData(new CancelMessageSerializer(), "c28107").SetName("Prefixed single byte");
        yield return new TestCaseData(new CancelMessageSerializer(), "c20707").SetName("Extra field");
        yield return new TestCaseData(new CancelMessageSerializer(), "c10700").SetName("Trailing bytes");
        yield return new TestCaseData(new CancelMessageSerializer(), "c1c0").SetName("List for integer");
        yield return new TestCaseData(new CancelMessageSerializer(), "8107").SetName("String for message list");
        yield return new TestCaseData(new CancelMessageSerializer(), "f8020107").SetName("Nonminimal length prefix");
        yield return new TestCaseData(new LeanStatusMessageSerializer(), Status(2, 1, 1, profile).ToHexString()).SetName("Status version");
        yield return new TestCaseData(new LeanStatusMessageSerializer(), Status(1, 2, 1, profile).ToHexString()).SetName("Status without kind 1");
        yield return new TestCaseData(new LeanStatusMessageSerializer(), Status(1, 9, 1, profile).ToHexString()).SetName("Status unknown kind bit");
        yield return new TestCaseData(new LeanStatusMessageSerializer(), Status(1, 1, 0, profile).ToHexString()).SetName("Status zero ceiling");
        yield return new TestCaseData(new LeanStatusMessageSerializer(), Status(1, 1, LeanProtocol.MaxObjectBytes + 1, profile).ToHexString())
            .SetName("Status ceiling above MAX_OBJECT_BYTES");
        yield return new TestCaseData(new LeanStatusMessageSerializer(), Status(1, 1, 1).ToHexString()).SetName("Status without profiles");
        yield return new TestCaseData(new LeanStatusMessageSerializer(), Status(1, 1, 1, profile, profile).ToHexString()).SetName("Status duplicate profiles");
        yield return new TestCaseData(new LeanStatusMessageSerializer(), Status(1, 1, 1, Many(17, i => LeanRlp.EncodeBytes(ValueKeccak.Compute([(byte)i]).Bytes))).ToHexString())
            .SetName("Status too many profiles");
        yield return new TestCaseData(new AnnounceObjectsMessageSerializer(), LeanRlp.EncodeList().ToHexString()).SetName("Empty announcement");
        yield return new TestCaseData(new AnnounceObjectsMessageSerializer(), LeanRlp.EncodeList(Many(LeanProtocol.MaxAnnouncements + 1, i =>
            VectorDescriptor(VectorBody(i + 1), out _).Encoded)).ToHexString()).SetName("Oversized announcement");
        yield return new TestCaseData(new AnnounceObjectsMessageSerializer(), (small.ObjectId.Bytes.SequenceCompareTo(large.ObjectId.Bytes) > 0
            ? LeanRlp.EncodeList(small.Encoded, large.Encoded) : LeanRlp.EncodeList(large.Encoded, small.Encoded)).ToHexString()).SetName("Unsorted announcement");
        yield return new TestCaseData(new AnnounceObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), profile,
            LeanDescriptor.WrapperContext(), LeanRlp.EncodeUInt(0), hash, hash)).ToHexString()).SetName("Zero-length body");
        yield return new TestCaseData(new AnnounceObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), profile,
            LeanDescriptor.WrapperContext(), LeanRlp.EncodeUInt(LeanProtocol.MaxObjectBytes + 1), hash, hash)).ToHexString()).SetName("Body above MAX_OBJECT_BYTES");
        yield return new TestCaseData(new AnnounceObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(3), profile,
            LeanDescriptor.InclusionListContext(TestItem.KeccakB.ValueHash256), LeanRlp.EncodeUInt(1), hash, hash)).ToHexString())
            .SetName("Package hash differs from content hash");
        yield return new TestCaseData(new AnnounceObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), profile,
            LeanRlp.EncodeList(hash), LeanRlp.EncodeUInt(1), hash, hash)).ToHexString()).SetName("Wrong kind-1 context");
        yield return new TestCaseData(new AnnounceObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(4), profile,
            LeanDescriptor.WrapperContext(), LeanRlp.EncodeUInt(1), hash, hash)).ToHexString()).SetName("Unknown kind");
        yield return new TestCaseData(new GetObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(Many(LeanProtocol.MaxLookups + 1, i => Selector(1, 0, LeanRlp.EncodeBytes(ValueKeccak.Compute([(byte)i]).Bytes))))).ToHexString())
            .SetName("Oversized lookup");
        yield return new TestCaseData(new GetObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(Selector(2, 0), Selector(1, 0))).ToHexString()).SetName("Unsorted selectors");
        yield return new TestCaseData(new GetObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(Selector(1, 0), Selector(1, 0))).ToHexString()).SetName("Duplicate selectors");
        yield return new TestCaseData(new GetObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(Selector(2, 1))).ToHexString()).SetName("Transaction lookup for kind 2");
        yield return new TestCaseData(new GetObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(Selector(1, 2))).ToHexString()).SetName("Unknown lookup kind");
        yield return new TestCaseData(new GetObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(Selector(1, 0, LeanRlp.EncodeBytes(new byte[31])))).ToHexString()).SetName("Short lookup key");
        yield return new TestCaseData(new GetChunksMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), hash,
            LeanRlp.EncodeList(LeanRlp.EncodeUInt(2), LeanRlp.EncodeUInt(2))).ToHexString()).SetName("Duplicate indices");
        yield return new TestCaseData(new GetChunksMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), hash,
            LeanRlp.EncodeList(LeanRlp.EncodeUInt(3), LeanRlp.EncodeUInt(2))).ToHexString()).SetName("Unsorted indices");
        yield return new TestCaseData(new GetChunksMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), hash,
            LeanRlp.EncodeList(Many(LeanProtocol.MaxChunksPerRequest + 1, i => LeanRlp.EncodeUInt((ulong)i)))).ToHexString()).SetName("Oversized index list");
        yield return new TestCaseData(new GetChunksMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), hash, LeanRlp.EncodeList()).ToHexString())
            .SetName("Empty index list");
        yield return new TestCaseData(new GetTransactionsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(hash, hash)).ToHexString()).SetName("Duplicate transaction hashes");
        yield return new TestCaseData(new GetTransactionsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(Many(LeanProtocol.MaxTxsPerRequest + 1, i => LeanRlp.EncodeBytes(ValueKeccak.Compute([(byte)i]).Bytes)))).ToHexString())
            .SetName("Oversized transaction request");
        yield return new TestCaseData(new TransactionsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), LeanRlp.EncodeBytes([1])))).ToHexString()).SetName("Envelope with non-OK status");
        yield return new TestCaseData(new ObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(0), small.Encoded, LeanRlp.EncodeBytes([1])))).ToHexString())
            .SetName("Auxiliary for kind 1");
        yield return new TestCaseData(new ObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1),
            LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), small.Encoded, LeanRlp.EncodeBytes([])))).ToHexString())
            .SetName("Descriptor with non-OK status");
        yield return new TestCaseData(new CompleteMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), hash, LeanRlp.EncodeUInt(6)).ToHexString())
            .SetName("Unknown Complete status");
        yield return new TestCaseData(new ChunkMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), hash, LeanRlp.EncodeUInt(0),
            LeanRlp.EncodeBytes(new byte[LeanProtocol.ChunkBytes + 1]), LeanRlp.EncodeList()).ToHexString()).SetName("Chunk above CHUNK_BYTES");
        byte[] nested = LeanRlp.EncodeList();
        for (int i = 0; i < LeanProtocol.MaxRlpDepth; i++) nested = LeanRlp.EncodeList(nested);
        yield return new TestCaseData(new AnnounceObjectsMessageSerializer(), LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), profile,
            nested, LeanRlp.EncodeUInt(1), hash, hash)).ToHexString()).SetName("Nesting beyond MAX_RLP_DEPTH");
    }

    [TestCaseSource(nameof(MalformedMessages))]
    public void Malformed_message_data_is_rejected(object serializer, string hex)
    {
        using DisposableByteBuffer buffer = Unpooled.WrappedBuffer(Bytes.FromHexString(hex)).AsDisposable();
        Assert.Throws<RlpException>(() => ((dynamic)serializer).Deserialize(buffer));
    }

    [TestCase(LeanProtocol.MaxMessageBytes + 1, typeof(CancelMessageSerializer))]
    [TestCase(LeanProtocol.MaxMetadataResponseBytes + 1, typeof(ObjectsMessageSerializer))]
    [TestCase(LeanProtocol.MaxTxResponseBytes + 1, typeof(TransactionsMessageSerializer))]
    public void Oversized_messages_are_rejected_before_parsing(int size, Type serializerType)
    {
        using DisposableByteBuffer buffer = Unpooled.WrappedBuffer(new byte[size]).AsDisposable();
        dynamic serializer = Activator.CreateInstance(serializerType)!;
        Assert.Throws<RlpException>(() => serializer.Deserialize(buffer));
        Assert.That(buffer.ReaderIndex, Is.Zero);
    }

    private static IEnumerable<TestCaseData> InvalidBodies()
    {
        byte[] envelope = LeanRlp.EncodeBytes([0x7f, 0x00]);
        byte[] hash = LeanRlp.EncodeBytes(TestItem.KeccakA.Bytes);
        byte[] Wrapper(byte[] transactions, ulong mode = 0, byte[]? deps = null, byte[]? proofs = null) => LeanRlp.EncodeList(transactions,
            LeanRlp.EncodeUInt(mode), LeanRlp.EncodeList(deps ?? LeanRlp.EncodeList(), proofs ?? LeanRlp.EncodeList()));
        byte[] Full(byte[] e) => LeanRlp.EncodeList(LeanRlp.EncodeUInt(0), e);
        byte[] ByHash(byte[] h) => LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), h);
        ValueHash256 lower = TestItem.KeccakA.ValueHash256, higher = TestItem.KeccakB.ValueHash256;
        if (lower.Bytes.SequenceCompareTo(higher.Bytes) > 0) (lower, higher) = (higher, lower);

        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList())).SetName("No transactions");
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(envelope))).SetName("Untagged entry");
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(LeanRlp.EncodeList(LeanRlp.EncodeUInt(2), envelope))))
            .SetName("Unknown tag");
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(ByHash(LeanRlp.EncodeBytes(new byte[31])))))
            .SetName("Short hash entry");
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(ByHash(hash), ByHash(hash)))).SetName("Duplicate hashes");
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(ByHash(LeanRlp.EncodeBytes(higher.Bytes)),
            ByHash(LeanRlp.EncodeBytes(lower.Bytes))))).SetName("Unsorted hashes");
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(Full(envelope)), 2)).SetName("Unknown mode");
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(Full(envelope)), 0,
            LeanRlp.EncodeList(LeanRlp.EncodeBytes(new byte[95])))).SetName("Dependency not 96 bytes");
        byte[] Dependency(byte scheme, byte padding = 0)
        {
            byte[] dependency = new byte[Eip8288Constants.DependencyTripleLength];
            dependency[0] = padding;
            dependency[31] = scheme;
            return LeanRlp.EncodeList(LeanRlp.EncodeBytes(dependency));
        }
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(Full(envelope)), 0, Dependency(0x12)))
            .SetName("Dependency scheme outside the profile");
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(Full(envelope)), 0, Dependency(0x00)))
            .SetName("Dependency scheme zero");
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(Full(envelope)), 0,
            Dependency(Eip8288Constants.LeanSphincsScheme, padding: 1))).SetName("Dependency with nonzero scheme padding");
        yield return new TestCaseData(LeanProtocol.KindWrapper, Wrapper(LeanRlp.EncodeList(Full(envelope)), 1, null, LeanRlp.EncodeList()))
            .SetName("Mode 1 with proof list");
        yield return new TestCaseData(LeanProtocol.KindBlockProof, LeanRlp.EncodeList(LeanRlp.EncodeBytes([1]), LeanRlp.EncodeBytes([1])))
            .SetName("Kind 2 extra field");
        yield return new TestCaseData(LeanProtocol.KindInclusionList, LeanRlp.EncodeList(LeanRlp.EncodeList(envelope),
            LeanRlp.EncodeList(LeanRlp.EncodeBytes([1])))).SetName("Kind 3 without deps hash");
    }

    [TestCaseSource(nameof(InvalidBodies))]
    public void Noncanonical_bodies_are_rejected(byte kind, byte[] body) =>
        Assert.Throws<RlpException>(() => LeanBodies.Check(kind, body));

    internal static BlockHeader ProofHeader(byte[] proof, Hash256? depsHash = null, ulong number = 7)
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(number).WithTimestamp(1000).TestObject;
        header.BaseFeePerGas = 7;
        header.WithdrawalsRoot = Keccak.EmptyTreeHash;
        header.BlobGasUsed = 0;
        header.ExcessBlobGas = 0;
        header.ParentBeaconBlockRoot = TestItem.KeccakC;
        header.RequestsHash = TestItem.KeccakD;
        header.BlockAccessListHash = TestItem.KeccakE;
        header.SlotNumber = 9;
        header.RecursiveStark = new RecursiveStark(proof, depsHash ?? new Hash256(Eip8288Dependencies.ComputeDepsHash([])));
        header.Hash = header.CalculateHash();
        return header;
    }

    internal static LeanDescriptor SidecarDescriptor(BlockHeader header, LeanHeaderSkeleton skeleton, out byte[] body)
    {
        body = LeanBodies.EncodeBlockProof(header.RecursiveStark!.StarkProof);
        return LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(header.Hash!.ValueHash256, header.Number, header.TxRoot!.ValueHash256,
                header.RecursiveStark.BlockDepsHash.ValueHash256, skeleton.Hash), body, out _);
    }

    [Test]
    public void Skeleton_reconstructs_the_header_and_validates_against_its_schema([Values(0, 100_000)] int proofBytes)
    {
        byte[] proof = new byte[proofBytes];
        new Random(5).NextBytes(proof);
        BlockHeader header = ProofHeader(proof);
        LeanHeaderSkeleton skeleton = LeanHeaderSkeleton.FromHeader(header, new HeaderDecoder());
        LeanHeaderSkeleton decoded = LeanHeaderSkeleton.Decode(skeleton.Encoded);
        LeanDescriptor descriptor = SidecarDescriptor(header, skeleton, out byte[] body);
        byte[] reconstructed = decoded.Reconstruct(LeanBodies.ParseBlockProof(body), descriptor.BlockDepsHash);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(decoded.ProofFieldIndex, Is.EqualTo(decoded.Fields.Length - 1));
            Assert.That(decoded.Fields[decoded.ProofFieldIndex], Is.Empty);
            Assert.That(decoded.Hash, Is.EqualTo(descriptor.SkeletonHash));
            Assert.That(decoded.TryValidate(descriptor, new HeaderDecoder(), new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance), out string? error),
                Is.True, error);
            Assert.That(ValueKeccak.Compute(reconstructed), Is.EqualTo(header.Hash!.ValueHash256));
        }
    }

    private static IEnumerable<TestCaseData> InvalidSkeletons()
    {
        yield return new TestCaseData("double-encoded field").SetName("Double RLP encoding of a field entry");
        yield return new TestCaseData("misplaced placeholder").SetName("Misplaced proof placeholder");
        yield return new TestCaseData("missing field").SetName("Wrong field count");
        yield return new TestCaseData("changed number").SetName("Skeleton disagrees with the context");
        yield return new TestCaseData("wrong hash").SetName("Wrong skeleton hash");
        yield return new TestCaseData("pre-fork").SetName("Fork without recursive_stark");
    }

    [TestCaseSource(nameof(InvalidSkeletons))]
    public void Skeletons_violating_the_header_schema_fail_validation(string mutation)
    {
        BlockHeader header = ProofHeader([1, 2, 3], new Hash256(ValueKeccak.Compute("deps"u8)));
        LeanHeaderSkeleton original = LeanHeaderSkeleton.FromHeader(header, new HeaderDecoder());
        byte[][] fields = (byte[][])original.Fields.Clone();
        int proofIndex = original.ProofFieldIndex;
        switch (mutation)
        {
            case "double-encoded field": fields[0] = LeanRlp.EncodeBytes(fields[0]); break;
            case "misplaced placeholder":
                (fields[proofIndex], fields[proofIndex - 1]) = (fields[proofIndex - 1], fields[proofIndex]);
                proofIndex--;
                break;
            case "missing field":
                fields = [.. fields[..(proofIndex - 1)], fields[proofIndex]];
                proofIndex--;
                break;
            case "changed number": fields[8] = LeanRlp.EncodeUInt(header.Number + 1); break;
        }
        LeanHeaderSkeleton skeleton = Skeleton(proofIndex, fields);
        LeanDescriptor descriptor = SidecarDescriptor(header, mutation == "wrong hash" ? original : skeleton, out _);
        if (mutation == "wrong hash") skeleton = Skeleton(proofIndex, [LeanRlp.EncodeBytes(TestItem.KeccakF.Bytes), .. fields[1..]]);
        TestSingleReleaseSpecProvider specs = new(mutation == "pre-fork" ? Prague.Instance : Eip8288Prototype.Instance);
        Assert.That(skeleton.TryValidate(descriptor, new HeaderDecoder(), specs, out _), Is.False);
    }

    private static LeanHeaderSkeleton Skeleton(int proofIndex, byte[][] fields)
    {
        byte[][] entries = new byte[fields.Length][];
        for (int i = 0; i < fields.Length; i++) entries[i] = LeanRlp.EncodeBytes(fields[i]);
        return LeanHeaderSkeleton.Decode(LeanRlp.EncodeList(LeanRlp.EncodeUInt((ulong)proofIndex), LeanRlp.EncodeList(entries)));
    }

    [TestCase("nonempty placeholder")]
    [TestCase("empty field")]
    [TestCase("index out of range")]
    [TestCase("too many fields")]
    [TestCase("oversized")]
    [TestCase("deeply nested field")]
    public void Malformed_skeletons_are_rejected_before_any_proof_allocation(string mutation)
    {
        static byte[] Entries(params byte[][] fields)
        {
            byte[][] entries = new byte[fields.Length][];
            for (int i = 0; i < fields.Length; i++) entries[i] = LeanRlp.EncodeBytes(fields[i]);
            return LeanRlp.EncodeList(entries);
        }
        byte[] field = LeanRlp.EncodeUInt(1);
        byte[] nested = LeanRlp.EncodeList();
        for (int i = 0; i < LeanProtocol.MaxRlpDepth; i++) nested = LeanRlp.EncodeList(nested);
        byte[] encoded = mutation switch
        {
            "nonempty placeholder" => LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), Entries(field, field)),
            "empty field" => LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), Entries([], [])),
            "index out of range" => LeanRlp.EncodeList(LeanRlp.EncodeUInt(2), Entries(field, [])),
            "too many fields" => LeanRlp.EncodeList(LeanRlp.EncodeUInt(0), Entries([.. new byte[LeanProtocol.MaxHeaderFields + 1][]])),
            "oversized" => LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), Entries(LeanRlp.EncodeBytes(new byte[LeanProtocol.MaxHeaderSkeletonBytes]), [])),
            _ => LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), Entries(nested, []))
        };
        Assert.Throws<RlpException>(() => LeanHeaderSkeleton.Decode(encoded));
    }
}
