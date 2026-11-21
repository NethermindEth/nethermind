// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Threading.Tasks;
using Nethermind.Core.Crypto;
using Nethermind.Core.Metric;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Analyzers;
using Nethermind.Network.P2P.EventArg;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.Rlpx;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P;

[Parallelizable(ParallelScope.Self)]
public class SessionTests
{
    private IRlpxConnection _connection;
    private IPacketSender _packetSender;

    [SetUp]
    public void SetUp()
    {
        _connection = Substitute.For<IRlpxConnection>();
        _packetSender = Substitute.For<IPacketSender>();
    }

    [Test]
    public void Constructor_sets_the_values()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyB, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.RemoteNodeId, Is.EqualTo(TestItem.PublicKeyB));
            Assert.That(session.LocalPort, Is.EqualTo(30312));
            Assert.That(session.Direction, Is.EqualTo(ConnectionDirection.Out));
            Assert.That(session.SessionId, Is.Not.EqualTo(default(Guid)));
        }
    }

    [Test]
    public void Can_set_remaining_properties()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.PingSender = Substitute.For<IPingSender>();
        Assert.That(session.PingSender, Is.Not.Null);
        session.ObsoleteRemoteNodeId = TestItem.PublicKeyC;
        Assert.That(session.ObsoleteRemoteNodeId, Is.Not.Null);
    }

    [Test]
    public void Node_can_be_retrieved_only_after_remote_known()
    {
        Session session = new(30312, _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        Assert.Throws<InvalidOperationException>(() =>
        {
            Node node = session.Node;
        });

        session.Handshake(TestItem.PublicKeyA);
        session.RemoteHost = "127.0.0.1";
        session.RemotePort = 30000;
        Assert.That(session.Node, Is.Not.Null);
    }

    [Test]
    public void Throws_when_init_called_before_the_handshake()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        Assert.Throws<InvalidOperationException>(() => session.Init(5, _packetSender));
    }

    [Test]
    public void Raises_event_on_init()
    {
        bool wasCalled = false;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Initialized += (s, e) => wasCalled = true;

        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.P2PVersion, Is.EqualTo(5));
            Assert.That(wasCalled, Is.True);
        }
    }

    [Test]
    public void Sets_p2p_version_on_init()
    {
        Session session = new(30312, _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyB);
        session.Init(4, _packetSender);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(session.P2PVersion, Is.EqualTo(4));
            Assert.That(session.RemoteNodeId, Is.EqualTo(TestItem.PublicKeyB));
        }
    }

    [Test]
    public void Raises_event_on_handshake()
    {
        bool wasCalled = false;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.HandshakeComplete += (s, e) => wasCalled = true;

        session.Handshake(TestItem.PublicKeyA);
        Assert.That(wasCalled, Is.True);
    }

    [Test]
    public void Outbound_buffer_is_full_when_the_connection_is_not_writable_after_init()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        _connection.IsWritable.Returns(false);
        Assert.That(session.IsOutboundBufferFull, Is.False, "Before init");

        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        Assert.That(session.IsOutboundBufferFull, Is.True, "Not writable");

        _connection.IsWritable.Returns(true);
        Assert.That(session.IsOutboundBufferFull, Is.False, "Writable");
    }

    [Test]
    public void Cannot_handshake_twice()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        Assert.Throws<InvalidOperationException>(() => session.Handshake(TestItem.PublicKeyA));
    }

    [Test]
    public void Cannot_handshake_after_init()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        Assert.Throws<InvalidOperationException>(() => session.Handshake(TestItem.PublicKeyA));
    }

    [Test]
    public void Cannot_enable_snappy_before_init()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        Assert.Throws<InvalidOperationException>(() => session.EnableSnappy());
        session.Handshake(TestItem.PublicKeyA);
        Assert.Throws<InvalidOperationException>(() => session.EnableSnappy());
        session.Init(5, _packetSender);
    }

    [Test]
    public void Enables_snappy_on_the_connection()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        session.EnableSnappy();
        _connection.Received(1).EnableSnappy();
    }

    [Test]
    public void Enabling_snappy_on_disconnected_will_not_cause_trouble()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, "test");
        session.EnableSnappy();
        _connection.DidNotReceive().EnableSnappy();
    }

    [Test]
    public async Task Adding_protocols_when_disconnecting_will_not_cause_trouble()
    {
        bool shouldStop = false;
        int i = 0;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        void addProtocol()
        {
            IProtocolHandler required = Substitute.For<IProtocolHandler>();
            required.ProtocolCode.Returns("p2p");
            required.MessageIdSpaceSize.Returns(16);
            session.AddProtocolHandler(required);
            while (!shouldStop)
            {
                IProtocolHandler protocolHandler = Substitute.For<IProtocolHandler>();
                protocolHandler.ProtocolCode.Returns("aa" + i++);
                protocolHandler.MessageIdSpaceSize.Returns(10);
                session.AddProtocolHandler(protocolHandler);
            }
        }

        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        Task task = new(addProtocol);
        task.Start();

        await Task.Delay(20);
        session.InitiateDisconnect(DisconnectReason.Other, "test");
        await Task.Delay(10);
        shouldStop = true;
    }

    [Test]
    public void Cannot_init_before_the_handshake()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        Assert.Throws<InvalidOperationException>(() => session.Init(5, _packetSender));
    }

    [Test]
    public void Is_closing_is_false_when_not_closing()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        Assert.That(session.IsClosing, Is.False);
        session.Handshake(TestItem.PublicKeyA);
        Assert.That(session.IsClosing, Is.False);
        session.Init(5, _packetSender);
        Assert.That(session.IsClosing, Is.False);
    }

    [Test]
    public void Best_state_reached_is_correct()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        Assert.That(session.BestStateReached, Is.EqualTo(SessionState.New));
        session.Handshake(TestItem.PublicKeyA);
        Assert.That(session.BestStateReached, Is.EqualTo(SessionState.HandshakeComplete));
        session.Init(5, _packetSender);
        Assert.That(session.BestStateReached, Is.EqualTo(SessionState.Initialized));
    }

    [Test]
    public void Cannot_dispose_unless_disconnected()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        Assert.Throws<InvalidOperationException>(() => session.Dispose());
        session.Init(5, _packetSender);
        Assert.Throws<InvalidOperationException>(() => session.Dispose());

        IProtocolHandler p2p = BuildHandler("p2p", 10);
        IProtocolHandler aaa = BuildHandler("aaa", 10);
        IProtocolHandler bbb = BuildHandler("bbb", 5);
        IProtocolHandler ccc = BuildHandler("ccc", 1);
        session.AddProtocolHandler(p2p);
        session.AddProtocolHandler(aaa);
        session.AddProtocolHandler(bbb);
        session.AddProtocolHandler(ccc);

        session.InitiateDisconnect(DisconnectReason.Other, "test");
        session.Dispose();

        aaa.Received().DisconnectProtocol(DisconnectReason.Other, "test");
        bbb.Received().DisconnectProtocol(DisconnectReason.Other, "test");
        ccc.Received().DisconnectProtocol(DisconnectReason.Other, "test");

        aaa.Received().Dispose();
        bbb.Received().Dispose();
        ccc.Received().Dispose();
    }

    [Test]
    public void Raises_event_on_disconnecting()
    {
        bool wasCalled = false;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Disconnecting += (s, e) => wasCalled = true;

        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        session.InitiateDisconnect(DisconnectReason.Other);
        Assert.That(wasCalled, Is.True);
    }

    [Test]
    public void Raises_event_on_disconnected()
    {
        bool wasCalled = false;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Disconnected += (s, e) => wasCalled = true;

        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, "test");
        Assert.That(wasCalled, Is.True);
    }

    [Test]
    public void Disconnected_handlers_can_unsubscribe_during_dispatch()
    {
        int firstCalls = 0;
        int secondCalls = 0;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);

        EventHandler<DisconnectEventArgs>? first = null;
        EventHandler<DisconnectEventArgs>? second = null;

        first = (_, _) =>
        {
            firstCalls++;
            session.Disconnected -= first;
        };

        second = (_, _) =>
        {
            secondCalls++;
            session.Disconnected -= second;
        };

        session.Disconnected += first;
        session.Disconnected += second;

        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, "test");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(firstCalls, Is.EqualTo(1));
            Assert.That(secondCalls, Is.EqualTo(1));
        }
    }

    [Test]
    public void Removing_one_duplicate_disconnected_handler_keeps_remaining_subscription()
    {
        int calls = 0;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        EventHandler<DisconnectEventArgs> handler = (_, _) => calls++;

        session.Disconnected += handler;
        session.Disconnected += handler;
        session.Disconnected -= handler;

        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, "test");

        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void Disconnects_after_initiating_disconnect()
    {
        bool wasCalled = false;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Disconnecting += (s, e) => wasCalled = true;

        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        session.InitiateDisconnect(DisconnectReason.Other);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wasCalled, Is.True);
            Assert.That(session.IsClosing, Is.True);
        }
    }

    // Privileged (static or trusted) nodes are kept connected through capacity/transient reasons, but
    // chain-identity and protocol mismatches must still disconnect them.
    [TestCase(false, DisconnectReason.TooManyPeers, false)]
    [TestCase(true, DisconnectReason.TooManyPeers, false)]
    [TestCase(false, DisconnectReason.UselessPeer, false)]
    [TestCase(true, DisconnectReason.UselessPeer, false)]
    [TestCase(false, DisconnectReason.ConnectionReset, false)]
    [TestCase(true, DisconnectReason.ConnectionReset, false)]
    [TestCase(false, DisconnectReason.InvalidNetworkId, true)]
    [TestCase(true, DisconnectReason.InvalidNetworkId, true)]
    [TestCase(false, DisconnectReason.InvalidForkId, true)]
    [TestCase(true, DisconnectReason.InvalidForkId, true)]
    [TestCase(false, DisconnectReason.IncompatibleP2PVersion, true)]
    [TestCase(true, DisconnectReason.IncompatibleP2PVersion, true)]
    public void Privileged_node_disconnect_depends_on_reason(bool useStatic, DisconnectReason reason, bool shouldDisconnect)
    {
        bool wasCalled = false;
        Node node = new(TestItem.PublicKeyA, "127.0.0.1", 8545);
        if (useStatic) node.IsStatic = true; else node.IsTrusted = true;
        Session session = new(30312, node, _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Disconnecting += (s, e) => wasCalled = true;

        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        session.InitiateDisconnect(reason);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wasCalled, Is.EqualTo(shouldDisconnect));
            Assert.That(session.IsClosing, Is.EqualTo(shouldDisconnect));
        }
    }

    [Test]
    public void Error_on_connection_when_disconnecting_does_not_prevent_the_event([Values] bool initialized)
    {
        bool wasCalled = false;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        _connection.DisconnectAsync().Returns(Task.FromException<Exception>(new Exception()));
        session.Disconnected += (s, e) => wasCalled = true;
        if (initialized)
        {
            session.Handshake(TestItem.PublicKeyA);
            session.Init(5, _packetSender);
        }

        session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, "test");
        Assert.That(wasCalled, Is.True);
    }

    [Test]
    public async Task Delays_closing_the_connection_only_for_local_disconnects_after_init([Values] bool initialized, [Values] DisconnectType disconnectType)
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        TaskCompletionSource<TimeSpan> closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        long start = 0;
        _connection.DisconnectAsync().Returns(_ =>
        {
            closed.TrySetResult(Stopwatch.GetElapsedTime(start));
            return Task.CompletedTask;
        });
        session.Handshake(TestItem.PublicKeyA);
        if (initialized) session.Init(5, _packetSender);

        start = Stopwatch.GetTimestamp();
        session.MarkDisconnected(DisconnectReason.Other, disconnectType, "test");

        if (initialized && disconnectType == DisconnectType.Local)
        {
            // A timer can fire up to a system clock tick early
            Assert.That(await closed.Task.WaitAsync(Timeouts.Disconnection * 10),
                Is.GreaterThanOrEqualTo(Timeouts.Disconnection - TimeSpan.FromMilliseconds(50)), "Closed after");
        }
        else
        {
            Assert.That(closed.Task.IsCompleted, Is.True, "Closed right away");
        }

        await _connection.Received(1).DisconnectAsync();
    }

    [Test]
    public void Can_disconnect_many_times()
    {
        int wasCalledTimes = 0;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Disconnecting += (s, e) => wasCalledTimes++;

        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        session.InitiateDisconnect(DisconnectReason.Other);
        session.InitiateDisconnect(DisconnectReason.Other);
        session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, "test");
        session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, "test");
        Assert.That(wasCalledTimes, Is.EqualTo(1));
    }

    [Test]
    public void Can_disconnect_before_init()
    {
        int wasCalledTimes = 0;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Disconnecting += (s, e) => wasCalledTimes++;
        session.Handshake(TestItem.PublicKeyA);
        session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, "test");
        session.Init(5, _packetSender);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(wasCalledTimes, Is.EqualTo(1));
            Assert.That(session.IsOutboundBufferFull, Is.False, "Init returned early");
        }
    }

    [Test]
    public void On_incoming_sessions_can_fill_remote_id_on_handshake()
    {
        Session session = new(30312, _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyB);
        Assert.That(session.RemoteNodeId, Is.EqualTo(TestItem.PublicKeyB));
    }

    [Test]
    public void Outgoing_handshake_with_unexpected_identity_is_rejected()
    {
        DisconnectReason? disconnectReason = null;
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Disconnecting += (_, e) => disconnectReason = e.DisconnectReason;

        session.Handshake(TestItem.PublicKeyB);

        Assert.That(session.RemoteNodeId, Is.EqualTo(TestItem.PublicKeyA));
        session.Init(5, _packetSender);
        Assert.That(disconnectReason, Is.EqualTo(DisconnectReason.UnexpectedIdentity));
    }

    [Test]
    public void Checks_init_arguments()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        Assert.Throws<ArgumentNullException>(() => session.Init(5, null), "packageSender");
    }

    [Test]
    public void Can_add_and_disconnect_many_handlers()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        IProtocolHandler p2p = BuildHandler("p2p", 10);
        IProtocolHandler aaa = BuildHandler("aaa", 10);
        IProtocolHandler bbb = BuildHandler("bbb", 5);
        IProtocolHandler ccc = BuildHandler("ccc", 1);
        session.AddProtocolHandler(p2p);
        session.AddProtocolHandler(aaa);
        session.AddProtocolHandler(bbb);
        session.AddProtocolHandler(ccc);
        session.InitiateDisconnect(DisconnectReason.Other, "test");
        aaa.Received().DisconnectProtocol(DisconnectReason.Other, "test");
        bbb.Received().DisconnectProtocol(DisconnectReason.Other, "test");
        ccc.Received().DisconnectProtocol(DisconnectReason.Other, "test");
    }

    [Test]
    public void Cannot_add_handlers_before_p2p()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        IProtocolHandler aaa = BuildHandler("aaa", 10);
        Assert.Throws<InvalidOperationException>(() => session.AddProtocolHandler(aaa));
    }

    [Test]
    public void Cannot_add_handler_twice()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        IProtocolHandler p2p = BuildHandler("p2p", 10);
        IProtocolHandler p2pAgain = BuildHandler("p2p", 10);
        session.AddProtocolHandler(p2p);
        Assert.Throws<InvalidOperationException>(() => session.AddProtocolHandler(p2pAgain));
    }

    private IProtocolHandler BuildHandler(string code, int spaceSize)
    {
        IProtocolHandler handler = Substitute.For<IProtocolHandler>();
        handler.Name.Returns(code);
        handler.ProtocolCode.Returns(code);
        handler.MessageIdSpaceSize.Returns(spaceSize);
        return handler;
    }

    [Test]
    [NonParallelizable]
    public void Can_deliver_messages()
    {
        Metrics.P2PBytesSent = 0;

        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        IProtocolHandler p2p = BuildHandler("p2p", 10);
        IProtocolHandler aaa = BuildHandler("aaa", 10);
        IProtocolHandler bbb = BuildHandler("bbb", 5);
        IProtocolHandler ccc = BuildHandler("ccc", 1);
        session.AddProtocolHandler(p2p);
        session.AddProtocolHandler(aaa);
        session.AddProtocolHandler(bbb);
        session.AddProtocolHandler(ccc);

        TestMessage message = new();
        _packetSender.Enqueue(message).Returns(10);
        session.DeliverMessage(message);
        _packetSender.Received().Enqueue(message);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(message.WasDisposed, Is.True);

            Assert.That(Metrics.P2PBytesSent, Is.EqualTo(10));
        }
    }

    [Test]
    public void Cannot_deliver_before_initialized()
    {
        TestMessage message = new();
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        Assert.Throws<InvalidOperationException>(() => session.DeliverMessage(message));
        session.Handshake(TestItem.PublicKeyA);
        Assert.Throws<InvalidOperationException>(() => session.DeliverMessage(message));
        Assert.That(message.WasDisposed, Is.True);
        session.Init(5, _packetSender);
        IProtocolHandler p2p = BuildHandler("p2p", 10);
        session.AddProtocolHandler(p2p);
    }

    [Test]
    public void Cannot_receive_before_initialized()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        Assert.Throws<InvalidOperationException>(() => session.ReceiveMessage(new Packet("p2p", 1, [])));
        session.Handshake(TestItem.PublicKeyA);
        Assert.Throws<InvalidOperationException>(() => session.ReceiveMessage(new Packet("p2p", 1, [])));
        session.Init(5, _packetSender);
        IProtocolHandler p2p = BuildHandler("p2p", 10);
        session.AddProtocolHandler(p2p);
    }

    [Test]
    public void Stops_delivering_messages_after_disconnect()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        IProtocolHandler p2p = BuildHandler("p2p", 10);
        session.AddProtocolHandler(p2p);

        session.InitiateDisconnect(DisconnectReason.Other);

        TestMessage message = new();
        session.DeliverMessage(message);
        _packetSender.DidNotReceive().Enqueue(Arg.Any<TestMessage>());
        Assert.That(message.WasDisposed, Is.True);
    }

    [Test]
    public void Initiated_disconnect_is_not_logged_at_debug()
    {
        TestLogger logger = new() { IsTrace = false };
        Session session = new(
            30312,
            new Node(TestItem.PublicKeyA, "127.0.0.1", 8545),
            _connection,
            NullDisconnectsAnalyzer.Instance,
            new OneLoggerLogManager(new ILogger(logger)));
        session.Disconnected += static (_, _) => { };
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        logger.LogList.Clear();

        session.InitiateDisconnect(DisconnectReason.BreachOfProtocol);

        Assert.That(logger.LogList, Has.None.Contains("initiating disconnect"));
    }

    [Test]
    public void Stops_receiving_messages_after_disconnect()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        IProtocolHandler p2p = BuildHandler("p2p", 10);
        session.AddProtocolHandler(p2p);

        session.InitiateDisconnect(DisconnectReason.Other);

        session.ReceiveMessage(new Packet("p2p", 3, []));
        p2p.DidNotReceive().HandleMessage(Arg.Is<Packet>(static p => p.Protocol == "p2p" && p.PacketType == 3));
    }

    [Test]
    public void Delay_disconnect_until_after_initialize()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.InitiateDisconnect(DisconnectReason.TooManyPeers);

        IProtocolHandler p2p = BuildHandler("p2p", 10);
        session.AddProtocolHandler(p2p);

        session.Init(5, _packetSender);

        p2p.Received().DisconnectProtocol(Arg.Any<DisconnectReason>(), Arg.Any<string?>());
    }

    [Test]
    public void Protocol_handler_can_send_message_on_disconnect()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.InitiateDisconnect(DisconnectReason.TooManyPeers);

        IProtocolHandler p2p = BuildHandler("p2p", 10);
        session.AddProtocolHandler(p2p);

        TestMessage message = new();
        p2p.When(it => it.DisconnectProtocol(Arg.Any<DisconnectReason>(), Arg.Any<string>()))
            .Do((_) =>
            {
                session.DeliverMessage(message);
            });

        session.Init(5, _packetSender);
        session.InitiateDisconnect(DisconnectReason.Other);

        _packetSender
            .Received()
            .Enqueue(message);

        Assert.That(message.WasDisposed, Is.True);
    }

    [Test]
    public void Can_receive_messages()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        IProtocolHandler p2p = BuildHandler("p2p", 10);
        IProtocolHandler aaa = BuildHandler("aaa", 10);
        IProtocolHandler bbb = BuildHandler("bbb", 5);
        IProtocolHandler ccc = BuildHandler("ccc", 1);
        session.AddProtocolHandler(p2p);
        session.AddProtocolHandler(aaa);
        session.AddProtocolHandler(bbb);
        session.AddProtocolHandler(ccc);

        byte[] data = new byte[10];
        session.ReceiveMessage(new Packet("---", 3, data));
        p2p.Received().HandleMessage(Arg.Is<Packet>(static p => p.Protocol == "p2p" && p.PacketType == 3));

        session.ReceiveMessage(new Packet("---", 11, data));
        aaa.Received().HandleMessage(Arg.Is<Packet>(static p => p.Protocol == "aaa" && p.PacketType == 1));

        session.ReceiveMessage(new Packet("---", 21, data));
        bbb.Received().HandleMessage(Arg.Is<Packet>(static p => p.Protocol == "bbb" && p.PacketType == 1));

        session.ReceiveMessage(new Packet("---", 25, data));
        ccc.Received().HandleMessage(Arg.Is<Packet>(static p => p.Protocol == "ccc" && p.PacketType == 0));

        session.ReceiveMessage(new Packet("---", 100, data));

        Assert.That(session.BytesReceived, Is.EqualTo(data.Length * 5));
    }

    // Nothing else reads the node of a bare inbound session here. Each case starts from a fresh session, so one raise
    // site cannot create the node for the other. The highest port is still a usable one.
    [Test]
    public void Inbound_session_raises_message_events_with_its_node([Values] bool received, [Values(30000, IPEndPoint.MaxPort)] int port)
    {
        Session session = CreateInboundSession(TestItem.PublicKeyA, "127.0.0.1", port);
        List<Node> nodes = [];
        if (received) session.MsgReceived += (_, e) => nodes.Add(e.Node);
        else session.MsgDelivered += (_, e) => nodes.Add(e.Node);

        RaiseMessageEvent(session, received);
        RaiseMessageEvent(session, received);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(nodes, Has.Count.EqualTo(2).And.All.Not.Null, "both events carry a node");
            Assert.That(nodes, Has.All.SameAs(session.Node), "the node is created once and kept");
            Assert.That(session.Node.Id, Is.EqualTo(TestItem.PublicKeyA));
            Assert.That(session.Node.Address, Is.EqualTo(new IPEndPoint(IPAddress.Loopback, port)));
        }
    }

    // Without a subscriber the event arguments are not evaluated, so raising an event does not create the node: it is
    // built when Node is read, from the details known then.
    [Test]
    public void Message_events_without_a_subscriber_leave_the_node_to_be_created_on_read([Values] bool received)
    {
        Session session = CreateInboundSession(TestItem.PublicKeyA, "127.0.0.1", 30000);

        RaiseMessageEvent(session, received);
        session.RemotePort = 30001;

        Assert.That(session.Node.Port, Is.EqualTo(30001));
    }

    private static IEnumerable<TestCaseData> UnusableRemoteDetails()
    {
        foreach (bool received in new[] { true, false })
        {
            string direction = received ? "received" : "delivered";
            yield return new TestCaseData(null, "127.0.0.1", 30000, received).SetArgDisplayNames("no id", direction);
            yield return new TestCaseData(TestItem.PublicKeyA, null, 30000, received).SetArgDisplayNames("no host", direction);
            yield return new TestCaseData(TestItem.PublicKeyA, "127.0.0.1", 0, received).SetArgDisplayNames("no port", direction);
            yield return new TestCaseData(TestItem.PublicKeyA, "not an address", 30000, received).SetArgDisplayNames("host not an IP address", direction);
            yield return new TestCaseData(TestItem.PublicKeyA, "127.0.0.1", -1, received).SetArgDisplayNames("negative port", direction);
            yield return new TestCaseData(TestItem.PublicKeyA, "127.0.0.1", IPEndPoint.MaxPort + 1, received).SetArgDisplayNames("port above the range", direction);
        }
    }

    // The public Node getter throws for these, and new Node(...) would throw for an unparsable host or a port out of
    // range; the message path must not.
    [TestCaseSource(nameof(UnusableRemoteDetails))]
    public void Session_with_unknown_or_unusable_remote_details_raises_message_events_without_a_node(PublicKey? remoteId, string? host, int port, bool received)
    {
        Session session = CreateInboundSession(remoteId, host, port);
        List<PeerEventArgs> raised = [];
        if (received) session.MsgReceived += (_, e) => raised.Add(e);
        else session.MsgDelivered += (_, e) => raised.Add(e);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(() => RaiseMessageEvent(session, received), Throws.Nothing, "the events are raised on the p2p path");
            Assert.That(raised, Has.Count.EqualTo(1).And.All.Property(nameof(PeerEventArgs.Node)).Null, "one event, without a node");
        }
    }

    private Session CreateInboundSession(PublicKey? remoteId, string? host, int port)
    {
        Session session = new(30312, _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance)
        {
            RemoteHost = host,
            RemotePort = port,
        };
        session.Handshake(remoteId);
        session.Init(5, _packetSender);
        session.AddProtocolHandler(BuildHandler("p2p", 10));
        return session;
    }

    private static void RaiseMessageEvent(Session session, bool received)
    {
        if (received) session.ReceiveMessage(new Packet("---", 3, new byte[10]));
        else session.DeliverMessage(new TestMessage());
    }

    [Test]
    public void Updates_local_and_remote_metrics_on_disconnects()
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, new MetricsDisconnectsAnalyzer(), LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        IProtocolHandler p2p = BuildHandler("p2p", 10);
        session.AddProtocolHandler(p2p);

        long beforeLocal = Network.Metrics.LocalDisconnectsTotal.GetValueOrDefault(DisconnectReason.Other);
        long beforeRemote = Network.Metrics.RemoteDisconnectsTotal.GetValueOrDefault(DisconnectReason.Other);
        session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Local, string.Empty);
        long afterLocal = Network.Metrics.LocalDisconnectsTotal.GetValueOrDefault(DisconnectReason.Other);
        long afterRemote = Network.Metrics.RemoteDisconnectsTotal.GetValueOrDefault(DisconnectReason.Other);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterLocal, Is.EqualTo(beforeLocal + 1));
            Assert.That(afterRemote, Is.EqualTo(beforeRemote));
        }

        session = new Session(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, new MetricsDisconnectsAnalyzer(), LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        p2p = BuildHandler("p2p", 10);
        session.AddProtocolHandler(p2p);

        beforeLocal = Network.Metrics.LocalDisconnectsTotal.GetValueOrDefault(DisconnectReason.Other);
        beforeRemote = Network.Metrics.RemoteDisconnectsTotal.GetValueOrDefault(DisconnectReason.Other);
        session.MarkDisconnected(DisconnectReason.Other, DisconnectType.Remote, string.Empty);
        afterLocal = Network.Metrics.LocalDisconnectsTotal.GetValueOrDefault(DisconnectReason.Other);
        afterRemote = Network.Metrics.RemoteDisconnectsTotal.GetValueOrDefault(DisconnectReason.Other);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(afterLocal, Is.EqualTo(beforeLocal));
            Assert.That(afterRemote, Is.EqualTo(beforeRemote + 1));
        }
    }

    [Test]
    public void Message_metrics_count_every_message_and_byte_once_per_key()
    {
        const string protocol = "mta";
        const byte version = 7;
        Session session = CreateInitializedSession(BuildHandler("p2p", 10), BuildVersionedHandler(protocol, 10, version));
        _packetSender.Enqueue(Arg.Any<ProtocolMessage>()).Returns(20);
        byte[] data = new byte[10];

        for (int i = 0; i < 5; i++) session.ReceiveMessage(new Packet("---", 11, data));
        for (int i = 0; i < 3; i++) session.DeliverMessage(new ProtocolMessage(protocol, 2));
        session.ReceiveMessage(new Packet("---", 100, data));

        P2PMessageKey received = new(new VersionedProtocol(protocol, version), 1);
        P2PMessageKey delivered = new(new VersionedProtocol(protocol, version), 2);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.IncomingP2PMessages[received].Value, Is.EqualTo(5));
            Assert.That(Metrics.IncomingP2PMessageBytes[received].Value, Is.EqualTo(50));
            Assert.That(Metrics.OutgoingP2PMessages[delivered].Value, Is.EqualTo(3));
            Assert.That(Metrics.OutgoingP2PMessageBytes[delivered].Value, Is.EqualTo(60));
            Assert.That(Metrics.IncomingP2PMessages.ContainsKey(delivered), Is.False);
            Assert.That(Metrics.OutgoingP2PMessages.ContainsKey(received), Is.False);
            Assert.That(CountKeys(Metrics.IncomingP2PMessages, protocol), Is.EqualTo(1));
            Assert.That(CountKeys(Metrics.OutgoingP2PMessageBytes, protocol), Is.EqualTo(1));
        }
    }

    [Test]
    public void Message_metrics_follow_protocol_version_changes()
    {
        const string protocol = "mtv";
        IProtocolHandler handler = BuildVersionedHandler(protocol, 10, 1);
        Session session = CreateInitializedSession(BuildHandler("p2p", 10), handler);
        _packetSender.Enqueue(Arg.Any<ProtocolMessage>()).Returns(4);
        byte[] data = new byte[3];

        for (int i = 0; i < 2; i++) session.ReceiveMessage(new Packet("---", 10, data));
        session.DeliverMessage(new ProtocolMessage(protocol, 0));
        handler.ProtocolVersion.Returns((byte)2);
        for (int i = 0; i < 3; i++) session.ReceiveMessage(new Packet("---", 10, data));
        for (int i = 0; i < 2; i++) session.DeliverMessage(new ProtocolMessage(protocol, 0));

        P2PMessageKey first = new(new VersionedProtocol(protocol, 1), 0);
        P2PMessageKey second = new(new VersionedProtocol(protocol, 2), 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.IncomingP2PMessages[first].Value, Is.EqualTo(2));
            Assert.That(Metrics.IncomingP2PMessageBytes[first].Value, Is.EqualTo(6));
            Assert.That(Metrics.IncomingP2PMessages[second].Value, Is.EqualTo(3));
            Assert.That(Metrics.IncomingP2PMessageBytes[second].Value, Is.EqualTo(9));
            Assert.That(Metrics.OutgoingP2PMessages[first].Value, Is.EqualTo(1));
            Assert.That(Metrics.OutgoingP2PMessageBytes[first].Value, Is.EqualTo(4));
            Assert.That(Metrics.OutgoingP2PMessages[second].Value, Is.EqualTo(2));
            Assert.That(Metrics.OutgoingP2PMessageBytes[second].Value, Is.EqualTo(8));
        }
    }

    [Test]
    public void Message_metrics_are_exact_across_concurrent_sessions()
    {
        const string protocol = "mtc";
        const int sessions = 8;
        const int messagesPerSession = 1000;
        Session[] all = new Session[sessions];
        for (int i = 0; i < sessions; i++)
        {
            all[i] = CreateInitializedSession(BuildHandler("p2p", 10), BuildVersionedHandler(protocol, 10, 3));
        }

        _packetSender.Enqueue(Arg.Any<ProtocolMessage>()).Returns(2);
        byte[] data = new byte[5];
        Parallel.For(0, sessions, new ParallelOptions { MaxDegreeOfParallelism = sessions }, i =>
        {
            for (int j = 0; j < messagesPerSession; j++)
            {
                all[i].ReceiveMessage(new Packet("---", 13, data));
                all[i].DeliverMessage(new ProtocolMessage(protocol, 4));
            }
        });

        P2PMessageKey received = new(new VersionedProtocol(protocol, 3), 3);
        P2PMessageKey delivered = new(new VersionedProtocol(protocol, 3), 4);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Metrics.IncomingP2PMessages[received].Value, Is.EqualTo(sessions * messagesPerSession));
            Assert.That(Metrics.IncomingP2PMessageBytes[received].Value, Is.EqualTo(sessions * messagesPerSession * data.Length));
            Assert.That(Metrics.OutgoingP2PMessages[delivered].Value, Is.EqualTo(sessions * messagesPerSession));
            Assert.That(Metrics.OutgoingP2PMessageBytes[delivered].Value, Is.EqualTo(sessions * messagesPerSession * 2));
        }
    }

    private Session CreateInitializedSession(params IProtocolHandler[] handlers)
    {
        Session session = new(30312, new Node(TestItem.PublicKeyA, "127.0.0.1", 8545), _connection, NullDisconnectsAnalyzer.Instance, LimboLogs.Instance);
        session.Handshake(TestItem.PublicKeyA);
        session.Init(5, _packetSender);
        foreach (IProtocolHandler handler in handlers)
        {
            session.AddProtocolHandler(handler);
        }

        return session;
    }

    private IProtocolHandler BuildVersionedHandler(string code, int spaceSize, byte version)
    {
        IProtocolHandler handler = BuildHandler(code, spaceSize);
        handler.ProtocolVersion.Returns(version);
        return handler;
    }

    private static int CountKeys(NonBlocking.ConcurrentDictionary<P2PMessageKey, MetricCounter> metric, string protocol)
    {
        int count = 0;
        foreach (KeyValuePair<P2PMessageKey, MetricCounter> entry in metric)
        {
            if (entry.Key.Protocol.Protocol == protocol) count++;
        }

        return count;
    }

    private sealed class ProtocolMessage(string protocol, int packetType) : P2PMessage
    {
        public override int PacketType => packetType;
        public override string Protocol => protocol;
    }

    private class TestMessage : P2PMessage
    {
        public override int PacketType => P2PMessageCode.Ping;
        public override string Protocol => "p2p";

        public bool WasDisposed { get; set; }
        public override void Dispose()
        {
            base.Dispose();
            WasDisposed = true;
        }
    }
}
