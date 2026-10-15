// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Blockchain;
using Nethermind.Consensus.Scheduler;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.Rlpx;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Lean.LeanTestObjects;

namespace Nethermind.Network.Test.P2P.Subprotocols.Lean;

/// <summary>The <c>lean/1</c> handler: Status negotiation, strict decoding, and two in-process peers exchanging wrappers.</summary>
public class Lean1ProtocolHandlerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>One direction of a connection: messages are encoded, queued and handled in order by the remote handler.</summary>
    private sealed class Wire : IAsyncDisposable
    {
        private readonly Channel<(int Type, byte[] Data)> _queue = Channel.CreateUnbounded<(int, byte[])>();
        private readonly Task _reader;

        public Wire(Func<Lean1ProtocolHandler?> remote) => _reader = Task.Run(async () =>
        {
            await foreach ((int type, byte[] data) in _queue.Reader.ReadAllAsync())
            {
                if (remote() is not { } handler) continue;
                ZeroPacket packet = new(Unpooled.WrappedBuffer(data)) { PacketType = (byte)type, Protocol = LeanProtocol.Code };
                try { handler.HandleMessage(packet); }
                finally { packet.SafeRelease(); }
            }
        });

        public void Post(LeanMessage message) => _queue.Writer.TryWrite((message.PacketType, Encode(message)));

        public async ValueTask DisposeAsync()
        {
            _queue.Writer.TryComplete();
            await _reader;
        }
    }

    private static byte[] Encode(LeanMessage message) => message switch
    {
        LeanStatusMessage m => new LeanStatusMessageSerializer().Encode(m),
        AnnounceObjectsMessage m => new AnnounceObjectsMessageSerializer().Encode(m),
        GetChunksMessage m => new GetChunksMessageSerializer().Encode(m),
        ChunkMessage m => new ChunkMessageSerializer().Encode(m),
        CompleteMessage m => new CompleteMessageSerializer().Encode(m),
        CancelMessage m => new CancelMessageSerializer().Encode(m),
        _ => throw new ArgumentException(message.GetType().Name)
    };

    private sealed class Side(LeanTestNode node, ISession session, Lean1ProtocolHandler handler, ConcurrentQueue<string> disconnects)
    {
        public LeanTestNode Node { get; } = node;
        public ISession Session { get; } = session;
        public Lean1ProtocolHandler Handler { get; } = handler;
        public ConcurrentQueue<string> Disconnects { get; } = disconnects;
    }

    private static Side CreateSide(LeanTestNode node, Action<LeanMessage>? post, bool bulk = true)
    {
        ISession session = Substitute.For<ISession, ILeanBulkSession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 30303));
        session.RemoteNodeId.Returns(TestItem.PublicKeyA);
        node.EthTransactions.Register(TestItem.PublicKeyA, session, (_, _, _) => Task.FromResult(Array.Empty<Transaction>()));
        session.HasAgreedCapability(Arg.Any<Capability>()).Returns(true);
        ((ILeanBulkSession)session).EnableLeanBulk().Returns(bulk);
        ConcurrentQueue<string> disconnects = new();
        session.When(s => s.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>()))
            .Do(call => disconnects.Enqueue($"{call.Arg<DisconnectReason>()}: {call.Arg<string>()}"));
        Route<LeanStatusMessage>(session, post);
        Route<AnnounceObjectsMessage>(session, post);
        Route<GetChunksMessage>(session, post);
        Route<CompleteMessage>(session, post);
        Route<CancelMessage>(session, post);
        ((ILeanBulkSession)session).DeliverLeanChunkAsync(Arg.Any<ChunkMessage>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            post?.Invoke(call.Arg<ChunkMessage>());
            return new ValueTask<int>(1);
        });
        Lean1ProtocolHandler handler = new(session, Substitute.For<INodeStatsManager>(), Substitute.For<IMessageSerializationService>(),
            Substitute.For<IBackgroundTaskScheduler>(), LimboLogs.Instance, node.Transport, node.EthTransactions);
        return new Side(node, session, handler, disconnects);
    }

    private static void Route<T>(ISession session, Action<LeanMessage>? post) where T : LeanMessage =>
        session.When(s => s.DeliverMessage(Arg.Any<T>())).Do(call => post?.Invoke(call.Arg<T>()));

    /// <summary>Two nodes connected by ordered in-process wires, with both handshakes completed.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        private readonly Wire _toA;
        private readonly Wire _toB;

        public Connection()
        {
            _toA = new Wire(() => A?.Handler);
            _toB = new Wire(() => B?.Handler);
            A = CreateSide(new LeanTestNode(manualTime: false), _toB.Post);
            B = CreateSide(new LeanTestNode(manualTime: false), _toA.Post);
            BindEth(A, B);
            BindEth(B, A);
        }

        private static void BindEth(Side receiver, Side source)
            => receiver.Node.EthTransactions.Register(TestItem.PublicKeyA, receiver.Session, (hashes, reserve, _) =>
            {
                Transaction[] transactions = hashes.Select(hash => source.Node.Transport.TryGetRetainedTransaction(hash, out Transaction? retained)
                    ? retained : source.Node.Pending.Find(transaction => transaction.Hash!.ValueHash256 == hash)).OfType<Transaction>().ToArray();
                int bytes = transactions.Sum(transaction => TxDecoder.Instance.GetLength(transaction, RlpBehaviors.InMempoolForm | RlpBehaviors.SkipTypedWrapping));
                return Task.FromResult(reserve(bytes) ? transactions : []);
            });

        public Side A { get; }
        public Side B { get; }

        public async Task Open()
        {
            A.Handler.Init();
            B.Handler.Init();
            await Until(() => A.Node.Transport.PeerCount == 1 && B.Node.Transport.PeerCount == 1);
        }

        public async ValueTask DisposeAsync()
        {
            A.Handler.Dispose();
            B.Handler.Dispose();
            await _toA.DisposeAsync();
            await _toB.DisposeAsync();
            A.Node.Dispose();
            B.Node.Dispose();
        }
    }

    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(Timeout);
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void AssertNoDisconnects(Connection connection)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connection.A.Disconnects, Is.Empty);
            Assert.That(connection.B.Disconnects, Is.Empty);
        }
    }

    [Test]
    public async Task Mempool_wrapper_is_announced_fetched_validated_and_admitted_by_the_peer()
    {
        await using Connection connection = new();
        await connection.Open();
        Transaction transaction = FrameTransaction(1);
        connection.A.Node.Pending.Add(transaction);
        byte[] wrapper = Wrapper(3 * LeanProtocol.ChunkBytes, false, transaction);
        LeanDescriptor descriptor = Describe(wrapper);

        Assert.That((await connection.A.Node.Wrappers.AcceptDetailedAsync(wrapper)).HasValidProof, Is.True);

        await Until(() => connection.B.Node.Transport.IsStored(descriptor.ObjectId));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(connection.A.Node.Transport.IsStored(descriptor.ObjectId), Is.True);
            Assert.That(connection.B.Node.Pending.Select(t => t.Hash), Does.Contain(transaction.Hash));
        }
        AssertNoDisconnects(connection);
    }

    [Test]
    public async Task Hash_entries_are_resolved_over_ETH_before_the_peer_admits()
    {
        await using Connection connection = new();
        await connection.Open();
        Transaction transaction = FrameTransaction(2);
        connection.A.Node.Pending.Add(transaction);
        byte[] wrapper = Wrapper(LeanProtocol.ChunkBytes, true, transaction);

        Assert.That((await connection.A.Node.Wrappers.AcceptDetailedAsync(wrapper)).HasValidProof, Is.True);

        await Until(() => connection.B.Node.Pending.Any(t => t.Hash == transaction.Hash));
        AssertNoDisconnects(connection);
    }

    private static Side Single(out LeanTestNode node, bool bulk = true)
    {
        node = new LeanTestNode();
        return CreateSide(node, null, bulk);
    }

    private static void Receive(Side side, int type, byte[] data)
    {
        ZeroPacket packet = new(Unpooled.WrappedBuffer(data)) { PacketType = (byte)type, Protocol = LeanProtocol.Code };
        try { side.Handler.HandleMessage(packet); }
        finally { packet.SafeRelease(); }
    }

    private static byte[] StatusBytes(LeanStatusMessage? status = null) => Encode(status ?? LeanTestNode.Status());

    [TestCase("before status")]
    [TestCase("second status")]
    [TestCase("malformed")]
    [TestCase("unknown message")]
    [TestCase("former Cancel ID")]
    [TestCase("after incompatible status")]
    public void Protocol_violations_disconnect_the_peer(string scenario)
    {
        Side side = Single(out LeanTestNode node);
        using (node)
        {
            side.Handler.Init();
            switch (scenario)
            {
                case "before status":
                    Receive(side, LeanMessageCode.Cancel, Encode(new CancelMessage(1)));
                    break;
                case "second status":
                    Receive(side, LeanMessageCode.Status, StatusBytes());
                    Receive(side, LeanMessageCode.Status, StatusBytes());
                    break;
                case "malformed":
                    Receive(side, LeanMessageCode.Status, StatusBytes());
                    Receive(side, LeanMessageCode.Cancel, [0xc3, 0x82, 0x00, 0x07]);
                    break;
                case "unknown message":
                    Receive(side, LeanMessageCode.Status, StatusBytes());
                    Receive(side, LeanProtocol.MessageCount, [0xc0]);
                    break;
                case "former Cancel ID":
                    // Six consecutive IDs: 0x07 is outside the capability's message space.
                    Receive(side, LeanMessageCode.Status, StatusBytes());
                    Receive(side, 0x07, Encode(new CancelMessage(1)));
                    break;
                case "after incompatible status":
                    // Disabling only the capability leaves the handler inert rather than disconnecting.
                    Receive(side, LeanMessageCode.Status, StatusBytes(new LeanStatusMessage(2, LeanTestNode.Genesis.ValueHash256,
                        [LeanObjectTransport.LocalProfile], LeanLimits.MaxObjectBytes)));
                    Assert.That(side.Disconnects, Is.Empty);
                    Assert.That(node.Transport.PeerCount, Is.Zero);
                    return;
            }
            Assert.That(side.Disconnects.Single(), Does.StartWith(nameof(DisconnectReason.BreachOfProtocol)));
        }
    }

    [Test]
    public void Compatible_status_registers_the_peer_and_disposal_releases_it()
    {
        Side side = Single(out LeanTestNode node);
        using (node)
        {
            int initialized = 0;
            side.Handler.ProtocolInitialized += (_, _) => initialized++;
            side.Handler.Init();
            Receive(side, LeanMessageCode.Status, StatusBytes());
            Assert.That(initialized, Is.EqualTo(1));
            Assert.That(node.Transport.PeerCount, Is.EqualTo(1));
            side.Handler.Dispose();
            Assert.That(node.Transport.PeerCount, Is.Zero);
            Assert.That(side.Disconnects, Is.Empty);
        }
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public void Initialization_without_the_capability_or_chunk_transport_disconnects(bool capability, bool bulk)
    {
        Side side = Single(out LeanTestNode node, bulk);
        using (node)
        {
            side.Session.HasAgreedCapability(Arg.Any<Capability>()).Returns(capability);
            side.Handler.Init();
            using (Assert.EnterMultipleScope())
            {
                Assert.That(side.Disconnects.Single(), Does.StartWith(nameof(DisconnectReason.BreachOfProtocol)));
                side.Session.DidNotReceive().DeliverMessage(Arg.Any<LeanStatusMessage>());
            }
        }
    }
}
