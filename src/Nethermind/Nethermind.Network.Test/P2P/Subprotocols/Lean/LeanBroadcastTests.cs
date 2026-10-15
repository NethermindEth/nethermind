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
using Nethermind.Blockchain;
using NSubstitute;
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
    public bool ContextThrows { get; set; }
    public Action? BeforeContext { get; set; }
    public int AuthorizationChecks { get; private set; }

    public DateTimeOffset SlotStart(ulong slot) => _genesis + TimeSpan.FromSeconds(12.0 * ((double)slot - CurrentSlot + 1));

    public LeanBroadcastAuthorization Authorize(LeanBroadcastManifest manifest, in ValueHash256 manifestId, ReadOnlySpan<byte> signature,
        ReadOnlySpan<byte> authorization)
    {
        AuthorizationChecks++;
        return Unresolved ? LeanBroadcastAuthorization.Unresolved
            : signature.SequenceEqual(manifestId.Bytes) ? LeanBroadcastAuthorization.Authorized
            : LeanBroadcastAuthorization.Rejected;
    }

    public bool MatchesContext(LeanBroadcastManifest manifest, ReadOnlySpan<byte> body)
    {
        BeforeContext?.Invoke();
        return ContextThrows ? throw new InvalidOperationException("context lookup failed") : !ContextMismatch;
    }

    public static byte[] Sign(LeanBroadcastManifest manifest) => manifest.Id(1, LeanTestNode.Genesis.ValueHash256).ToByteArray();
}

/// <summary>
/// EIP-8437 ethp2p broadcast: the fixed Reed-Solomon code, manifests and shard encodings ported from
/// <c>assets/eip-8437/check_broadcast.py</c>, framework wire encodings, and the session engine between in-process peers.
/// </summary>
public class LeanBroadcastTests
{
    private static readonly ConcurrentDictionary<Hash256, Block> KnownBlocks = new();

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly byte[] Body = "12345678901234567"u8.ToArray();
    private static readonly ValueHash256 Genesis = new(Bytes.FromHexString("5555555555555555555555555555555555555555555555555555555555555555"));
    private static readonly ValueHash256 ProofProfile = new(Bytes.FromHexString("2222222222222222222222222222222222222222222222222222222222222222"));

