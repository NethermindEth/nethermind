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
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
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
using PooledTransactionsMessage65 = Nethermind.Network.P2P.Subprotocols.Eth.V65.Messages.PooledTransactionsMessage;
using PooledTransactionsMessage66 = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;

/// <summary>EIP-8437 envelope recovery and serving through the <c>eth/73</c> handler's GetPooledTransactions path.</summary>
public class Eth73ProtocolHandlerTests
{
    private WrapperTestNode _node = null!;
    private ISession _session = null!;
    private IMessageSerializationService _serializer = null!;
    private List<P2PMessage> _delivered = null!;
    private Eth73ProtocolHandler _handler = null!;
    private Block _genesis = null!;
    private SparseBlobPoolPeerRegistry _registry = null!;

    [SetUp]
    public void Setup()
    {
        _node = new WrapperTestNode();
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
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

    [Test]
    public void Dependency_bearing_envelopes_are_served_from_the_pool_and_the_wrapper_envelope_cache()
    {
        Transaction cached = FrameTransaction(1), pooled = FrameTransaction(2);
        _node.Pending.Add(cached);
        Assert.That(_node.Transport.PublishWrapper(Wrapper(64, cached)), Is.True);
        _node.Pending.Clear();
        _node.Pool.TryGetPendingTransactionWithoutBlobs(pooled.Hash!, out Arg.Any<Transaction?>())
            .Returns(call => { call[1] = pooled; return true; });

        using GetPooledTransactionsMessage66 request = new(new ArrayPoolList<ValueHash256>(3) { cached.Hash!.ValueHash256, TestItem.KeccakA.ValueHash256, pooled.Hash!.ValueHash256 });
        Handle(request, Eth66MessageCode.GetPooledTransactions);

        PooledTransactionsMessage66 response = Delivered<PooledTransactionsMessage66>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RequestId, Is.EqualTo(request.RequestId));
            Assert.That(response.EthMessage.Transactions.Select(static t => t.Hash), Is.EqualTo(new[] { cached.Hash, pooled.Hash }),
                "served in request order, the unknown hash omitted");
        }
    }

    [Test]
    public async Task Envelope_recovery_round_trips_through_get_pooled_transactions()
    {
        Transaction transaction = FrameTransaction(1);
        byte[] wrapper = Wrapper(64, transaction);
        using NewProofWrapperHashesMessage announce = new([Describe(wrapper)]);
        Handle(announce, Eth73MessageCode.NewProofWrapperHashes);
        GetProofWrappersMessage wrapperRequest = Delivered<GetProofWrappersMessage>().Single();
        using ProofWrappersMessage wrapperResponse = new(wrapperRequest.RequestId, [wrapper]);
        Handle(wrapperResponse, Eth73MessageCode.ProofWrappers);

        await WrapperTestNode.Until(() => Delivered<GetPooledTransactionsMessage66>().Length == 1);
        GetPooledTransactionsMessage66 envelopeRequest = Delivered<GetPooledTransactionsMessage66>().Single();
        Assert.That(envelopeRequest.EthMessage.Hashes.AsSpan().ToArray(), Is.EqualTo(new[] { transaction.Hash!.ValueHash256 }));

        // An uncorrelated response stays with the eth/72 handling, which ignores it.
        using PooledTransactionsMessage66 stray = new(envelopeRequest.RequestId + 1, new PooledTransactionsMessage65(new ArrayPoolList<Transaction>(1) { transaction }));
        Handle(stray, Eth66MessageCode.PooledTransactions);
        Assert.That(_node.Pending, Is.Empty);

        using PooledTransactionsMessage66 response = new(envelopeRequest.RequestId, new PooledTransactionsMessage65(new ArrayPoolList<Transaction>(1) { transaction }));
        Handle(response, Eth66MessageCode.PooledTransactions);
        await WrapperTestNode.Until(() => _node.Transport.IsStored(Describe(wrapper).Hash));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_node.Pending.Select(static t => t.Hash), Is.EqualTo(new[] { transaction.Hash }));
            _session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }
}
