// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using DotNetty.Buffers;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Blocks;
using Nethermind.Blockchain.Find;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Timers;
using Nethermind.Logging;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V72;
using Nethermind.Network.P2P.Subprotocols.Eth.V72.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V76;
using Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Network.Test.Builders;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats;
using Nethermind.Stats.Model;
using Nethermind.Synchronization;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V76;

/// <summary>EIP-8518 <c>eth/76</c> message handling over a session.</summary>
public class Eth76ProtocolHandlerTests
{
    private ISession _session = null!;
    private IMessageSerializationService _serializer = null!;
    private List<P2PMessage> _delivered = null!;
    private Eth76ProtocolHandler _handler = null!;
    private SparseBlobPoolPeerRegistry _registry = null!;
    private ProofWrapperRelay _relay = null!;
    private ITxPool _txPool = null!;

    [SetUp]
    public void Setup()
    {
        IChainHeadSpecProvider specProvider = Substitute.For<IChainHeadSpecProvider>();
        specProvider.GetCurrentHeadSpec().Returns(Eip8288Prototype.Instance);
        _serializer = Build.A.SerializationService().WithEth72(specProvider)
            .With(new NewProofWrapperHashesMessageSerializer())
            .With(new GetProofWrappersMessageSerializer())
            .With(new ProofWrappersMessageSerializer())
            .With(new GetProofWrapperManifestsMessageSerializer())
            .With(new ProofWrapperManifestsMessageSerializer())
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
        _txPool = Substitute.For<ITxPool>();
        _txPool.SubmitTx(default!, default).ReturnsForAnyArgs(AcceptTxResult.Accepted);
        _txPool.ValidateTxForBlobSampling(default!).ReturnsForAnyArgs(AcceptTxResult.Accepted);
        _registry = new SparseBlobPoolPeerRegistry(_txPool, custody, RunImmediatelyScheduler.Instance, LimboLogs.Instance);
        _relay = new ProofWrapperRelay(_txPool, new DependencyProofCache(new AcceptingVerifier(), LimboLogs.Instance), new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance),
            Substitute.For<IBlockTree>(), Timestamper.Default, new BlocksConfig { DependencyProofAggregation = DependencyProofAggregation.Mempool }, LimboLogs.Instance);
        _handler = new Eth76ProtocolHandler(_session, _serializer, new NodeStatsManager(Substitute.For<ITimerFactory>(), LimboLogs.Instance),
            syncServer, RunImmediatelyScheduler.Instance, _txPool, Substitute.For<IGossipPolicy>(), new ForkInfo(specProvider, syncServer),
            LimboLogs.Instance, new TxPoolConfig { BlobsSupport = BlobsSupportMode.StorageWithReorgs }, specProvider, custody, _registry, Substitute.For<IBlockFinder>(),
            new DependencyProofStore(new TestMemDb()), _relay);
        _handler.Init();
        using StatusMessage69 status = new() { ProtocolVersion = 76, GenesisHash = genesis.Hash!, LatestBlockHash = genesis.Hash! };
        Handle(status, 0);
    }

    [TearDown]
    public void TearDown()
    {
        lock (_delivered) foreach (P2PMessage message in _delivered) message.Dispose();
        _handler.Dispose();
        _session.Dispose();
        _registry.Dispose();
        _relay.Dispose();
    }

    [Test]
    public void Metadata_correct()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(_handler.Name, Is.EqualTo("eth76"));
            Assert.That(_handler.ProtocolVersion, Is.EqualTo(76));
            Assert.That(_handler.MessageIdSpaceSize, Is.EqualTo(0x1d));
            Assert.That(new[] { Eth76MessageCode.NewProofWrapperHashes, Eth76MessageCode.GetProofWrappers, Eth76MessageCode.ProofWrappers,
                Eth76MessageCode.GetProofWrapperManifests, Eth76MessageCode.ProofWrapperManifests }, Is.EqualTo(new[] { 0x18, 0x19, 0x1a, 0x1b, 0x1c }));
        }
    }

    /// <summary>The handler factory registers each handler under its static version, so eth/76 must not take over eth/74.</summary>
    [Test]
    public void Registers_under_its_own_version()
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(StaticVersion<Eth76ProtocolHandler>(), Is.EqualTo(76));
            Assert.That(StaticVersion<Nethermind.Network.P2P.Subprotocols.Eth.V74.Eth74ProtocolHandler>(), Is.EqualTo(74));
        }

        static byte StaticVersion<T>() where T : IStaticProtocolInfo => T.Version;
    }

    [Test]
    public void Announces_a_held_wrapper_and_serves_it()
    {
        ProofWrapper wrapper = Wrapper();
        _relay.AddLocal(wrapper);
        NewProofWrapperHashesMessage announcement = Delivered<NewProofWrapperHashesMessage>().Single();

        GetProofWrappersMessage request = new(5, [wrapper.Hash.ValueHash256, TestItem.KeccakF.ValueHash256], compact: false);
        Handle(request, Eth76MessageCode.GetProofWrappers);
        Assert.That(() => Delivered<ProofWrappersMessage>(), Has.Length.EqualTo(1).After(5000, 10));
        ProofWrappersMessage response = Delivered<ProofWrappersMessage>().Single();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(announcement.Announcements.Single().Hash, Is.EqualTo(wrapper.Hash.ValueHash256));
            Assert.That(response.RequestId, Is.EqualTo(5));
            Assert.That(response.Wrappers.Single().Item, Is.EqualTo(wrapper.Encoded), "the unannounced hash is omitted");
        }
    }

    [Test]
    public void Fetches_an_announced_wrapper_and_admits_it()
    {
        ProofWrapper wrapper = Wrapper();
        Handle(new NewProofWrapperHashesMessage([new WrapperAnnouncement(wrapper.Length, wrapper.Hash.ValueHash256, wrapper.DepsHash, wrapper.TxsHash)]),
            Eth76MessageCode.NewProofWrapperHashes);
        Assert.That(() => Delivered<GetProofWrappersMessage>(), Has.Length.EqualTo(1).After(5000, 10));
        GetProofWrappersMessage request = Delivered<GetProofWrappersMessage>().Single();

        Handle(new ProofWrappersMessage(request.RequestId, [new ServedWrapper(wrapper.Hash.ValueHash256, wrapper.Encoded)]), Eth76MessageCode.ProofWrappers);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(request.Compact, Is.True);
            Assert.That(() => _relay.TryGetHeld(wrapper.Hash.ValueHash256, out _), Is.True.After(5000, 10));
            _txPool.Received(1).SubmitTx(Arg.Any<Transaction>(), TxHandlingOptions.None);
        }
    }

    /// <summary>A blob-carrying frame transaction's cell announcement is tracked only while an EIP-8518 wrapper's elided copy
    /// of it awaits cells; other frame transactions keep travelling whole.</summary>
    [TestCase(true, 1)]
    [TestCase(false, 0)]
    public void Tracks_frame_cell_announcements_only_for_an_aggregated_copy_awaiting_cells(bool awaiting, int providers)
    {
        Transaction tx = FrameTxTestFrames.LeanSphincsBlobTx(0);
        if (awaiting)
        {
            byte[] encoded = ProofWrapperCodec.Normalize(tx)!;
            Transaction elided = TxDecoder.Instance.DecodeCompleteNotNull(encoded, ProofWrapperCodec.TransactionBehaviors);
            elided.ClaimWitnesses![0] = Nethermind.Evm.TransactionProcessing.FrameTxClaimVerifier.AggregatedWitness;
            AcceptTxResult? recorded = _registry.RecordTransaction(_handler, elided);
            Assert.That(_registry.HasRecordedTransaction(tx.Hash!.ValueHash256), Is.True, recorded?.ToString() ?? "null");
        }

        Handle(new NewPooledTransactionHashesMessage72([(byte)TxType.FrameTx], [tx.GetLength()], [tx.Hash!.ValueHash256], BlobCellMask.Full.ToBytes()),
            Eth72MessageCode.NewPooledTransactionHashes);

        Assert.That(_registry.GetFullProviderAnnouncementCount(tx.Hash!.ValueHash256), Is.EqualTo(providers));
    }

    /// <summary>Whether a cell mask is required depends on the announced types alone: a frame transaction announced without
    /// one is a whole-transaction announcement, even while an elided copy of it awaits cells.</summary>
    [Test]
    public void Frame_announcement_without_a_cell_mask_is_accepted_while_an_elided_copy_awaits_cells()
    {
        Transaction tx = FrameTxTestFrames.LeanSphincsBlobTx(0);
        Transaction elided = TxDecoder.Instance.DecodeCompleteNotNull(ProofWrapperCodec.Normalize(tx)!, ProofWrapperCodec.TransactionBehaviors);
        elided.ClaimWitnesses![0] = Nethermind.Evm.TransactionProcessing.FrameTxClaimVerifier.AggregatedWitness;
        _registry.RecordTransaction(_handler, elided);

        Handle(new NewPooledTransactionHashesMessage72([(byte)TxType.FrameTx], [tx.GetLength()], [tx.Hash!.ValueHash256], BlobCellMask.Empty.ToBytes()),
            Eth72MessageCode.NewPooledTransactionHashes);

        _session.DidNotReceiveWithAnyArgs().InitiateDisconnect(default, default);
    }

    private static ProofWrapper Wrapper()
    {
        Transaction tx = FrameTxTestFrames.LeanSphincsTx(0, false, (TestItem.KeccakA, TestItem.KeccakB));
        List<AggregatedDependency> deps = Eip8288Dependencies.Canonicalize(ProofWrapperCodec.AggregatedDependencies(tx));
        return ProofWrapperCodec.Create([ProofWrapperCodec.Normalize(tx)!], [tx], deps, [1, 2, 3]);
    }

    private void Handle<T>(T message, int code) where T : P2PMessage
    {
        IByteBuffer buffer = _serializer.ZeroSerialize(message);
        try
        {
            buffer.ReadByte();
            _handler.HandleMessage(new NettyBufferPacket(buffer) { PacketType = (byte)code });
        }
        finally { buffer.Release(); }
    }

    private T[] Delivered<T>() { lock (_delivered) return [.. _delivered.OfType<T>()]; }

    private sealed class AcceptingVerifier : ILeanProofVerifier
    {
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 message, in ValueHash256 publicKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => true;
        public byte[] ProveDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) => [1];
    }
}
