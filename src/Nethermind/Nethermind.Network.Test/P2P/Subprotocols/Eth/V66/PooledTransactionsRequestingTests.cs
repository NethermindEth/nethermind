// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Spec;
using Nethermind.Consensus;
using Nethermind.Consensus.Comparers;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Db;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V65;
using Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NSubstitute.ReceivedExtensions;
using NUnit.Framework;
using System;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using PooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V66;

[TestFixture, Parallelizable(ParallelScope.Self)]
public class PooledTransactionsRequestingTests
{
    private ISession _session = null!;
    private ISession _session2 = null!;
    private Eth66ProtocolHandler _handler = null!;
    private ArrayPoolList<Transaction> _txs = null!;
    private IMessageSerializationService _svc = null!;
    private Block _genesisBlock = null!;
    private CompositeDisposable _disposables = null!;

    private readonly int Timeout = 3000;
    private readonly int InTime = 1000;

    [SetUp]
    public void Setup()
    {
        _svc = Build.A.SerializationService().WithEth66().TestObject;

        IGossipPolicy _gossipPolicy = Substitute.For<IGossipPolicy>();
        _disposables = [];

        _session = Substitute.For<ISession>();
        _session.Node.Returns(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303)));
        _session.When(s => s.DeliverMessage(Arg.Any<P2PMessage>())).Do(c => c.Arg<P2PMessage>().AddTo(_disposables));

        _genesisBlock = Build.A.Block.Genesis.TestObject;

        TestSingleReleaseSpecProvider specProvider = new(Osaka.Instance);
        IBlockTree blockTree = Build.A.BlockTree().WithoutSettingHead.WithSpecProvider(specProvider).TestObject;

        TxPool.TxPool transactionPool = new(
            new EthereumEcdsa(specProvider.ChainId),
            new BlobTxStorage(),
            new ChainHeadInfoProvider(
                new ChainHeadSpecProvider(specProvider, blockTree), blockTree, TestWorldStateFactory.CreateForTestWithStateReader(TestMemDbProvider.Init(), LimboLogs.Instance).Item2),
            new TxPoolConfig(),
            new TxValidator(specProvider.ChainId),
            LimboLogs.Instance,
            new TransactionComparerProvider(specProvider, blockTree).GetDefaultComparer());

        PooledTxsRequestor realPooledTxsRequestor = new(transactionPool, new TxPoolConfig(), specProvider);

        ISyncServer syncManager = Substitute.For<ISyncServer>();
        syncManager.Head.Returns(_genesisBlock.Header);
        syncManager.Genesis.Returns(_genesisBlock.Header);

        ITimerFactory _timerFactory = Substitute.For<ITimerFactory>();

        _handler = new Eth66ProtocolHandler(
            _session,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            syncManager,
            RunImmediatelyScheduler.Instance,
            transactionPool,
            realPooledTxsRequestor,
            Substitute.For<IGossipPolicy>(),
            new ForkInfo(specProvider, syncManager),
            LimboLogs.Instance);

        syncManager.AddTo(_disposables);
        _handler.Init();

        Transaction tx = Build.A.Transaction.WithShardBlobTxTypeAndFields(1).WithMaxPriorityFeePerGas(1).WithGasLimit(100)
            .SignedAndResolved(new EthereumEcdsa(1), TestItem.PrivateKeyA).TestObject;

        //transactionPool.AnnounceTx(Arg.Any<ValueHash256>(), Arg.Any<Guid>(), Arg.Any<Action>())
        //    .Returns(AcceptTxResult.Accepted)
        //    .AndDoes(s => transactionPool.Received((s.Args()[0] as Transaction).Hash));

        //transactionPool.SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>())
        //    .Returns(AcceptTxResult.Accepted)
        //    .AndDoes(s => transactionPool.Received((s.Args()[0] as Transaction).Hash));

        Hash256 _txHash = tx.CalculateHash();
        _txs = new(1) { tx };
        _txs.AddTo(_disposables);

        _session2 = Substitute.For<ISession>();
        _session2.Node.Returns(new Node(TestItem.PublicKeyB, new IPEndPoint(IPAddress.Loopback, 30304)));
        _session2.When(s => s.DeliverMessage(Arg.Any<P2PMessage>())).Do(c => c.Arg<P2PMessage>().AddTo(_disposables));


        // Create second handler for second peer
        Eth66ProtocolHandler handler2 = new(
            _session2,
            _svc,
            new NodeStatsManager(_timerFactory, LimboLogs.Instance),
            syncManager,
            RunImmediatelyScheduler.Instance,
            transactionPool,
            realPooledTxsRequestor,
            _gossipPolicy,
            new ForkInfo(specProvider, syncManager),
            LimboLogs.Instance);
        handler2.Init();
        handler2.AddTo(_disposables);

        // Setup both handlers to receive status messages
        HandleIncomingStatusMessage(_handler);
        HandleIncomingStatusMessage(handler2);

        // Mock transaction pool to not have the transaction
        transactionPool.TryGetPendingTransaction(_txHash, out Arg.Any<Transaction>()).Returns(false);

        // Act - Send new pooled transaction hashes from both peers
        using NewPooledTransactionHashesMessage hashesMsg1 = new(new ArrayPoolList<Hash256>(1) { _txHash });
        using NewPooledTransactionHashesMessage hashesMsg2 = new(new ArrayPoolList<Hash256>(1) { _txHash });

        HandleZeroMessage(_handler, hashesMsg1, Eth65MessageCode.NewPooledTransactionHashes);
        HandleZeroMessage(handler2, hashesMsg2, Eth65MessageCode.NewPooledTransactionHashes);
    }

    [TearDown]
    public void TearDown()
    {
        _session.Dispose();
        _session2.Dispose();
        _handler.Dispose();
        _disposables?.Dispose();
    }

    [Test]
    public async Task Should_request_from_others_after_timout()
    {
        await Task.Delay(Timeout);

        _session2.Received(1).DeliverMessage(
            Arg.Is<Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage>(
                m => m.EthMessage.Hashes.Contains(_txs[0].Hash)));
    }


    [Test]
    public async Task Should_not_request_from_others_if_received()
    {
        await Task.Delay(InTime);
        HandleZeroMessage(_handler, new Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage(1111, new PooledTransactionsMessage(_txs)), Eth65MessageCode.PooledTransactions);
        await Task.Delay(Timeout);

        _session2.Received(0).DeliverMessage(
            Arg.Is<Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage>(
                m => m.EthMessage.Hashes.Contains(_txs[0].Hash)));
    }


    [Test]
    public async Task Should_not_request_from_others_if_received_immidietly()
    {
        HandleZeroMessage(_handler, new Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage(1111, new PooledTransactionsMessage(_txs)), Eth65MessageCode.PooledTransactions);
        await Task.Delay(Timeout);

        _session2.Received(0).DeliverMessage(
            Arg.Is<Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage>(
                m => m.EthMessage.Hashes.Contains(_txs[0].Hash)));
    }

    private void HandleIncomingStatusMessage(Eth66ProtocolHandler handler)
    {
        using var statusMsg = new StatusMessage();
        statusMsg.GenesisHash = _genesisBlock.Hash;
        statusMsg.BestHash = _genesisBlock.Hash;

        IByteBuffer statusPacket = _svc.ZeroSerialize(statusMsg);
        statusPacket.ReadByte();
        handler.HandleMessage(new ZeroPacket(statusPacket) { PacketType = 0 });
    }

    private void HandleZeroMessage<T>(Eth66ProtocolHandler handler, T msg, int messageCode) where T : MessageBase
    {
        IByteBuffer packet = _svc.ZeroSerialize(msg);
        packet.ReadByte();
        handler.HandleMessage(new ZeroPacket(packet) { PacketType = (byte)messageCode });
    }
}
