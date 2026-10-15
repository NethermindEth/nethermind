// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Buffers;
using DotNetty.Common.Utilities;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V66;
using EthStatusMessage = Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages.StatusMessage;
using EthGetPooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.GetPooledTransactionsMessage;
using EthPooledTransactionsMessage = Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages.PooledTransactionsMessage;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

/// <summary>EIP-8437 <c>lean/1</c> over production DI wiring with the native verifier.</summary>
[NonParallelizable]
public class NativeLeanProtocolTests
{
    private sealed record WireMessage(bool Eth, int PacketType, byte[] Body);

    private sealed class Context(BasicTestBlockchain chain, ISession session, Lean1ProtocolHandler handler, Eth66ProtocolHandler eth) : IDisposable
    {
        public BasicTestBlockchain Chain { get; } = chain;
        public ISession Session { get; } = session;
        public Lean1ProtocolHandler Handler { get; } = handler;
        public Eth66ProtocolHandler Eth { get; } = eth;
        public Action<WireMessage>? PostEth { get; set; }
        public ConcurrentQueue<string> Disconnects { get; } = new();
        public LeanObjectTransport Transport => Chain.Container.Resolve<LeanObjectTransport>();
        public Action<LeanMessage>? Post { get; set; }

        public void Dispose()
        {
            Handler.Dispose();
            Eth.Dispose();
            Chain.Dispose();
        }
    }