    private static LeanBroadcastManifest Fixture(byte[] body, out byte[][] shards)
    {
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, ProofProfile,
            LeanDescriptor.BlockProofContext(new ValueHash256(Enumerable.Repeat((byte)0x66, 32).ToArray())), body);
        return LeanBroadcastManifest.Create(new ValueHash256(Enumerable.Repeat((byte)0x11, 32).ToArray()), [0x33, 0x33, 0x33, 0x33], 42, 0, 9,
            new ValueHash256(Enumerable.Repeat((byte)0x44, 32).ToArray()), descriptor, body, out shards);
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
            Assert.That(id.ToString(false), Is.EqualTo("ef5e6a8395ac1f23d6e6179b0454f7ac6131d911c96dfb23990b836a02c2c6a7"));
            Assert.That(LeanBroadcastManifest.MessageId(id), Is.EqualTo("ef5e6a8395ac1f23d6e6179b0454f7ac6131d911c96dfb23990b836a02c2c6a7"));
            Assert.That(manifest.Channel(1, Genesis), Is.EqualTo("lean/1/rs/c6f07ea02f8a1d325716da3fb6befadb26c836dc702d9ec82004ba6968af101c"));
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
            LeanRlp.EncodeUInt(0), LeanRlp.EncodeUInt(9), LeanRlp.EncodeBytes(manifest.ConsensusContextRoot.Bytes),
            descriptor?.Invoke(manifest.Descriptor.Encoded) ?? manifest.Descriptor.Encoded, LeanRlp.EncodeList(coding(fields)));
    }

    private static IEnumerable<TestCaseData> BadGeometry()
    {
        yield return new TestCaseData(ManifestWith(f => f)).Returns(true).SetName("Canonical fixture");
        yield return new TestCaseData(ManifestWith(f => [LeanRlp.EncodeUInt(15), .. f[1..]])).Returns(false).SetName("15 data shards");
        yield return new TestCaseData(ManifestWith(f => [f[0], LeanRlp.EncodeUInt(17), .. f[2..]])).Returns(false).SetName("17 parity shards");
        yield return new TestCaseData(ManifestWith(f => [.. f[..2], LeanRlp.EncodeUInt(0), .. f[3..]])).Returns(false).SetName("Zero shard width");
        yield return new TestCaseData(ManifestWith(f => [.. f[..2], LeanRlp.EncodeUInt(65537), .. f[3..]])).Returns(false).SetName("Shard width 65537");
        yield return new TestCaseData(ManifestWith(f => [.. f[..4], LeanRlp.EncodeList()])).Returns(false).SetName("No shard hashes");
        yield return new TestCaseData(ManifestWith(f => f, d => LeanDescriptor.Create(LeanProtocol.KindWrapper, ProofProfile,
            LeanDescriptor.WrapperContext(), Body).Encoded)).Returns(false).SetName("Kind 1 is never broadcast");
        yield return new TestCaseData(ManifestWith(f => f, d => LeanRlp.EncodeList(LeanRlp.EncodeUInt(3), LeanRlp.EncodeBytes(ProofProfile.Bytes),
            LeanRlp.EncodeList(), LeanRlp.EncodeUInt((ulong)Body.Length), LeanRlp.EncodeBytes(ValueKeccak.Compute(Body).Bytes))))
            .Returns(false).SetName("Kind 3 has no transport object");
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
    public void Block_manifest_rejects_nonzero_duty_and_obsolete_auxiliary_field([Values] bool auxiliary)
    {
        LeanBroadcastManifest manifest = Fixture(Body, out _);
        LeanRlpReader outer = new(manifest.Encoded);
        LeanRlpReader reader = outer.ReadList();
        byte[][] fields = new byte[8][];
        for (int i = 0; i < fields.Length; i++) fields[i] = reader.ReadItem().ToArray();
        byte[] malformed;
        if (auxiliary) malformed = LeanRlp.EncodeList([.. fields[..7], LeanRlp.EncodeBytes([]), fields[7]]);
        else
        {
            fields[3] = LeanRlp.EncodeUInt(1);
            malformed = LeanRlp.EncodeList(fields);
        }
        Assert.That(() => LeanBroadcastManifest.Decode(malformed), Throws.InstanceOf<RlpException>());
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
            LeanRlp.EncodeBytes(manifest.ConsensusContextRoot.Bytes), manifest.Descriptor.Encoded, coding));
    }

    [Test]
    public void Manifest_id_binds_every_field_the_chain_and_the_genesis()
    {
        LeanBroadcastManifest manifest = Fixture(Body, out _);
        ValueHash256 original = manifest.Id(1, Genesis);
        LeanBroadcastManifest otherSlot = LeanBroadcastManifest.Create(manifest.BroadcastProfileId, manifest.ForkDigest, 43, 0, 9,
            manifest.ConsensusContextRoot, manifest.Descriptor, Body, out _);
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
            Node.Tree.FindHeader(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
                .Returns(c => KnownBlocks.TryGetValue(c.Arg<Hash256>(), out Block? b) ? b.Header : null);
            Node.Tree.FindBlock(Arg.Any<Hash256>(), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>())
                .Returns(c => KnownBlocks.TryGetValue(c.Arg<Hash256>(), out Block? b) ? b : null);
            Node.Tree.IsMainChain(Arg.Any<BlockHeader>()).Returns(true);
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

        public LeanPeer? RetrievalPeer { get; set; }
        public string Description { get; }
        public ulong MaxObjectBytes => LeanLimits.MaxObjectBytes;
        public bool IsClosed { get; set; }
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

    /// <summary>A subscribed connection that holds every shard send until released, to observe the sender's CHUNK stream window.</summary>
    private sealed class HeldPeer : ILeanBroadcastPeer
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _live, _maxLive, _sent, _closed;

        public bool Fail { get; init; }
        public bool ThrowOnSend { get; init; }
        public int Closed => Volatile.Read(ref _closed);
        public LeanPeer? RetrievalPeer => null;
        public string Description => "held";
        public ulong MaxObjectBytes => LeanLimits.MaxObjectBytes;
        public bool IsClosed => false;
        public int Live => Volatile.Read(ref _live);
        public int MaxLive => Volatile.Read(ref _maxLive);
        public int Sent => Volatile.Read(ref _sent);

        public bool IsSubscribed(string channel) => true;

        public ILeanBroadcastSession OpenSession(string channel, string messageId, byte[] preamble, byte[] initialUpdate) =>
            new HeldSession(this, channel, messageId);

        public async ValueTask<bool> SendShardAsync(ILeanBroadcastSession session, int index, ReadOnlyMemory<byte> shard)
        {
            int live = Interlocked.Increment(ref _live);
            for (int max = Volatile.Read(ref _maxLive); max < live; max = Volatile.Read(ref _maxLive))
                if (Interlocked.CompareExchange(ref _maxLive, live, max) == max) break;
            await _release.Task;
            Interlocked.Decrement(ref _live);
            Interlocked.Increment(ref _sent);
            if (ThrowOnSend) throw new InvalidOperationException("shard stream closed");
            return !Fail;
        }

        public void Release() => _release.TrySetResult();

        public void Penalize(string reason) { }

        private sealed class HeldSession(HeldPeer owner, string channel, string messageId) : ILeanBroadcastSession
        {
            public string Channel { get; } = channel;
            public string MessageId { get; } = messageId;
            public void Update(byte[] bitmap) { }
            public void Complete() { }
            public void Close() => Interlocked.Increment(ref owner._closed);
        }
    }

    [Test]
    public async Task Synchronous_shard_write_failure_finishes_the_session_without_retrying([Values] bool throws)
    {
        using BroadcastNode origin = new();
        HeldPeer peer = new() { Fail = true, ThrowOnSend = throws };
        peer.Release();
        origin.Engine.AddPeer(peer);
        (LeanBroadcastManifest manifest, _) = await Package(origin, 917);

        Assert.That(origin.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(peer.Sent, Is.InRange(1, LeanBroadcastEngine.MaxShardsInFlightPerPeer));
            Assert.That(peer.Live, Is.Zero);
            Assert.That(peer.Closed, Is.EqualTo(1));
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

    /// <summary>A validated block proof and signed manifest for the current slot.</summary>
    private static async Task<(LeanBroadcastManifest Manifest, LeanDescriptor Descriptor)> Package(BroadcastNode origin, int seed, int proofBytes = 40_000,
        ulong? slot = null)
    {
        byte[] proof = Proof(proofBytes);
        BlockHeader header = LeanTransportTests.ProofHeader(proof, number: (ulong)seed);
        Block block = new(header, new BlockBody()) { StarkProof = proof };
        KnownBlocks[header.Hash!] = block;
        byte[] body = LeanBodies.EncodeBlockProof(proof);
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(header.Hash!.ValueHash256), body);
        origin.Node.Tree.NewHeadBlock += Raise.EventWith(new BlockEventArgs(block));
        await Until(() => origin.Node.Transport.IsStored(descriptor.ObjectId));
        LeanBroadcastManifest manifest = LeanBroadcastManifest.Create(TestBroadcastProfile.ProfileId, [1, 2, 3, 4], slot ?? origin.Profile.CurrentSlot,
            0, 7, default, descriptor, body, out _);
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

        await Until(() => receiver.Node.Transport.HasBroadcastSidecar(descriptor.BlockHash) && relay.Node.Transport.HasBroadcastSidecar(descriptor.BlockHash));
        // Once both reconstructed, each finishes its SESS stream toward the other, returning its QUIC stream allowance.
        await Until(() => originToRelay.ClosedSessions == 1 && relayToReceiver.ClosedSessions == 1);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(relay.Node.Transport.IncompleteBytes + receiver.Node.Transport.IncompleteBytes, Is.GreaterThan(0),
                "unvalidated block proofs remain charged until import or expiry");
            Assert.That(relayToReceiver.Delivered, Is.Not.Empty, "the relay forwarded checked shards");
            Assert.That(originToRelay.Delivered.Distinct().Count(), Is.GreaterThanOrEqualTo(LeanReedSolomon.DataShards));
            Assert.That(new[] { originToRelay, relayToOrigin, relayToReceiver, receiverToRelay }.SelectMany(p => p.Penalties), Is.Empty);
        }
    }

    [Test]
    public async Task Shards_in_flight_to_a_connection_are_capped_across_sessions_and_resume_as_streams_free()
    {
        using BroadcastNode origin = new();
        HeldPeer peer = new();
        origin.Engine.AddPeer(peer);
        (LeanBroadcastManifest first, _) = await Package(origin, 5);
        (LeanBroadcastManifest second, _) = await Package(origin, 6, slot: 11);

        Assert.That(origin.Engine.Originate(first, TestBroadcastProfile.Sign(first), []), Is.True);
        Assert.That(origin.Engine.Originate(second, TestBroadcastProfile.Sign(second), []), Is.True);
        await Until(() => peer.Live == LeanBroadcastEngine.MaxShardsInFlightPerPeer);
        peer.Release();

        // Streams freed by one session go to the other, so both codewords reach the peer.
        await Until(() => peer.Sent == 2 * LeanReedSolomon.TotalShards);
        Assert.That(peer.MaxLive, Is.EqualTo(LeanBroadcastEngine.MaxShardsInFlightPerPeer));
    }

    [Test]
    public async Task Retrieval_waits_about_a_round_trip_while_broadcast_shards_arrive()
    {
        using LeanTestNode node = new();
        FakeLink link = new();
        LeanPeer peer = node.Connect(link);
        LeanDescriptor descriptor = BroadcastPackage(node, 7);
        TimeSpan wait = node.Transport.BroadcastWait;
        node.Transport.OnBroadcastProgress(descriptor);
        using CancellationTokenSource stop = new();
        Task<LeanFetchedBody?> fetch = node.Transport.FetchBodyAsync(descriptor, peer, stop.Token);

        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        TimeSpan? wake = node.Time!.TimerDueTime;
        node.Tick(wait / 2);
        node.Transport.OnBroadcastProgress(descriptor);
        node.Tick(wait / 2);
        GetChunksMessage[] deferred = link.Sent<GetChunksMessage>();
        // No new shard for a round trip: the object is retrieved within its unchanged deadlines.
        node.Tick(wait);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(deferred, Is.Empty, "chunks are not requested while shards keep arriving");
            Assert.That(wake, Is.EqualTo(wait), "the timer fires when the deferral ends rather than at the next tick");
            Assert.That(link.Sent<GetChunksMessage>(), Is.Not.Empty);
        }
        stop.Cancel();
        using LeanFetchedBody? fetched = await fetch;
    }

    [Test]
    public async Task Shards_trickling_in_defer_retrieval_only_a_few_round_trips_after_the_manifest()
    {
        using LeanTestNode node = new();
        FakeLink link = new();
        LeanPeer peer = node.Connect(link);
        LeanDescriptor descriptor = BroadcastPackage(node, 8);
        TimeSpan wait = node.Transport.BroadcastWait;
        node.Transport.OnBroadcastProgress(descriptor);
        using CancellationTokenSource stop = new();
        Task<LeanFetchedBody?> fetch = node.Transport.FetchBodyAsync(descriptor, peer, stop.Token);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));

        int ticks = 0;
        while (link.Sent<GetChunksMessage>().Length == 0 && ticks++ < 100)
        {
            node.Tick(wait / 2);
            node.Transport.OnBroadcastProgress(descriptor);
        }
        Assert.That(ticks * (wait / 2), Is.GreaterThanOrEqualTo(3 * wait).And.LessThanOrEqualTo(3 * wait + wait / 2),
            "retrieval waits while shards arrive, but no longer than three round trips after the manifest");
        stop.Cancel();
        using LeanFetchedBody? fetched = await fetch;
    }

    [Test]
    public void A_slow_peer_does_not_lengthen_the_broadcast_wait()
    {
        using LeanTestNode node = new();
        LeanPeer fast = node.Connect(new FakeLink("fast")), slow = node.Connect(new FakeLink("slow"));
        node.Transport.SampleRoundTrip(fast, TimeSpan.FromMilliseconds(100).Ticks * node.Time!.TimestampFrequency / TimeSpan.TicksPerSecond);
        node.Transport.SampleRoundTrip(slow, 10 * node.Time.TimestampFrequency);
        Assert.That(node.Transport.BroadcastWait, Is.EqualTo(TimeSpan.FromMilliseconds(100)));
    }

    [Test]
    public void Round_trip_is_measured_on_the_transport_clock_from_sending_to_the_first_chunk()
    {
        using LeanTestNode node = new();
        FakeLink link = new();
        LeanPeer peer = node.Connect(link);
        byte[] body = Wrapper(3 * LeanProtocol.ChunkBytes, false, FrameTransaction(9));
        LeanDescriptor descriptor = Describe(body);
        node.Transport.OnAnnounce(peer, new AnnounceObjectsMessage([descriptor]));
        GetChunksMessage request = link.Sent<GetChunksMessage>().Single();

        node.Time!.Advance(TimeSpan.FromMilliseconds(300));
        node.Transport.OnChunk(peer, LeanChunkView.Parse(Wire(Chunk(request.RequestId, descriptor, body, request.Indices[0]))));
        Assert.That(node.Transport.BroadcastWait, Is.EqualTo(TimeSpan.FromMilliseconds(300)));
    }

    /// <summary>A known block-proof descriptor whose broadcast starts its shared assembly.</summary>
    private static LeanDescriptor BroadcastPackage(LeanTestNode node, int seed)
    {
        byte[] proof = Proof(3 * LeanProtocol.ChunkBytes);
        BlockHeader header = LeanTransportTests.ProofHeader(proof, number: (ulong)seed);
        node.Tree.FindHeader(header.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(header);
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(header.Hash!.ValueHash256), LeanBodies.EncodeBlockProof(proof));
        Assert.That(node.Transport.AdmitBroadcast(descriptor, 0), Is.EqualTo(LeanBroadcastAdmission.Started));
        return descriptor;
    }

    [Test]
    public async Task Wrong_shard_is_the_suppliers_fault_and_valid_shards_still_complete_the_object()
    {
        using BroadcastNode origin = new(), receiver = new(), other = new();
        (LinkPeer bad, _) = Link(origin, receiver);
        (LinkPeer good, _) = Link(other, receiver);
        (LeanBroadcastManifest manifest, LeanDescriptor descriptor) = await Package(origin, 2);
        await Package(other, 2);
        bad.Tamper = static (_, shard) => [.. shard[..^1], (byte)(shard[^1] ^ 1)];

        Assert.That(origin.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);
        await Until(() => !bad.Reverse.Penalties.IsEmpty);
        Assert.That(other.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);

        await Until(() => receiver.Node.Transport.HasBroadcastSidecar(descriptor.BlockHash));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(bad.Reverse.Penalties, Has.Some.Contains("shard"));
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
    public async Task Repeated_bad_signatures_do_not_create_unbounded_authorization_work()
    {
        using BroadcastNode origin = new(), receiver = new();
        (LinkPeer toReceiver, _) = Link(origin, receiver);
        (LeanBroadcastManifest manifest, _) = await Package(origin, 46);
        LeanSessOpen open = new(manifest.Channel(1, LeanTestNode.Genesis.ValueHash256),
            LeanBroadcastManifest.MessageId(manifest.Id(1, LeanTestNode.Genesis.ValueHash256)), manifest.Preamble([], []), []);
        for (int i = 0; i < LeanBroadcastEngine.MaxAuthorizationChecksPerSlot; i++)
            Assert.That(receiver.Engine.OnSessionOpen(toReceiver.Reverse, open, out _), Is.EqualTo(LeanBroadcastVerdict.Invalid));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(receiver.Engine.OnSessionOpen(toReceiver.Reverse, open, out _), Is.EqualTo(LeanBroadcastVerdict.Refused));
            Assert.That(receiver.Profile.AuthorizationChecks, Is.EqualTo(LeanBroadcastEngine.MaxAuthorizationChecksPerSlot));
            Assert.That(receiver.Engine.SessionCount, Is.Zero);
            Assert.That(receiver.Node.Transport.IncompleteBytes, Is.Zero);
        }
    }

    [Test]
    public async Task Broadcast_refuses_when_the_same_authenticated_sources_retrieval_budget_is_full()
    {
        using BroadcastNode origin = new(), receiver = new();
        (LinkPeer toReceiver, _) = Link(origin, receiver);
        LeanPeer source = receiver.Node.Connect(new FakeLink("retrieval"));
        toReceiver.Reverse.RetrievalPeer = source;
        source.ChargedBytes = LeanLimits.MaxPeerBytes;
        (LeanBroadcastManifest manifest, _) = await Package(origin, 44);
        LeanBroadcastVerdict verdict = receiver.Engine.OnSessionOpen(toReceiver.Reverse,
            new LeanSessOpen(manifest.Channel(1, LeanTestNode.Genesis.ValueHash256),
                LeanBroadcastManifest.MessageId(manifest.Id(1, LeanTestNode.Genesis.ValueHash256)),
                manifest.Preamble(TestBroadcastProfile.Sign(manifest), []), []), out _);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.EqualTo(LeanBroadcastVerdict.Refused));
            Assert.That(receiver.Engine.SessionCount, Is.Zero);
            Assert.That(source.ChargedBytes, Is.EqualTo(LeanLimits.MaxPeerBytes));
            Assert.That(toReceiver.Reverse.Penalties, Is.Empty);
        }
        source.ChargedBytes = 0;
    }

    [Test]
    public async Task Unknown_ordinary_header_refuses_broadcast_before_any_coded_state()
    {
        using BroadcastNode origin = new(), receiver = new();
        (LinkPeer toReceiver, _) = Link(origin, receiver);
        (LeanBroadcastManifest manifest, LeanDescriptor descriptor) = await Package(origin, 45);
        receiver.Node.Tree.FindHeader(new Hash256(descriptor.BlockHash), Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns((BlockHeader?)null);
        LeanBroadcastVerdict verdict = receiver.Engine.OnSessionOpen(toReceiver.Reverse,
            new LeanSessOpen(manifest.Channel(1, LeanTestNode.Genesis.ValueHash256),
                LeanBroadcastManifest.MessageId(manifest.Id(1, LeanTestNode.Genesis.ValueHash256)),
                manifest.Preamble(TestBroadcastProfile.Sign(manifest), []), []), out string? reason);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verdict, Is.EqualTo(LeanBroadcastVerdict.Refused));
            Assert.That(reason, Does.Contain("header"));
            Assert.That(receiver.Node.Transport.IncompleteBytes, Is.Zero);
            Assert.That(receiver.Engine.SessionCount, Is.Zero);
        }
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
            Assert.That(receiver.Engine.ProducerFaults(manifest.ProducerIndex), Is.EqualTo(1));
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
    public async Task Closed_peer_is_not_added_to_the_engine()
    {
        using BroadcastNode origin = new(), receiver = new();
        LinkPeer toReceiver = new("closed", receiver) { IsClosed = true };
        toReceiver.Reverse = new LinkPeer("origin", origin) { Reverse = toReceiver };
        origin.Engine.AddPeer(toReceiver);
        (LeanBroadcastManifest manifest, _) = await Package(origin, 8);

        Assert.That(origin.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);

        await Task.Delay(300);
        Assert.That(receiver.Engine.SessionCount, Is.Zero, "no session was opened toward the closed peer");
    }

    [Test]
    public async Task Reconstruction_that_throws_fails_its_session()
    {
        using BroadcastNode origin = new(), receiver = new(new TestBroadcastProfile { ContextThrows = true });
        (LinkPeer toReceiver, _) = Link(origin, receiver);
        (LeanBroadcastManifest manifest, _) = await Package(origin, 9);
        string channel = manifest.Channel(1, LeanTestNode.Genesis.ValueHash256);
        string messageId = LeanBroadcastManifest.MessageId(manifest.Id(1, LeanTestNode.Genesis.ValueHash256));

        Assert.That(origin.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);

        // A session left decoding would still accept its missing shards; a failed one needs none.
        await Until(() => Enumerable.Range(0, LeanReedSolomon.TotalShards).All(i =>
            receiver.Engine.ResolveShard(new LeanChunkHeader(channel, messageId, LeanBroadcastWire.ShardId(i), (uint)manifest.ShardBytes),
                out _, out bool unknown, out string? reason) == LeanBroadcastVerdict.Refused && !unknown && reason == "shard not needed"));
        Assert.That(toReceiver.Reverse.Penalties, Is.Empty);
    }

    [Test]
    public async Task Disposal_during_reconstruction_retains_work_credit_and_does_not_publish_late()
    {
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        TestBroadcastProfile profile = new()
        {
            BeforeContext = () =>
            {
                entered.TrySetResult();
                if (!release.Wait(Timeout)) throw new TimeoutException("context was not released");
            }
        };
        using BroadcastNode origin = new(), receiver = new(profile);
        (LinkPeer toReceiver, _) = Link(origin, receiver);
        (LeanBroadcastManifest manifest, LeanDescriptor descriptor) = await Package(origin, 915);
        try
        {
            Assert.That(origin.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.True);
            await entered.Task.WaitAsync(Timeout);
            receiver.Engine.Dispose();
            receiver.Node.Transport.Dispose();
            Assert.That(receiver.Node.Transport.IncompleteBytes, Is.GreaterThan(0), "the decoding task still owns coded memory");
        }
        finally { release.Set(); }
        await Until(() => receiver.Node.Transport.IncompleteBytes == 0);
        Assert.That(receiver.Node.Transport.HasBroadcastSidecar(descriptor.BlockHash), Is.False);
        Assert.That(toReceiver.Reverse.Penalties, Is.Empty);
    }

    [Test]
    public void Originating_requires_a_fully_validated_object()
    {
        using BroadcastNode origin = new();
        byte[] body = LeanBodies.EncodeBlockProof(Proof(1000));
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(default), body);
        LeanBroadcastManifest manifest = LeanBroadcastManifest.Create(TestBroadcastProfile.ProfileId, [1, 2, 3, 4], 10, 0, 7, default,
            descriptor, body, out _);
        Assert.That(origin.Engine.Originate(manifest, TestBroadcastProfile.Sign(manifest), []), Is.False);
    }

    [Test]
    public async Task First_commitment_matching_block_proof_is_kept_and_authenticated_before_import()
    {
        using LeanTestNode node = new(manualTime: false);
        byte[] proof = Proof(2000);
        BlockHeader header = LeanTransportTests.ProofHeader(proof);
        node.Tree.FindHeader(header.Hash!, Arg.Any<BlockTreeLookupOptions>(), Arg.Any<ulong?>()).Returns(header);
        byte[] body = LeanBodies.EncodeBlockProof(proof);
        LeanDescriptor first = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(header.Hash!.ValueHash256), body);
        byte[] wrongBody = LeanBodies.EncodeBlockProof(Proof(2001));
        LeanDescriptor second = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(header.Hash.ValueHash256), wrongBody);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(node.Transport.AcceptBroadcastBody(first, body, Stopwatch.GetTimestamp()), Is.Null);
            Assert.That(node.Transport.AcceptBroadcastBody(second, wrongBody, Stopwatch.GetTimestamp()), Does.Contain("commitment"));
            Assert.That(node.Transport.IsTombstoned(second.ObjectId), Is.True);
        }
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(1));
        RecursiveStark? result = await node.Transport.TryGetAsync(new Block(header, new BlockBody()), deadline.Token);
        Assert.That(result?.StarkProof, Is.EqualTo(proof));
        Assert.That(node.Transport.HasBroadcastSidecar(header.Hash.ValueHash256), Is.False, "the unvalidated retention charge was released");
    }

    [Test]
    public void Committed_broadcast_body_failing_encoding_is_invalid_while_a_mismatch_is_manifest_scoped([Values] bool committed)
    {
        using LeanTestNode node = new(manualTime: false);
        byte[] body = LeanBodies.EncodeBlockProof(Proof(2000));
        byte[] noncanonical = [.. body, 0x00];
        LeanDescriptor descriptor = LeanDescriptor.Create(LeanProtocol.KindBlockProof, LeanObjectTransport.LocalProfile,
            LeanDescriptor.BlockProofContext(default), committed ? noncanonical : body);

        string? error = node.Transport.AcceptBroadcastBody(descriptor, noncanonical, Stopwatch.GetTimestamp());

        using (Assert.EnterMultipleScope())
        {
            Assert.That(error, committed ? Does.StartWith("noncanonical body") : Is.Not.Null);
            Assert.That(node.Transport.IsTombstoned(descriptor.ObjectId), Is.EqualTo(committed));
        }
    }
}
