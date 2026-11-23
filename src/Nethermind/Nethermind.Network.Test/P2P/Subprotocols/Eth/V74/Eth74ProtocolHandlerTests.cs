// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Stats.Model;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DotNetty.Buffers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Consensus;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V74;
using Nethermind.Network.P2P.Subprotocols.Eth.V74.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Network.Test.P2P.Subprotocols.Eth.V73;
using Nethermind.Stats;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V74;

/// <summary>EIP-8498 <c>eth/74</c> dependency proof exchange.</summary>
public class Eth74ProtocolHandlerTests
{
    private WrapperTestNode _node = null!;
    private ISession _session = null!;
    private IMessageSerializationService _serializer = null!;
    private List<P2PMessage> _delivered = null!;
    private Eth74ProtocolHandler _handler = null!;
    private SparseBlobPoolPeerRegistry _registry = null!;
    private IBlockFinder _blockFinder = null!;
    private DependencyProofStore _proofStore = null!;

    [SetUp]
    public void Setup()
    {
        _node = new WrapperTestNode();
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        _serializer = Build.A.SerializationService().WithEth72(specProvider)
            .With(new NewProofWrapperHashesMessageSerializer())
            .With(new GetProofWrappersMessageSerializer())
            .With(new ProofWrappersMessageSerializer())
            .With(new GetDependencyProofsMessageSerializer())
            .With(new DependencyProofsMessageSerializer())
            .TestObject;
        _delivered = [];
        _session = Substitute.For<ISession>();
        _session.Node.Returns(new Node(TestItem.PublicKeyA, new IPEndPoint(IPAddress.Broadcast, 30303)));
        _session.When(static s => s.DeliverMessage(Arg.Any<P2PMessage>()))
            .Do(c => { lock (_delivered) _delivered.Add(c.Arg<P2PMessage>()); });
        ISyncServer syncServer = Substitute.For<ISyncServer>();
        Block genesis = Build.A.Block.Genesis.TestObject;
        syncServer.Head.Returns(genesis.Header);
        syncServer.Genesis.Returns(genesis.Header);
        BlobCustodyTracker custody = new();
        _registry = new SparseBlobPoolPeerRegistry(_node.Pool, custody, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        _blockFinder = Substitute.For<IBlockFinder>();
        _proofStore = new DependencyProofStore(new TestMemDb());
        _handler = new Eth74ProtocolHandler(_session, _serializer, new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance),
            syncServer, RunImmediatelyScheduler.Instance, _node.Pool, Substitute.For<IGossipPolicy>(), new ForkInfo(specProvider, syncServer),
            LimboLogs.Instance, Substitute.For<ITxPoolConfig>(), specProvider, custody, _registry, _node.Transport, _blockFinder, _proofStore);
        _handler.Init();
        using StatusMessage69 status = new() { ProtocolVersion = 74, GenesisHash = genesis.Hash!, LatestBlockHash = genesis.Hash! };
        Handle(status, 0);
    }

