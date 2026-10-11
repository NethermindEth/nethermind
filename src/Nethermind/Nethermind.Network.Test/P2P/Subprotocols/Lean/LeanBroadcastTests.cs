// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Lean.LeanTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

/// <summary>Accepts manifests whose signature is the manifest ID itself; a stand-in for a network's producer-duty profile.</summary>
internal sealed class TestBroadcastProfile : ILeanBroadcastProfile
{
    public static readonly ValueHash256 ProfileId = ValueKeccak.Compute("test broadcast profile"u8);
    private readonly DateTimeOffset _genesis = DateTimeOffset.UtcNow;

    public ValueHash256 Id => ProfileId;
    public ulong CurrentSlot { get; set; } = 10;
    public bool Unresolved { get; set; }
    public bool ContextMismatch { get; set; }

    public DateTimeOffset SlotStart(ulong slot) => _genesis + TimeSpan.FromSeconds(12.0 * ((double)slot - CurrentSlot + 1));

    public LeanBroadcastAuthorization Authorize(LeanBroadcastManifest manifest, in ValueHash256 manifestId, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> authorization) =>
        Unresolved ? LeanBroadcastAuthorization.Unresolved
        : signature.SequenceEqual(manifestId.Bytes) ? LeanBroadcastAuthorization.Authorized
        : LeanBroadcastAuthorization.Rejected;

    public bool MatchesContext(LeanBroadcastManifest manifest, ReadOnlySpan<byte> body) => !ContextMismatch;

    public static byte[] Sign(LeanBroadcastManifest manifest) => manifest.Id(1, LeanTestNode.Genesis.ValueHash256).ToByteArray();
}

