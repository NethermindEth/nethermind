// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V71;
using Nethermind.Network.P2P.Subprotocols.Eth.V71.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V71;

public class Eth71ProtocolHandlerTests
{
    private ISession _session = null!;
    private IMessageSerializationService _svc = null!;
    private ISyncServer _syncManager = null!;
    private ITxPool _transactionPool = null!;
    private IGossipPolicy _gossipPolicy = null!;
    private ISpecProvider _specProvider = null!;
    private Block _genesisBlock = null!;
    private Eth71ProtocolHandler _handler = null!;
    private ITxGossipPolicy _txGossipPolicy = null!;
    private ITimerFactory _timerFactory = null!;

    [SetUp]
    public void Setup()
    {
        NetworkDiagTracer.IsEnabled = true;

        _session = Substitute.For<ISession>();
        Node node = new(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303));
        _session.Node.Returns(node);
        _syncManager = Substitute.For<ISyncServer>();
        _transactionPool = Substitute.For<ITxPool>();
        _specProvider = Substitute.For<ISpecProvider>();
        _gossipPolicy = Substitute.For<IGossipPolicy>();
        _genesisBlock = Build.A.Block.Genesis.TestObject;
        _syncManager.Head.Returns(_genesisBlock.Header);
        _syncManager.Genesis.Returns(_genesisBlock.Header);
        _syncManager.LowestBlock.Returns(0);
        _timerFactory = Substitute.For<ITimerFactory>();
        _txGossipPolicy = Substitute.For<ITxGossipPolicy>();
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        _txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
        _svc = Build.A.SerializationService().WithEth71(_specProvider).TestObject;
        _handler = new Eth71ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            _gossipPolicy,
            new ForkInfo(_specProvider, _syncManager),
            LimboLogs.Instance,
            Substitute.For<ITxPoolConfig>(),
            _specProvider,
            _txGossipPolicy);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        _session?.ReceivedCalls()
            .Where(c => c.GetMethodInfo().Name == nameof(_session.DeliverMessage))
            .ForEach(c => (c.GetArguments()[0] as P2PMessage)?.Dispose());

        _handler.Dispose();
        _session.Dispose();
        _syncManager.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        Assert.That(_handler.ProtocolCode, Is.EqualTo("eth"));
        Assert.That(_handler.Name, Is.EqualTo("eth71"));
        Assert.That(_handler.ProtocolVersion, Is.EqualTo(71));
        Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(20));
        Assert.That(_handler.IncludeInTxPool, Is.True);
        Assert.That(_handler.ClientId, Is.EqualTo(_session.Node?.ClientId));
        Assert.That(_handler.HeadHash, Is.Null);
        Assert.That(_handler.HeadNumber, Is.EqualTo(0));
    }

    [Test]
    public void Should_handle_GetBlockAccessLists_and_return_available_BALs()
    {
        byte[] bal1 = [0xc1, 0x80]; // some RLP bytes
        byte[] bal2 = [0xc2, 0x01, 0x02];

        _syncManager.GetBlockAccessListRlp(Keccak.Zero).Returns(bal1);
        _syncManager.GetBlockAccessListRlp(TestItem.KeccakA).Returns(bal2);

        HandleIncomingStatusMessage();

        using GetBlockAccessListsMessage request = new(1111,
            new Hash256[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList(2));
        HandleZeroMessage(request, Eth71MessageCode.GetBlockAccessLists);

        _session.Received(1).DeliverMessage(Arg.Is<BlockAccessListsMessage>(m =>
            m.RequestId == 1111 &&
            m.BlockAccessLists.Count == 2 &&
            SameBytes(m.BlockAccessLists[0], bal1) &&
            SameBytes(m.BlockAccessLists[1], bal2)));
    }

    [Test]
    public void Should_return_empty_BAL_for_unavailable_blocks()
    {
        _syncManager.GetBlockAccessListRlp(Arg.Any<Hash256>()).Returns((byte[]?)null);

        HandleIncomingStatusMessage();

        using GetBlockAccessListsMessage request = new(2222,
            new Hash256[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList(2));
        HandleZeroMessage(request, Eth71MessageCode.GetBlockAccessLists);

        _session.Received(1).DeliverMessage(Arg.Is<BlockAccessListsMessage>(m =>
            m.RequestId == 2222 &&
            m.BlockAccessLists.Count == 2 &&
            IsMissing(m.BlockAccessLists[0]) &&
            IsMissing(m.BlockAccessLists[1])));
    }

    [Test]
    public void Should_return_mixed_available_and_empty_BALs()
    {
        byte[] bal1 = [0xc3, 0x01, 0x02, 0x03];
        _syncManager.GetBlockAccessListRlp(Keccak.Zero).Returns(bal1);
        _syncManager.GetBlockAccessListRlp(TestItem.KeccakA).Returns((byte[]?)null);
        _syncManager.GetBlockAccessListRlp(TestItem.KeccakB).Returns((byte[]?)null);

        HandleIncomingStatusMessage();

        using GetBlockAccessListsMessage request = new(3333,
            new Hash256[] { Keccak.Zero, TestItem.KeccakA, TestItem.KeccakB }.ToPooledList(3));
        HandleZeroMessage(request, Eth71MessageCode.GetBlockAccessLists);

        _session.Received(1).DeliverMessage(Arg.Is<BlockAccessListsMessage>(m =>
            m.BlockAccessLists.Count == 3 &&
            SameBytes(m.BlockAccessLists[0], bal1) &&
            IsMissing(m.BlockAccessLists[1]) &&
            IsMissing(m.BlockAccessLists[2])));
    }

    [Test]
    public async Task Can_request_and_handle_block_access_lists()
    {
        byte[] bal1 = [0xc1, 0x80];
        byte[] bal2 = [0xc2, 0x01, 0x02];

        BlockAccessListsMessage? response = null;

        _session.When(s => s.DeliverMessage(Arg.Any<GetBlockAccessListsMessage>())).Do(call =>
        {
            GetBlockAccessListsMessage sent = (GetBlockAccessListsMessage)call[0];
            response = new BlockAccessListsMessage(sent.RequestId, new ArrayPoolList<byte[]?>(2) { bal1, bal2 });
        });

        HandleIncomingStatusMessage();
        Task<IOwnedReadOnlyList<byte[]?>> task = _handler.GetBlockAccessLists(
            new[] { Keccak.Zero, TestItem.KeccakA }, CancellationToken.None);

        _session.Received(1).DeliverMessage(Arg.Any<GetBlockAccessListsMessage>());
        HandleZeroMessage(response!, Eth71MessageCode.BlockAccessLists);

        IOwnedReadOnlyList<byte[]?> result = await task;
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0], Is.EqualTo(bal1));
        Assert.That(result[1], Is.EqualTo(bal2));
        response?.Dispose();
    }

    [Test]
    public async Task Can_request_and_handle_block_access_lists_via_sync_peer_interface()
    {
        byte[] bal1 = [0xc1, 0x80];
        byte[] bal2 = [0xc2, 0x01, 0x02];

        BlockAccessListsMessage? response = null;

        _session.When(s => s.DeliverMessage(Arg.Any<GetBlockAccessListsMessage>())).Do(call =>
        {
            GetBlockAccessListsMessage sent = (GetBlockAccessListsMessage)call[0];
            response = new BlockAccessListsMessage(sent.RequestId, new ArrayPoolList<byte[]?>(2) { bal1, bal2 });
        });

        HandleIncomingStatusMessage();
        ISyncPeer syncPeer = _handler;
        Task<IOwnedReadOnlyList<byte[]?>> task = syncPeer.GetBlockAccessLists(
            new[] { Keccak.Zero, TestItem.KeccakA }, CancellationToken.None);

        _session.Received(1).DeliverMessage(Arg.Any<GetBlockAccessListsMessage>());
        HandleZeroMessage(response!, Eth71MessageCode.BlockAccessLists);

        IOwnedReadOnlyList<byte[]?> result = await task;
        Assert.That(result, Has.Count.EqualTo(2));
        Assert.That(result[0], Is.EqualTo(bal1));
        Assert.That(result[1], Is.EqualTo(bal2));
        response?.Dispose();
    }

    [Test]
    public void Should_throw_when_receiving_unrequested_block_access_lists()
    {
        using BlockAccessListsMessage msg = new(9999, ArrayPoolList<byte[]?>.Empty());

        HandleIncomingStatusMessage();
        Assert.Throws<SubprotocolException>(() => HandleZeroMessage(msg, Eth71MessageCode.BlockAccessLists));
    }

    [Test]
    public async Task Should_return_empty_list_when_requesting_zero_hashes()
    {
        HandleIncomingStatusMessage();
        IOwnedReadOnlyList<byte[]?> result = await _handler.GetBlockAccessLists(
            Array.Empty<Hash256>(), CancellationToken.None);

        Assert.That(result, Has.Count.EqualTo(0));
        _session.DidNotReceive().DeliverMessage(Arg.Any<GetBlockAccessListsMessage>());
    }

    [Test]
    public void Should_handle_empty_request()
    {
        HandleIncomingStatusMessage();

        using GetBlockAccessListsMessage request = new(4444,
            new ArrayPoolList<Hash256>(0));
        HandleZeroMessage(request, Eth71MessageCode.GetBlockAccessLists);

        _session.Received(1).DeliverMessage(Arg.Is<BlockAccessListsMessage>(m =>
            m.RequestId == 4444 &&
            m.BlockAccessLists.Count == 0));
    }

    private void HandleIncomingStatusMessage()
    {
        using StatusMessage69 statusMsg = new()
        {
            ProtocolVersion = 71,
            GenesisHash = _genesisBlock.Hash!,
            LatestBlockHash = _genesisBlock.Hash!
        };

        IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
        statusPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
    {
        IByteBuffer packet = _svc.ZeroSerialize(msg);
        packet.ReadByte();
        _handler.HandleMessage(new ZeroPacket(packet) { PacketType = (byte)messageCode });
    }

    private static bool SameBytes(byte[]? actual, byte[] expected) =>
        actual is not null && actual.AsSpan().SequenceEqual(expected);

    private static bool IsMissing(byte[]? actual) => actual is null;
}