    [TearDown]
    public void TearDown()
    {
        lock (_delivered) foreach (P2PMessage message in _delivered) message.Dispose();
        _handler.Dispose();
        _session.Dispose();
        _registry.Dispose();
        _node.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_handler.Name, Is.EqualTo("eth74"));
            Assert.That(_handler.ProtocolVersion, Is.EqualTo(74));
            Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(0x1b));
        }
    }

    // EIP-8498: a stored proof is served; an unknown, pre-fork, pruned or dependency-free block gets the empty entry.
    [Test]
    public void Serves_proofs_from_the_store_and_the_empty_entry_when_unavailable()
    {
        BlockHeader stored = Committed(1, [7, 8, 9]);
        BlockHeader pruned = Committed(2, [1]);
        BlockHeader empty = Committed(3, []);
        BlockHeader preFork = Build.A.BlockHeader.WithNumber(4).TestObject;
        _proofStore.Insert(stored.Number, stored.Hash!, [7, 8, 9]);
        _proofStore.Insert(empty.Number, empty.Hash!, []);
        Hash256[] hashes = [stored.Hash!, pruned.Hash!, empty.Hash!, preFork.Hash!, TestItem.KeccakH];

        using GetDependencyProofsMessage request = new(77, hashes.ToPooledList(hashes.Length));
        Handle(request, Eth74MessageCode.GetDependencyProofs);

        DependencyProofsMessage response = Delivered<DependencyProofsMessage>().Single();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response.RequestId, Is.EqualTo(77));
            Assert.That(response.Proofs.ToArray(), Is.EqualTo(new byte[][] { [7, 8, 9], [], [], [], [] }));
        }
    }

    [Test]
    public void Stops_the_response_after_the_soft_limit()
    {
        BlockHeader[] headers = [Committed(1, [1]), Committed(2, [2]), Committed(3, [3])];
        int size = Eth74Protocol.SoftResponseLimit / 2 + 1;
        foreach (BlockHeader header in headers) _proofStore.Insert(header.Number, header.Hash!, new byte[size]);

        using GetDependencyProofsMessage request = new(5, headers.Select(static h => h.Hash!).ToArray().ToPooledList(3));
        Handle(request, Eth74MessageCode.GetDependencyProofs);

        Assert.That(Delivered<DependencyProofsMessage>().Single().Proofs, Has.Count.EqualTo(2));
    }

    // EIP-8498 asks responders to rate limit: a burst beyond the per-peer backlog is answered with no proofs.
    [Test]
    public async Task Rate_limits_serving_per_peer()
    {
        const int requests = 20;
        BlockHeader stored = Committed(1, [7, 8, 9]);
        _proofStore.Insert(stored.Number, stored.Hash!, [7, 8, 9]);

        for (int i = 0; i < requests; i++)
        {
            using GetDependencyProofsMessage request = new(i, new[] { stored.Hash! }.ToPooledList(1));
            Handle(request, Eth74MessageCode.GetDependencyProofs);
        }

        TimeSpan deadline = Eth74Protocol.MaxServeBacklog * 3;
        for (TimeSpan waited = TimeSpan.Zero; Delivered<DependencyProofsMessage>().Length < requests && waited < deadline; waited += TimeSpan.FromMilliseconds(50))
            await Task.Delay(50);

        DependencyProofsMessage[] responses = Delivered<DependencyProofsMessage>();
        int maxServed = 1 + (int)(Eth74Protocol.MaxServeBacklog.TotalSeconds * Eth74Protocol.MaxServedRequestsPerSecond) + 1;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(responses, Has.Length.EqualTo(requests), "every request is answered");
            Assert.That(responses.Count(static r => r.Proofs.Count > 0), Is.InRange(1, maxServed));
        }
    }

    [Test]
    public async Task Requests_and_receives_proofs([Values] bool viaSyncPeerInterface)
    {
        Hash256[] hashes = [TestItem.KeccakA, TestItem.KeccakB];
        Task<IOwnedReadOnlyList<byte[]>> task = viaSyncPeerInterface
            ? ((ISyncPeer)_handler).GetDependencyProofs(hashes, CancellationToken.None)
            : _handler.GetDependencyProofs(hashes, CancellationToken.None);
        GetDependencyProofsMessage sent = Delivered<GetDependencyProofsMessage>().Single();
        Assert.That(sent.Hashes.ToArray(), Is.EqualTo(hashes));

        using DependencyProofsMessage reply = new(sent.RequestId, new ArrayPoolList<byte[]>(2, new byte[][] { [1, 2], [] }));
        Handle(reply, Eth74MessageCode.DependencyProofs);

        using IOwnedReadOnlyList<byte[]> proofs = await task;
        Assert.That(proofs.ToArray(), Is.EqualTo(new byte[][] { [1, 2], [] }));
    }

    private BlockHeader Committed(ulong number, byte[] proof)
    {
        BlockHeader header = Build.A.BlockHeader.WithNumber(number).TestObject;
        header.RecursiveStark = new RecursiveStarkCommitment(Keccak.Compute(proof), Keccak.Compute("deps"));
        header.Hash = header.CalculateHash();
        _blockFinder.FindHeader(header.Hash, Arg.Any<BlockTreeLookupOptions>()).Returns(header);
        return header;
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
}
