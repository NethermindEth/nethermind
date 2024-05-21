// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using FluentAssertions;
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
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V68;
using Nethermind.Network.P2P.Subprotocols.Eth.V69;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using GetReceiptsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetReceiptsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V69;

public class Eth69ProtocolHandlerTests
{
    private ISession _session = null!;
    private IMessageSerializationService _svc = null!;
    private ISyncServer _syncManager = null!;
    private ITxPool _transactionPool = null!;
    private IPooledTxsRequestor _pooledTxsRequestor = null!;
    private IGossipPolicy _gossipPolicy = null!;
    private ISpecProvider _specProvider = null!;
    private Block _genesisBlock = null!;
    private Eth68ProtocolHandler _handler = null!;
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
        _pooledTxsRequestor = Substitute.For<IPooledTxsRequestor>();
        _specProvider = Substitute.For<ISpecProvider>();
        _gossipPolicy = Substitute.For<IGossipPolicy>();
        _genesisBlock = Build.A.Block.Genesis.TestObject;
        _syncManager.Head.Returns(_genesisBlock.Header);
        _syncManager.Genesis.Returns(_genesisBlock.Header);
        _timerFactory = Substitute.For<ITimerFactory>();
        _txGossipPolicy = Substitute.For<ITxGossipPolicy>();
        _txGossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        _txGossipPolicy.ShouldGossipTransaction(Arg.Any<Transaction>()).Returns(true);
        _svc = Build.A.SerializationService().WithEth69(_specProvider).TestObject;
        _handler = new Eth69ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            _syncManager,
            RunImmediatelyScheduler.Instance,
            _transactionPool,
            _pooledTxsRequestor,
            _gossipPolicy,
            new ForkInfo(_specProvider, _genesisBlock.Header.Hash!),
            LimboLogs.Instance,
            _txGossipPolicy);
        _handler.Init();
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
        _session.Dispose();
        _syncManager.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        _handler.ProtocolCode.Should().Be("eth");
        _handler.Name.Should().Be("eth69");
        _handler.ProtocolVersion.Should().Be(69);
        _handler.MessageIdSpaceSize.Should().Be(17);
        _handler.IncludeInTxPool.Should().BeTrue();
        _handler.ClientId.Should().Be(_session.Node?.ClientId);
        _handler.HeadHash.Should().BeNull();
        _handler.HeadNumber.Should().Be(0);
    }

    [Test]
    public void Can_handle_Status_message()
    {
        HandleIncomingStatusMessage();

        _handler.TotalDifficulty.Should().Be(0);
    }

    [Test]
    public async Task Can_request_and_handle_receipts()
    {
        const int count = 100;

        TxReceipt[][] receipts = Enumerable.Repeat(
            Enumerable.Repeat(Build.A.Receipt.WithAllFieldsFilled.TestObject, 10).ToArray(), count
        ).ToArray();
        ReceiptsMessage69 msg = new(0, new(receipts.ToPooledList()));

        _session.When(s => s.DeliverMessage(Arg.Any<GetReceiptsMessage66>())).Do(call =>
        {
            var message = (GetReceiptsMessage66)call[0];
            msg.RequestId = message.RequestId;
        });

        HandleIncomingStatusMessage();
        Task<IOwnedReadOnlyList<TxReceipt[]>> getReceiptsTask = _handler.GetReceipts(
            Enumerable.Repeat(Keccak.Zero, count).ToArray(), CancellationToken.None
        );

        _session.Received(1).DeliverMessage(Arg.Any<GetReceiptsMessage66>());
        HandleZeroMessage(msg, Eth63MessageCode.Receipts);

        IOwnedReadOnlyList<TxReceipt[]>? response = await getReceiptsTask;

        response.Should().HaveCount(count);
    }

    [Test]
    public void Can_handle_GetReceipts()
    {
        var msg66 = new GetReceiptsMessage(1111, new(new[] { Keccak.Zero, TestItem.KeccakA }.ToPooledList()));

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg66, Eth63MessageCode.GetReceipts);
        _session.Received(1).DeliverMessage(Arg.Any<ReceiptsMessage69>());
    }

    [Test]
    public void Ignores_NewBlock_message()
    {
        var msg = new NewBlockMessage { TotalDifficulty = 1, Block = Build.A.Block.TestObject };

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth62MessageCode.NewBlock);

        _syncManager.DidNotReceiveWithAnyArgs().AddNewBlock(Arg.Any<Block>(), Arg.Any<ISyncPeer>());
    }

    [Test]
    public void Ignores_NewBlockHashes_message()
    {
        var msg = new NewBlockHashesMessage((Keccak.MaxValue, 111));

        HandleIncomingStatusMessage();
        HandleZeroMessage(msg, Eth62MessageCode.NewBlockHashes);

        _syncManager.DidNotReceiveWithAnyArgs().HintBlock(Arg.Any<Hash256>(), Arg.Any<long>(), Arg.Any<ISyncPeer>());
    }

    private void HandleIncomingStatusMessage()
    {
        var statusMsg = new StatusMessage69 { ProtocolVersion = 69, GenesisHash = _genesisBlock.Hash, BestHash = _genesisBlock.Hash };

        IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
        statusPacket.ReadByte();
        _handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(T msg, int messageCode) where T : MessageBase
    {
        IByteBuffer uOpsPacket = _svc!.ZeroSerialize(msg);
        uOpsPacket.ReadByte();
        _handler!.HandleMessage(new ZeroPacket(uOpsPacket) { PacketType = (byte)messageCode });
    }
}