/// <summary>
/// EIP-8437 ethp2p broadcast: the fixed Reed-Solomon code, manifests and shard encodings ported from
/// <c>assets/eip-8437/check_broadcast.py</c>, framework wire encodings, and the session engine between in-process peers.
/// </summary>
public class LeanBroadcastTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] Body = "12345678901234567"u8.ToArray();
    private static readonly ValueHash256 Genesis = new(Bytes.FromHexString("5555555555555555555555555555555555555555555555555555555555555555"));
    private static readonly ValueHash256 PackageProfile = new(Bytes.FromHexString("2222222222222222222222222222222222222222222222222222222222222222"));

    private static LeanBroadcastManifest Fixture(byte[] body, out byte[][] shards)
    {
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindInclusionList, PackageProfile,
            LeanDescriptor.InclusionListContext(ValueKeccak.Compute(body)), body, out _);
        return LeanBroadcastManifest.Create(new ValueHash256(Enumerable.Repeat((byte)0x11, 32).ToArray()), [0x33, 0x33, 0x33, 0x33], 42, 3, 9,
            new ValueHash256(Enumerable.Repeat((byte)0x44, 32).ToArray()), descriptor, null, body, out shards);
    }

    [Test]
    public void Code_manifest_and_channel_match_the_eip_vectors()
    {
        LeanBroadcastManifest manifest = Fixture(Body, out byte[][] shards);
        ValueHash256 id = manifest.Id(1, Genesis);
        using (Assert.EnterMultipleScope())
        {
            for (int row = 0; row < LeanReedSolomon.DataShards; row++)
                for (int column = 0; column < LeanReedSolomon.DataShards; column++)
                    Assert.That(LeanReedSolomon.Generator[row, column], Is.EqualTo(row == column ? 1 : 0), "systematic rows");
            Assert.That(Row(16), Is.EqualTo("21b5f685df02b7873edd4aa48dda6130"));
            Assert.That(Row(31), Is.EqualTo("3061da8da44add3e87b702df85f6b521"));
            Assert.That(shards.SelectMany(s => s).ToArray().ToHexString(),
                Is.EqualTo("3132333435363738393031323334353637000000000000000000000000000000"
                    + "4bd4aac40d61cd1a263e9124244a226b2c9a469b7059d630312ef292f501538d"));
            Assert.That(SHA256.HashData(Body).ToHexString(), Is.EqualTo("97f40b8ae3e4d3118bb4afb623d3e768f04d9bc2f913bd64296ebcd53386c1ac"));
            Assert.That(id.ToString(false), Is.EqualTo("e6f86470166cc46f3acdc1d0efd7f1b594e2209b0c917fb1becc4058be46fdab"));
            Assert.That(LeanBroadcastManifest.MessageId(id), Is.EqualTo("e6f86470166cc46f3acdc1d0efd7f1b594e2209b0c917fb1becc4058be46fdab"));
            Assert.That(manifest.Channel(1, Genesis), Is.EqualTo("lean/1/rs/6305cd9070a21d6be6be82a0e4ef98f2019641ab7f65de36d6a5de8889781744"));
            Assert.That(manifest.Preamble([], []).Length, Is.LessThanOrEqualTo(LeanBroadcastManifest.MaxPreambleBytes));
        }

        static string Row(int row) =>
            Enumerable.Range(0, LeanReedSolomon.DataShards).Select(c => LeanReedSolomon.Generator[row, c]).ToArray().ToHexString();
    }

    private static IEnumerable<int[]> ShardSets()
    {
        yield return [.. Enumerable.Range(0, 16)];
        yield return [.. Enumerable.Range(16, 16)];
        yield return [.. Enumerable.Range(0, 16).Select(i => 2 * i)];
        yield return [.. Enumerable.Range(16, 16).Reverse()];
    }

    [Test]
    public void Any_sixteen_distinct_shards_rebuild_the_body(
        [Values(1, 16, 17, 127, 753)] int size, [ValueSource(nameof(ShardSets))] int[] indices)
    {
        byte[] body = size == 753 ? [.. Enumerable.Repeat(Enumerable.Range(0, 251).Select(i => (byte)i), 3).SelectMany(b => b)]
            : size == 1 ? "x"u8.ToArray() : [.. Enumerable.Range(0, size).Select(i => (byte)i)];
        LeanBroadcastManifest manifest = Fixture(body, out byte[][] shards);
        byte[]? rebuilt = manifest.Reconstruct(indices, [.. indices.Select(i => shards[i])], out byte[][]? codeword, out string? error);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rebuilt, Is.EqualTo(body), error);
            Assert.That(codeword, Is.EqualTo(shards), "the re-encoded codeword");
        }
    }

    private static byte[] ManifestWith(Func<byte[][], byte[][]> coding, Func<byte[], byte[]>? descriptor = null)
    {
        LeanBroadcastManifest manifest = Fixture(Body, out byte[][] shards);
        byte[][] fields =
        [
            LeanRlp.EncodeUInt(16), LeanRlp.EncodeUInt(16), LeanRlp.EncodeUInt((ulong)manifest.ShardBytes), LeanRlp.EncodeBytes(manifest.BodySha256.Bytes),
            LeanRlp.EncodeList([.. manifest.ShardHashes.Select(h => LeanRlp.EncodeBytes(h.Bytes))])
        ];
        return LeanRlp.EncodeList(LeanRlp.EncodeBytes(manifest.BroadcastProfileId.Bytes), LeanRlp.EncodeBytes(manifest.ForkDigest), LeanRlp.EncodeUInt(42),
            LeanRlp.EncodeUInt(3), LeanRlp.EncodeUInt(9), LeanRlp.EncodeBytes(manifest.ConsensusContextRoot.Bytes),
            descriptor?.Invoke(manifest.Descriptor.Encoded) ?? manifest.Descriptor.Encoded, LeanRlp.EncodeBytes([]), LeanRlp.EncodeList(coding(fields)));
    }

    private static IEnumerable<TestCaseData> BadGeometry()
    {
        yield return new TestCaseData(ManifestWith(f => f)).Returns(true).SetName("Canonical fixture");
        yield return new TestCaseData(ManifestWith(f => [LeanRlp.EncodeUInt(15), .. f[1..]])).Returns(false).SetName("15 data shards");
        yield return new TestCaseData(ManifestWith(f => [f[0], LeanRlp.EncodeUInt(17), .. f[2..]])).Returns(false).SetName("17 parity shards");
        yield return new TestCaseData(ManifestWith(f => [.. f[..2], LeanRlp.EncodeUInt(0), .. f[3..]])).Returns(false).SetName("Zero shard width");
        yield return new TestCaseData(ManifestWith(f => [.. f[..2], LeanRlp.EncodeUInt(65537), .. f[3..]])).Returns(false).SetName("Shard width 65537");
        yield return new TestCaseData(ManifestWith(f => [.. f[..4], LeanRlp.EncodeList()])).Returns(false).SetName("No shard hashes");
        yield return new TestCaseData(ManifestWith(f => f, d => LeanDescriptor.Create(LeanProtocol.KindWrapper, PackageProfile,
            LeanDescriptor.WrapperContext(), Body, out _).Encoded)).Returns(false).SetName("Kind 1 is never broadcast");
    }

    [TestCaseSource(nameof(BadGeometry))]
    public bool Geometry_is_checked_before_any_coder_is_initialized(byte[] encoded)
    {
        try
        {
            LeanBroadcastManifest.Decode(encoded);
            return true;
        }
        catch (RlpException)
        {
            return false;
        }
    }

    [Test]
    public void Oversized_body_and_preamble_are_refused()
    {
        LeanBroadcastManifest manifest = Fixture(Body, out _);
        byte[] large = new byte[LeanReedSolomon.MaxBodyBytes + 1];
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<ArgumentException>(() => manifest.Preamble([], new byte[LeanBroadcastManifest.MaxPreambleBytes]));
            Assert.Throws<ArgumentException>(() => LeanReedSolomon.Encode(large), "bodies above 2**20 bytes use retrieval");
            Assert.Throws<RlpException>(() => LeanBroadcastManifest.DecodePreamble(new byte[LeanBroadcastManifest.MaxPreambleBytes + 1], out _, out _));
        }
    }

    [Test]
    public void Padding_body_hash_and_parity_commitments_are_checked_after_decoding()
    {
        LeanBroadcastManifest manifest = Fixture(Body, out byte[][] shards);
        int[] first = [.. Enumerable.Range(0, 16)];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(manifest.Reconstruct(first, [.. first.Select(i => shards[i])], out _, out _), Is.EqualTo(Body));
            Assert.That(manifest.IsCommittedShard(0, shards[1]), Is.False, "shard hash");
            Assert.That(manifest.IsCommittedShard(0, shards[0][..1]), Is.False, "shard width");

            // An authenticated but nonzero padding shard must fail on padding alone.
            byte[] badPadding = [.. shards[15][..^1], 0x01];
            Assert.That(Rebuild(Altered(manifest, hashes: h => { h[15] = new ValueHash256(SHA256.HashData(badPadding)); }), [.. shards[..15], badPadding]),
                Is.EqualTo("nonzero padding"));
            Assert.That(Rebuild(Altered(manifest, bodySha: default(ValueHash256)), shards[..16]), Is.EqualTo("body SHA-256 mismatch"));
            Assert.That(Rebuild(Altered(manifest, hashes: h => { h[31] = default; }), shards[..16]), Is.EqualTo("noncanonical parity commitment"));
            Assert.Throws<ArgumentException>(() => manifest.Reconstruct([.. first[..15], 0], [.. shards[..15], shards[0]], out _, out _), "repeated index");
        }

        static string? Rebuild(LeanBroadcastManifest manifest, byte[][] shards)
        {
            manifest.Reconstruct([.. Enumerable.Range(0, 16)], shards, out _, out string? error);
            return error;
        }
    }

    private static LeanBroadcastManifest Altered(LeanBroadcastManifest manifest, Action<ValueHash256[]>? hashes = null, ValueHash256? bodySha = null)
    {
        ValueHash256[] shardHashes = [.. manifest.ShardHashes];
        hashes?.Invoke(shardHashes);
        byte[] coding = LeanRlp.EncodeList(LeanRlp.EncodeUInt(16), LeanRlp.EncodeUInt(16), LeanRlp.EncodeUInt((ulong)manifest.ShardBytes),
            LeanRlp.EncodeBytes((bodySha ?? manifest.BodySha256).Bytes), LeanRlp.EncodeList([.. shardHashes.Select(h => LeanRlp.EncodeBytes(h.Bytes))]));
        return LeanBroadcastManifest.Decode(LeanRlp.EncodeList(LeanRlp.EncodeBytes(manifest.BroadcastProfileId.Bytes), LeanRlp.EncodeBytes(manifest.ForkDigest),
            LeanRlp.EncodeUInt(manifest.Slot), LeanRlp.EncodeUInt(manifest.DutyIndex), LeanRlp.EncodeUInt(manifest.ProducerIndex),
            LeanRlp.EncodeBytes(manifest.ConsensusContextRoot.Bytes), manifest.Descriptor.Encoded,
            manifest.Skeleton?.Encoded ?? LeanRlp.EncodeBytes([]), coding));
    }

    [Test]
    public void Manifest_id_binds_every_field_the_chain_and_the_genesis()
    {
        LeanBroadcastManifest manifest = Fixture(Body, out _);
        ValueHash256 original = manifest.Id(1, Genesis);
        LeanBroadcastManifest otherSlot = LeanBroadcastManifest.Create(manifest.BroadcastProfileId, manifest.ForkDigest, 43, 3, 9,
            manifest.ConsensusContextRoot, manifest.Descriptor, null, Body, out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(otherSlot.Id(1, Genesis), Is.Not.EqualTo(original));
            Assert.That(manifest.Id(2, Genesis), Is.Not.EqualTo(original));
            Assert.That(manifest.Id(1, default), Is.Not.EqualTo(original));
            Assert.That(ValueKeccak.Compute(LeanRlp.EncodeList(LeanRlp.EncodeUInt(1), LeanRlp.EncodeBytes(Genesis.Bytes), manifest.Encoded)),
                Is.Not.EqualTo(original), "domain separated");
        }
    }

    [Test]
    public void Shard_ids_and_bitmaps_use_their_canonical_encodings()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(LeanBroadcastWire.ShardId(0), Is.Empty);
            Assert.That(LeanBroadcastWire.ShardId(1).ToHexString(), Is.EqualTo("0801"));
            Assert.That(LeanBroadcastWire.ShardId(31).ToHexString(), Is.EqualTo("081f"));
            for (int i = 0; i < LeanReedSolomon.TotalShards; i++)
                Assert.That(LeanBroadcastWire.TryParseShardId(LeanBroadcastWire.ShardId(i), out int index) && index == i, Is.True);
            foreach (string bad in (string[])["0800", "0820", "088100", "08010801", "1001", "08"])
                Assert.That(LeanBroadcastWire.TryParseShardId(Bytes.FromHexString(bad), out _), Is.False, bad);
            Assert.That(LeanBroadcastWire.Bitmap(1u | 1u << 7 | 1u << 8 | 1u << 31).ToHexString(), Is.EqualTo("81010080"));
            Assert.That(LeanBroadcastWire.TryParseBitmap(Bytes.FromHexString("81010080"), out uint bits) && bits == (1u | 1u << 7 | 1u << 8 | 1u << 31), Is.True);
            foreach (int size in (int[])[0, 3, 5]) Assert.That(LeanBroadcastWire.TryParseBitmap(new byte[size], out _), Is.False);
        }
    }

    [Test]
    public void Framework_frames_use_the_broadcast_protobuf_schema()
    {
        using (Assert.EnterMultipleScope())
        {
            // Bcast { peer_handshake = Handshake { version = 1 } }
            Assert.That(LeanBroadcastWire.Handshake().ToHexString(), Is.EqualTo("0a020801"));
            Assert.That(LeanBroadcastWire.Envelope(LeanBroadcastWire.Handshake()).ToHexString(), Is.EqualTo("000000040a020801"));
            Assert.That(LeanBroadcastWire.DecodeBcast(LeanBroadcastWire.Subscribe("c")), Is.EqualTo(new LeanBcastSubscribe("c")));
            Assert.That(LeanBroadcastWire.Subscribe("c").ToHexString(), Is.EqualTo("12030a0163"));
            Assert.That(LeanBroadcastWire.DecodeBcast(LeanBroadcastWire.Unsubscribe("c")), Is.EqualTo(new LeanBcastUnsubscribe("c")));
            LeanSessOpen open = (LeanSessOpen)LeanBroadcastWire.DecodeSess(LeanBroadcastWire.SessOpen("c", "m", [1, 2], [3, 0, 0, 0]));
            Assert.That((open.Channel, open.MessageId, open.Preamble, open.InitialUpdate), Is.EqualTo(("c", "m", new byte[] { 1, 2 }, new byte[] { 3, 0, 0, 0 })));
            Assert.That(((LeanSessUpdate)LeanBroadcastWire.DecodeSess(LeanBroadcastWire.SessUpdate([7, 0, 0, 0]))).Data, Is.EqualTo(new byte[] { 7, 0, 0, 0 }));
            Assert.That(LeanBroadcastWire.DecodeChunkHeader(LeanBroadcastWire.ChunkHeader("c", "m", [0x08, 0x05], 300)),
                Is.EqualTo(new LeanChunkHeader("c", "m", [0x08, 0x05], 300)).Using<LeanChunkHeader>((a, b) =>
                    a.Channel == b.Channel && a.MessageId == b.MessageId && a.ChunkId.SequenceEqual(b.ChunkId) && a.DataLength == b.DataLength));
            Assert.Throws<LeanBroadcastFormatException>(() => LeanBroadcastWire.DecodeBcast([]), "no oneof member");
            Assert.Throws<LeanBroadcastFormatException>(() => LeanBroadcastWire.DecodeBcast([0x12, 0x05, 0x0a]), "truncated");
            Assert.Throws<LeanBroadcastFormatException>(() => LeanBroadcastWire.DecodeBcast([0x12, 0x03, 0x0a, 0x01, 0xff]), "not UTF-8");
        }
    }

    /// <summary>A node with a transport and a broadcast engine, linked to others through ordered in-process deliveries.</summary>
    private sealed class BroadcastNode : IDisposable
    {
        public BroadcastNode(TestBroadcastProfile? profile = null)
        {
            Node = new LeanTestNode(manualTime: false);
            Profile = profile ?? new TestBroadcastProfile();
            Engine = new LeanBroadcastEngine(Node.Transport, Profile, LimboLogs.Instance);
        }

        public LeanTestNode Node { get; }
        public TestBroadcastProfile Profile { get; }
        public LeanBroadcastEngine Engine { get; }

        public void Dispose()
        {
            Engine.Dispose();
            Node.Dispose();
        }
    }

    /// <summary>One side of an in-process link: what this node's engine sends is delivered to the remote engine in order.</summary>
    private sealed class LinkPeer : ILeanBroadcastPeer
    {
        private readonly Channel<Func<Task>> _deliveries = Channel.CreateUnbounded<Func<Task>>();

        public LinkPeer(string name, BroadcastNode remote)
        {
            Description = name;
            Remote = remote;
            _ = Task.Run(async () =>
            {
                await foreach (Func<Task> delivery in _deliveries.Reader.ReadAllAsync()) await delivery();
            });
        }

        public string Description { get; }
        public ulong MaxObjectBytes => LeanLimits.MaxObjectBytes;
        public BroadcastNode Remote { get; }
        public LinkPeer Reverse { get; set; } = null!;
        public ConcurrentQueue<string> Penalties { get; } = new();
        public ConcurrentQueue<int> Delivered { get; } = new();
        public int ClosedSessions => Volatile.Read(ref _closedSessions);
        private int _closedSessions;
        public Func<int, byte[], byte[]> Tamper { get; set; } = static (_, shard) => shard;

        public bool IsSubscribed(string channel) => Remote.Engine.Channels.Contains(channel);

        public ILeanBroadcastSession OpenSession(string channel, string messageId, byte[] preamble, byte[] initialUpdate)
        {
            LinkSession session = new(this, channel, messageId);
            Deliver(() =>
            {
                if (Remote.Engine.OnSessionOpen(Reverse, new LeanSessOpen(channel, messageId, preamble, initialUpdate), out string? reason)
                    == LeanBroadcastVerdict.Invalid) Reverse.Penalties.Enqueue(reason!);
            });
            return session;
        }

        public async ValueTask<bool> SendShardAsync(ILeanBroadcastSession session, int index, ReadOnlyMemory<byte> shard)
        {
            TaskCompletionSource<bool> sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] data = Tamper(index, shard.ToArray());
            Deliver(() =>
            {
                LeanChunkHeader header = new(session.Channel, session.MessageId, LeanBroadcastWire.ShardId(index), (uint)data.Length);
                LeanBroadcastVerdict verdict = Remote.Engine.ResolveShard(header, out int resolved, out _, out string? reason);
                if (verdict == LeanBroadcastVerdict.Accepted) verdict = Remote.Engine.OnShard(Reverse, session.MessageId, resolved, data);
                if (verdict == LeanBroadcastVerdict.Invalid) Reverse.Penalties.Enqueue(reason ?? $"shard {index}");
                if (verdict == LeanBroadcastVerdict.Accepted) Delivered.Enqueue(index);
                sent.TrySetResult(verdict != LeanBroadcastVerdict.Invalid);
            });
            return await sent.Task;
        }

        public void Penalize(string reason) => Penalties.Enqueue(reason);

        public void Deliver(Action delivery) => _deliveries.Writer.TryWrite(() =>
        {
            delivery();
            return Task.CompletedTask;
        });

        private sealed class LinkSession(LinkPeer peer, string channel, string messageId) : ILeanBroadcastSession
        {
            public string Channel { get; } = channel;
            public string MessageId { get; } = messageId;
            public void Update(byte[] bitmap) => peer.Deliver(() => peer.Remote.Engine.OnRoutingUpdate(peer.Reverse, MessageId, bitmap));
            public void Complete() => peer.Deliver(() => peer.Remote.Engine.OnPeerSessionEnded(peer.Reverse, MessageId, reconstructed: true));
            public void Close()
            {
                Interlocked.Increment(ref peer._closedSessions);
                peer.Deliver(() => peer.Remote.Engine.OnPeerSessionEnded(peer.Reverse, MessageId, reconstructed: false));
            }
        }
    }

    private static (LinkPeer AtoB, LinkPeer BtoA) Link(BroadcastNode a, BroadcastNode b)
    {
        LinkPeer toB = new($"{a.GetHashCode()}->b", b), toA = new($"{b.GetHashCode()}->a", a);
        toB.Reverse = toA;
        toA.Reverse = toB;
        a.Engine.AddPeer(toB);
        b.Engine.AddPeer(toA);
        return (toB, toA);
    }

    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(Timeout);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    /// <summary>A validated package held by <paramref name="origin"/>, and its signed manifest for the current slot.</summary>
    private static async Task<(LeanBroadcastManifest Manifest, LeanDescriptor Descriptor)> Package(BroadcastNode origin, int seed, int proofBytes = 40_000,
        ulong? slot = null, ulong duty = 0)
    {
        Transaction transaction = FrameTransaction(seed);
        byte[] package = InclusionList(proofBytes, transaction);
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindInclusionList, LeanObjectTransport.LocalProfile,
            LeanDescriptor.InclusionListContext(ValueKeccak.Compute(package)), package, out _);
        Assert.That((await origin.Node.Wrappers.AcceptInclusionListDetailedAsync(package)).HasValidProof, Is.True);
        await Until(() => origin.Node.Transport.IsStored(descriptor.ObjectId));
        LeanBroadcastManifest manifest = LeanBroadcastManifest.Create(TestBroadcastProfile.ProfileId, [1, 2, 3, 4], slot ?? origin.Profile.CurrentSlot,
            duty, 7, default, descriptor, null, package, out _);
        return (manifest, descriptor);
    }

    [Test]
    public async Task Package_relays_through_a_peer_before_it_reconstructs_and_is_validated_by_each_receiver()
    {
        using BroadcastNode origin = new(), relay = new(), receiver = new();
        (LinkPeer originToRelay, LinkPeer relayToOrigin) = Link(origin, relay);
        (LinkPeer relayToReceiver, LinkPeer receiverToRelay) = Link(relay, receiver);
        (LeanBroadcastManifest manifest, LeanDescriptor descriptor) = await Package(origin, 1);

        Assert.That(origin.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);

        await Until(() => receiver.Node.Transport.IsStored(descriptor.ObjectId) && relay.Node.Transport.IsStored(descriptor.ObjectId));
        // Once both reconstructed, each finishes its SESS stream toward the other, returning its QUIC stream allowance.
        await Until(() => originToRelay.ClosedSessions == 1 && relayToReceiver.ClosedSessions == 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Node.Transport.IncompleteBytes + receiver.Node.Transport.IncompleteBytes, Is.Zero,
                "a reconstructed session's codeword is no longer charged as an incomplete assembly");
            Assert.That(relayToReceiver.Delivered, Is.Not.Empty, "the relay forwarded checked shards");
            Assert.That(originToRelay.Delivered.Distinct().Count(), Is.GreaterThanOrEqualTo(LeanReedSolomon.DataShards));
            Assert.That(new[] { originToRelay, relayToOrigin, relayToReceiver, receiverToRelay }.SelectMany(p => p.Penalties), Is.Empty);
        }
    }

    [Test]
    public async Task Wrong_shard_is_the_suppliers_fault_and_valid_shards_still_complete_the_object()
    {
        using BroadcastNode origin = new(), receiver = new(), other = new();
        (LinkPeer bad, _) = Link(origin, receiver);
        (LinkPeer good, _) = Link(other, receiver);
        (LeanBroadcastManifest manifest, LeanDescriptor descriptor) = await Package(origin, 2);
        await Package(other, 2);
        bad.Tamper = static (index, shard) => index == 3 ? [.. shard[..^1], (byte)(shard[^1] ^ 1)] : shard;

        Assert.That(origin.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);
        Assert.That(other.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);

        await Until(() => receiver.Node.Transport.IsStored(descriptor.ObjectId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bad.Reverse.Penalties, Has.Some.Contains("shard 3"));
            Assert.That(good.Reverse.Penalties, Is.Empty);
        }
    }

    private static IEnumerable<TestCaseData> Refusals()
    {
        yield return new TestCaseData("unsigned", true).SetName("Unauthorized producer penalizes the forwarder");
        yield return new TestCaseData("unresolved", false).SetName("Unresolved consensus context is deferred to retrieval");
        yield return new TestCaseData("stale slot", false).SetName("Slot outside the one-slot window");
        yield return new TestCaseData("conflict", false).SetName("Second manifest for one duty gets no allowance");
        yield return new TestCaseData("wrong id", true).SetName("message_id is not the manifest ID");
        yield return new TestCaseData("other channel", false).SetName("Unsubscribed channel");
    }

    [TestCaseSource(nameof(Refusals))]
    public async Task Session_open_is_checked_before_any_session_state(string scenario, bool penalized)
    {
        using BroadcastNode origin = new(), receiver = new();
        (LinkPeer toReceiver, _) = Link(origin, receiver);
        (LeanBroadcastManifest manifest, _) = await Package(origin, 3, slot: scenario == "stale slot" ? 5 : null);
        receiver.Profile.Unresolved = scenario == "unresolved";
        string channel = manifest.Channel(1, LeanTestNode.Genesis.ValueHash256);
        if (scenario == "conflict")
        {
            (LeanBroadcastManifest first, _) = await Package(origin, 4);
            Assert.That(Open(first, TestBroadcastProfile.Sign(first), channel), Is.EqualTo(LeanBroadcastVerdict.Accepted));
        }
        byte[] signature = scenario == "unsigned" ? [] : TestBroadcastProfile.Sign(manifest);
        string messageId = scenario == "wrong id" ? new string('0', 64) : LeanBroadcastManifest.MessageId(manifest.Id(1, LeanTestNode.Genesis.ValueHash256));

        LeanBroadcastVerdict verdict = receiver.Engine.OnSessionOpen(toReceiver.Reverse,
            new LeanSessOpen(scenario == "other channel" ? "lean/1/rs/other" : channel, messageId, manifest.Preamble(signature, []), []), out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.EqualTo(penalized ? LeanBroadcastVerdict.Invalid : LeanBroadcastVerdict.Refused));
            Assert.That(receiver.Engine.SessionCount, Is.EqualTo(scenario == "conflict" ? 1 : 0));
        }

        LeanBroadcastVerdict Open(LeanBroadcastManifest m, byte[] s, string c) => receiver.Engine.OnSessionOpen(toReceiver.Reverse,
            new LeanSessOpen(c, LeanBroadcastManifest.MessageId(m.Id(1, LeanTestNode.Genesis.ValueHash256)), m.Preamble(s, []), []), out _);
    }

    [Test]
    public async Task Object_failing_its_context_is_the_signers_fault_and_stays_retrievable()
    {
        using BroadcastNode origin = new(), receiver = new(new TestBroadcastProfile { ContextMismatch = true });
        (LinkPeer toReceiver, _) = Link(origin, receiver);
        (LeanBroadcastManifest manifest, LeanDescriptor descriptor) = await Package(origin, 5);

        Assert.That(origin.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);

        await Until(() => toReceiver.Delivered.Distinct().Count() >= LeanReedSolomon.DataShards);
        await Task.Delay(200);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiver.Node.Transport.IsStored(descriptor.ObjectId), Is.False);
            Assert.That(receiver.Node.Transport.IsTombstoned(descriptor.ObjectId), Is.False, "failure is scoped to the manifest");
            Assert.That(receiver.Node.Transport.AssemblyCount, Is.EqualTo(1), "the shared assembly remains for retrieval");
            Assert.That(toReceiver.Reverse.Penalties, Is.Empty, "relays are not penalized for a producer fault");
        }
    }

    [TestCase(true, TestName = "Manifest with a wrong shard hash")]
    [TestCase(false, TestName = "Manifest with a wrong body hash")]
    public async Task Holder_forwards_nothing_its_manifest_does_not_commit(bool wrongShardHash)
    {
        using BroadcastNode origin = new(), holder = new(), receiver = new();
        (LinkPeer originToHolder, LinkPeer holderToOrigin) = Link(origin, holder);
        (LinkPeer holderToReceiver, LinkPeer receiverToHolder) = Link(holder, receiver);
        (LeanBroadcastManifest committed, _) = await Package(holder, 7);
        LeanBroadcastManifest manifest = wrongShardHash
            ? Altered(committed, hashes: h => { h[0] = default; })
            : Altered(committed, bodySha: default(ValueHash256));

        LeanBroadcastVerdict verdict = holder.Engine.OnSessionOpen(holderToOrigin, new LeanSessOpen(manifest.Channel(1, LeanTestNode.Genesis.ValueHash256),
            LeanBroadcastManifest.MessageId(manifest.Id(1, LeanTestNode.Genesis.ValueHash256)), manifest.Preamble(TestBroadcastProfile.Sign(manifest), []), []),
            out string? reason);

        await Task.Delay(300);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.Not.EqualTo(LeanBroadcastVerdict.Invalid), reason);
            Assert.That(receiver.Engine.SessionCount + origin.Engine.SessionCount, Is.Zero, "the holder opened no session toward its peers");
            Assert.That(holderToReceiver.Delivered.Concat(holderToOrigin.Delivered), Is.Empty, "the holder forwarded no shard");
            Assert.That(new[] { originToHolder, holderToOrigin, holderToReceiver, receiverToHolder }.SelectMany(p => p.Penalties), Is.Empty,
                "a manifest fault penalizes no relay");
        }
    }

    [Test]
    public async Task Originating_requires_a_fully_validated_object()
    {
        using BroadcastNode origin = new();
        byte[] package = InclusionList(1000, FrameTransaction(6));
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindInclusionList, LeanObjectTransport.LocalProfile,
            LeanDescriptor.InclusionListContext(ValueKeccak.Compute(package)), package, out _);
        LeanBroadcastManifest manifest = LeanBroadcastManifest.Create(TestBroadcastProfile.ProfileId, [1, 2, 3, 4], 10, 0, 7, default, descriptor, null,
            package, out _);
        Assert.That(origin.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.False);
        await Task.CompletedTask;
    }

    [Test]
    public async Task First_valid_block_proof_sidecar_is_kept()
    {
        using LeanTestNode node = new(manualTime: false);
        BlockHeader header = LeanTransportTests.ProofHeader(Proof(2000));
        LeanHeaderSkeleton skeleton = LeanHeaderSkeleton.FromHeader(header, new HeaderDecoder());
        LeanDescriptor first = LeanTransportTests.SidecarDescriptor(header, skeleton, out byte[] body);
        // Rebuilds the same header, so it passes the body checks, but its context names another block number.
        LeanDescriptor second = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(header.Hash!.ValueHash256, header.Number + 1, header.TxRoot!.ValueHash256,
                header.RecursiveStark!.BlockDepsHash.ValueHash256, skeleton.Hash), body, out _);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.AcceptBroadcastBody(first, skeleton, body, Stopwatch.GetTimestamp()), Is.Null);
            Assert.That(node.Transport.AcceptBroadcastBody(second, skeleton, body, Stopwatch.GetTimestamp()), Is.Null);
        }

        BlockHeader withoutProof = header.Clone();
        withoutProof.RecursiveStark = null;
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(1));
        RecursiveStark? proof = await node.Transport.TryGetAsync(new Block(withoutProof, new BlockBody()), deadline.Token);
        Assert.That(proof?.StarkProof, Is.EqualTo(header.RecursiveStark.StarkProof), "the later sidecar did not replace the first");
    }
}