    private static async Task<Context> Create()
    {
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
            builder.AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance)));
        ISession session = Substitute.For<ISession, ILeanBulkSession>();
        ((ILeanBulkSession)session).EnableLeanBulk().Returns(true);
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 1000, true));
        session.RemoteNodeId.Returns(TestItem.PublicKeyA);
        session.HasAgreedCapability(Arg.Any<Capability>())
            .Returns(call => call.Arg<Capability>().ProtocolCode == LeanProtocol.Code && call.Arg<Capability>().Version == LeanProtocol.Version);
        IProtocolHandler? handler = null;
        foreach (IProtocolHandlerFactory factory in chain.Container.Resolve<IProtocolHandlerFactory[]>())
            if (factory.ProtocolCode == LeanProtocol.Code && factory.TryCreate(session, LeanProtocol.Version, out handler)) break;
        Assert.That(handler, Is.TypeOf<Lean1ProtocolHandler>());
        IProtocolHandler? eth = null;
        foreach (IProtocolHandlerFactory factory in chain.Container.Resolve<IProtocolHandlerFactory[]>())
            if (factory.ProtocolCode == "eth" && factory.TryCreate(session, 66, out eth)) break;
        Assert.That(eth, Is.TypeOf<Eth66ProtocolHandler>());
        Context context = new(chain, session, (Lean1ProtocolHandler)handler!, (Eth66ProtocolHandler)eth!);
        RouteEth<EthStatusMessage>(context);
        RouteEth<EthGetPooledTransactionsMessage>(context);
        RouteEth<EthPooledTransactionsMessage>(context);
        session.When(s => s.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>()))
            .Do(call => context.Disconnects.Enqueue(call.Arg<string>()));
        Route<LeanStatusMessage>(context);
        Route<AnnounceObjectsMessage>(context);
        Route<GetChunksMessage>(context);
        Route<CompleteMessage>(context);
        Route<CancelMessage>(context);
        ((ILeanBulkSession)session).DeliverLeanChunkAsync(Arg.Any<ChunkMessage>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            context.Post?.Invoke(call.Arg<ChunkMessage>());
            return new ValueTask<int>(1);
        });
        return context;
    }

    private static void Route<T>(Context context) where T : LeanMessage =>
        context.Session.When(s => s.DeliverMessage(Arg.Any<T>())).Do(call => context.Post?.Invoke(call.Arg<T>()));

    private static void RouteEth<T>(Context context) where T : P2PMessage =>
        context.Session.When(session => session.DeliverMessage(Arg.Any<T>())).Do(call =>
        {
            T message = call.Arg<T>();
            IByteBuffer buffer = context.Chain.Container.Resolve<IMessageSerializationService>().ZeroSerialize(message);
            try
            {
                buffer.SkipBytes(1);
                byte[] body = new byte[buffer.ReadableBytes];
                buffer.ReadBytes(body);
                context.PostEth?.Invoke(new(true, message.PacketType, body));
            }
            finally { buffer.SafeRelease(); }
        });

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

    private static void Receive(Context context, LeanMessage message)
    {
        ZeroPacket packet = new(Unpooled.WrappedBuffer(Encode(message))) { PacketType = (byte)message.PacketType, Protocol = LeanProtocol.Code };
        try { context.Handler.HandleMessage(packet); }
        finally { packet.SafeRelease(); }
    }

    /// <summary>Delivers every message one side sends to the other side's handler, in order, off the sender's thread.</summary>
    private static Task Connect(Context from, Context to, Channel<WireMessage> queue)
    {
        from.Post = message => queue.Writer.TryWrite(new(false, message.PacketType, Encode(message)));
        from.PostEth = message => queue.Writer.TryWrite(message);
        return Task.Run(async () =>
        {
            await foreach (WireMessage message in queue.Reader.ReadAllAsync())
            {
                Receive(to, message);
            }
        });
    }

    private static void Receive(Context context, WireMessage message)
    {
        ZeroPacket packet = new(Unpooled.WrappedBuffer(message.Body))
        {
            PacketType = (byte)message.PacketType,
            Protocol = message.Eth ? "eth" : LeanProtocol.Code
        };
        try
        {
            if (message.Eth) context.Eth.HandleMessage(packet);
            else context.Handler.HandleMessage(packet);
        }
        finally { packet.SafeRelease(); }
    }

    private static void InitializeEth(Context context)
    {
        WireMessage? status = null;
        context.PostEth = message => status = message;
        context.Eth.Init();
        Assert.That(status, Is.Not.Null);
        Receive(context, status!);
        context.PostEth = null;
    }

    private static LeanStatusMessage Status(Context context) => context.Transport.CreateStatus()!;

    [TestCase("chain")]
    [TestCase("genesis")]
    [TestCase("guest")]
    [TestCase("old-guest")]
    public async Task Handshake_mismatch_disables_lean_without_dropping_other_protocols(string mismatch)
    {
        using Context context = await Create();
        InitializeEth(context);
        LeanStatusMessage valid = Status(context);
        ValueHash256 profile = mismatch switch
        {
            "guest" => default,
            "old-guest" => LeanCommitment.ProfileId(Convert.FromHexString("6deed6ff48d7e4af71132fb8cdc5224d16574d0358a94d274af5caee648c80ad")),
            _ => valid.Profiles[0]
        };
        context.Handler.Init();
        Receive(context, new LeanStatusMessage(mismatch == "chain" ? valid.ChainId + 1 : valid.ChainId,
            mismatch == "genesis" ? TestItem.KeccakA.ValueHash256 : valid.GenesisHash, [profile], valid.MaxObjectBytes));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Disconnects, Is.Empty);
            Assert.That(context.Transport.PeerCount, Is.Zero);
        }
        Receive(context, new CancelMessage(1));
        Assert.That(context.Disconnects, Is.Empty);
    }

    [Test]
    public async Task Unnegotiated_capability_is_rejected()
    {
        using Context context = await Create();
        foreach (IProtocolHandlerFactory factory in context.Chain.Container.Resolve<IProtocolHandlerFactory[]>())
            if (factory.ProtocolCode == LeanProtocol.Code)
            {
                Assert.That(factory.TryCreate(context.Session, 2, out IProtocolHandler? unsupported), Is.False);
                Assert.That(unsupported, Is.Null);
            }
        context.Session.HasAgreedCapability(Arg.Any<Capability>()).Returns(false);
        context.Handler.Init();
        Assert.That(context.Disconnects, Is.Not.Empty);
    }

    [Test]
    public async Task Real_recursive_wrapper_is_fetched_in_chunks_and_validated_before_admission()
    {
        byte[] proof = NativeLeanProofVerifierTests.MixedProof();
        using Context source = await Create();
        using Context target = await Create();
        using LeanP2PCapabilityResolver resolver = new(source.Chain.BlockTree, source.Chain.SpecProvider);
        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);
        Assert.That(capabilities, Does.Contain(new Capability(LeanProtocol.Code, 1)).And.Not.Contain(new Capability(LeanProtocol.Code, 2)));
        Channel<WireMessage> toTarget = Channel.CreateUnbounded<WireMessage>(), toSource = Channel.CreateUnbounded<WireMessage>();
        Task targetReader = Connect(source, target, toTarget), sourceReader = Connect(target, source, toSource);
        source.Eth.Init();
        target.Eth.Init();
        source.Handler.Init();
        target.Handler.Init();
        Transaction transaction = NativeBlockProductionTests.CreateTransaction(source.Chain);
        source.Chain.Container.Resolve<LeanProofStore>().AddVerified(
            [NativeLeanProofVerifierTests.Dependency("sphincs"), NativeLeanProofVerifierTests.Dependency("stark")], null, proof);
        Assert.That(source.Chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        await using LeanProofGossip gossip = source.Chain.Container.Resolve<LeanProofGossip>();
        gossip.Start();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (!target.Chain.TxPool.TryGetPendingTransaction(transaction.Hash!.ValueHash256, out _)) await Task.Delay(50, timeout.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.Chain.Container.Resolve<ILeanProofVerifier>(), Is.TypeOf<ProductionProofCache>());
            Assert.That(target.Chain.Container.Resolve<LeanProofStore>().Covers(transaction), Is.True);
            Assert.That(target.Transport.StoredObjects, Is.EqualTo(1), "the validated wrapper is served onward");
            Assert.That(source.Disconnects, Is.Empty);
            Assert.That(target.Disconnects, Is.Empty);
        }
        List<FrameDependency> dependencies = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue);
        Assert.That(target.Chain.Container.Resolve<LeanProofStore>().TryGetInput(dependencies, out AggregationInput admitted), Is.True);
        NativeLeanProofVerifierTests.AssertBareStarkProof(admitted.RecursiveProofs[0].Proof.ToArray(), dependencies);
        Block block = await target.Chain.AddBlock(TestBlockchainUtil.AddBlockFlags.MayHaveExtraTx);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions, Has.Length.EqualTo(1));
            Assert.That(NativeLeanProofVerifier.Instance.VerifyRecursiveStark(Eip8288Dependencies.ComputeBlockDepsHash(block, FrameTxSigHash.ComputeValue),
                Eip8288Constants.AggregatedVk, block.StarkProof!), Is.True);
        }
        toTarget.Writer.TryComplete();
        toSource.Writer.TryComplete();
        await Task.WhenAll(targetReader, sourceReader);
    }

    [Test]
    public async Task Valid_negotiation_initializes_and_disposal_stops_protocol()
    {
        using Context context = await Create();
        InitializeEth(context);
        int initialized = 0;
        context.Handler.ProtocolInitialized += (_, _) => initialized++;
        context.Handler.Init();
        Receive(context, Status(context));
        Assert.That(initialized, Is.EqualTo(1));
        Assert.That(context.Transport.PeerCount, Is.EqualTo(1));
        context.Handler.Dispose();
        Receive(context, new CancelMessage(1));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Transport.PeerCount, Is.Zero);
            Assert.That(context.Disconnects, Is.Empty);
        }
    }
}
