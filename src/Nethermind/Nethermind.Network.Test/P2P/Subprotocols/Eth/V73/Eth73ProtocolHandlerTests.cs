// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.Contract.Messages;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V62;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;
using static Nethermind.Network.Test.P2P.Subprotocols.Eth.V73.WrapperTestObjects;
using GetPooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;
using PooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

/// <summary>EIP-8437 wrapper exchange and transaction-message rules through the <c>eth/73</c> handler.</summary>
public class Eth73ProtocolHandlerTests
{
    private WrapperTestNode _node = null!;
    private ISession _session = null!;
    private IMessageSerializationService _serializer = null!;
    private List<P2PMessage> _delivered = null!;
    private Eth73ProtocolHandler _handler = null!;
    private Block _genesis = null!;
    private SparseBlobPoolPeerRegistry _registry = null!;
    private IChainHeadSpecProvider _specProvider = null!;

    [SetUp]
    public void Setup()
    {
        _node = new WrapperTestNode();
        IChainHeadSpecProvider specProvider = _specProvider = Substitute.For<IChainHeadSpecProvider>();
        _serializer = Build.A.SerializationService().WithEth72(specProvider)
            .With(new NewProofWrapperHashesMessageSerializer())
            .With(new GetProofWrappersMessageSerializer())
            .With(new ProofWrappersMessageSerializer())
            .TestObject;
        _delivered = [];
        _session = Substitute.For<ISession>();
        _session.Node.Returns(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303)));
        _session.When(static s => s.DeliverMessage(Arg.Any<P2PMessage>()))
            .Do(c => { lock (_delivered) _delivered.Add(c.Arg<P2PMessage>()); });
        ISyncServer syncServer = Substitute.For<ISyncServer>();
        _genesis = Build.A.Block.Genesis.TestObject;
        syncServer.Head.Returns(_genesis.Header);
        syncServer.Genesis.Returns(_genesis.Header);
        ITxPoolConfig txPoolConfig = Substitute.For<ITxPoolConfig>();
        txPoolConfig.BlobsSupport.Returns(BlobsSupportMode.InMemory);
        ITxGossipPolicy gossipPolicy = Substitute.For<ITxGossipPolicy>();
        gossipPolicy.ShouldListenToGossipedTransactions.Returns(true);
        BlobCustodyTracker custody = new();
        _registry = new SparseBlobPoolPeerRegistry(_node.Pool, custody, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        _handler = new Eth73ProtocolHandler(_session, _serializer, new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance),
            syncServer, RunImmediatelyScheduler.Instance, _node.Pool, Substitute.For<IGossipPolicy>(), new ForkInfo(specProvider, syncServer),
            LimboLogs.Instance, txPoolConfig, specProvider, custody, _registry, _node.Transport, gossipPolicy);
        _handler.Init();
        using StatusMessage69 status = new() { ProtocolVersion = 73, GenesisHash = _genesis.Hash!, LatestBlockHash = _genesis.Hash! };
        Handle(status, 0);
    }

    [TearDown]
    public void TearDown()
    {
        _handler.Dispose();
        _session.Dispose();
        _registry.Dispose();
        _node.Dispose();
    }

    private void Handle<T>(T message, int code) where T : MessageBase
    {
        IByteBuffer buffer = _serializer.ZeroSerialize(message);
        try
        {
            buffer.ReadByte();
            _handler.HandleMessage(new ZeroPacket(buffer) { PacketType = (byte)code });
        }
        finally { buffer.Release(); }
    }

    private T[] Delivered<T>() { lock (_delivered) return [.. _delivered.OfType<T>()]; }

    // EIP-8437: GetPooledTransactions treats dependency-bearing transactions as unavailable on every eth version, eth/73
    // included; other pooled transactions are served as usual.
    [Test]
    public void Dependency_bearing_transactions_are_unavailable_through_get_pooled_transactions()
    {
        Transaction dependent = FrameTransaction(1);
        Transaction plain = Build.A.Transaction.WithType(TxType.EIP1559).SignedAndResolved().TestObject;
        foreach (Transaction transaction in new[] { dependent, plain })
            _node.Pool.TryGetPendingTransactionWithoutBlobs(transaction.Hash!, out Arg.Any<Transaction?>())
                .Returns(call => { call[1] = transaction; return true; });

        using GetPooledTransactionsMessage66 request = new(new ArrayPoolList<ValueHash256>(2) { dependent.Hash!.ValueHash256, plain.Hash!.ValueHash256 });
        Handle(request, Eth66MessageCode.GetPooledTransactions);

        PooledTransactionsMessage66 response = Delivered<PooledTransactionsMessage66>().Single();
        Assert.That(response.EthMessage.Transactions.Select(static t => t.Hash), Is.EqualTo(new[] { plain.Hash }));
    }

    [Test]
    public async Task Wrapper_retrieval_round_trips_without_envelope_requests()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        using NewProofWrapperHashesMessage announce = new([Describe(wrapper)]);
        Handle(announce, Eth73MessageCode.NewProofWrapperHashes);
        GetProofWrappersMessage wrapperRequest = Delivered<GetProofWrappersMessage>().Single();
        Assert.That(wrapperRequest.Full, Is.True);
        using ProofWrappersMessage wrapperResponse = new(wrapperRequest.RequestId, [Entry(wrapper)]);
        Handle(wrapperResponse, Eth73MessageCode.ProofWrappers);

        await WrapperTestNode.Until(() => _node.Transport.IsStored(Describe(wrapper).Hash) && _node.Transport.FetchCount == 0);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_node.Pending.Select(static t => t.Hash), Is.EqualTo(new[] { transaction.Hash }));
            Assert.That(Delivered<GetPooledTransactionsMessage66>(), Is.Empty);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    // EIP-8437: dependency-bearing transactions never appear in Transactions or NewPooledTransactionHashes, eth/73 included.
    [Test]
    public void Dependency_bearing_transactions_do_not_propagate_on_eth73([Values] bool sendFullTx)
    {
        Transaction transaction = FrameTransaction(1);
        _handler.SendNewTransactions([transaction], sendFullTx);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Delivered<TransactionsMessage>(), Is.Empty);
            Assert.That(Delivered<NewPooledTransactionHashesMessage72>(), Is.Empty);
        }
    }

    private void EnableFrameTransactions()
    {
        IReleaseSpec spec = Substitute.For<IReleaseSpec>();
        spec.IsEip8141Enabled.Returns(true);
        _specProvider.GetCurrentHeadSpec().Returns(spec);
    }

    // A pushed dependency-bearing transaction is ignored: only a proof wrapper carries it.
    [Test]
    public void Pushed_dependency_bearing_transactions_are_ignored()
    {
        EnableFrameTransactions();
        Transaction transaction = FrameTransaction(1);
        using TransactionsMessage message = new(new ArrayPoolList<Transaction>(1) { transaction });
        Handle(message, Eth62MessageCode.Transactions);
        using (Assert.EnterMultipleScope())
        {
            _node.Pool.DidNotReceive().SubmitTx(Arg.Any<Transaction>(), Arg.Any<TxHandlingOptions>());
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    // EIP-8437: until EIP-8288 is enabled for local mempool validation, received wrapper messages are ignored without
    // penalties, malformed ones included, and none is sent.
    [Test]
    public void Wrapper_messages_are_ignored_without_penalty_while_eip8288_is_inactive()
    {
        _node.Tree.Head.Returns((Block?)null);
        using NewProofWrapperHashesMessage announce = new([Describe(Wrapper(64, FrameTransaction(1)))]);
        Handle(announce, Eth73MessageCode.NewProofWrapperHashes);
        using GetProofWrappersMessage request = new(1, true, [TestItem.KeccakA.ValueHash256]);
        Handle(request, Eth73MessageCode.GetProofWrappers);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Delivered<GetProofWrappersMessage>(), Is.Empty);
            Assert.That(Delivered<ProofWrappersMessage>(), Is.Empty);
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }
}
