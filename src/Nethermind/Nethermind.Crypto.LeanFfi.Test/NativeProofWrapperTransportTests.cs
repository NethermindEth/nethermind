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
using Nethermind.Network.Contract.P2P;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.Messages;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols;
using Nethermind.Network.P2P.Subprotocols.Eth.V69.Messages;
using Nethermind.Network.P2P.Subprotocols.Eth.V73;
using Nethermind.Network.P2P.Subprotocols.Eth.V73.Messages;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

/// <summary>EIP-8437 <c>eth/73</c> wrapper propagation over production DI wiring with the native verifier.</summary>
[NonParallelizable]
public class NativeProofWrapperTransportTests
{
    private sealed record WireMessage(int PacketType, byte[] Body);

    private sealed class Context(BasicTestBlockchain chain, ISession session, Eth73ProtocolHandler handler) : IDisposable
    {
        public BasicTestBlockchain Chain { get; } = chain;
        public ISession Session { get; } = session;
        public Eth73ProtocolHandler Handler { get; } = handler;
        public Action<WireMessage>? Post { get; set; }
        public ConcurrentQueue<string> Disconnects { get; } = new();
        public ProofWrapperTransport Transport => Chain.Container.Resolve<ProofWrapperTransport>();

        public void Dispose()
        {
            Handler.Dispose();
            Chain.Dispose();
        }
    }

    private static async Task<Context> Create()
    {
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
            builder.AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance)));
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 1000, true));
        session.RemoteNodeId.Returns(TestItem.PublicKeyA);
        session.HasAgreedCapability(Arg.Any<Capability>())
            .Returns(call => call.Arg<Capability>().ProtocolCode == Protocol.Eth && call.Arg<Capability>().Version == EthVersions.Eth73);
        IProtocolHandler? handler = null;
        foreach (IProtocolHandlerFactory factory in chain.Container.Resolve<IProtocolHandlerFactory[]>())
            if (factory.ProtocolCode == Protocol.Eth && factory.TryCreate(session, EthVersions.Eth73, out handler)) break;
        Assert.That(handler, Is.TypeOf<Eth73ProtocolHandler>());
        Context context = new(chain, session, (Eth73ProtocolHandler)handler!);
        session.When(s => s.InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>()))
            .Do(call => context.Disconnects.Enqueue(call.Arg<string>()));
        Route<StatusMessage69>(context);
        Route<NewProofWrapperHashesMessage>(context);
        Route<GetProofWrappersMessage>(context);
        Route<ProofWrappersMessage>(context);
        return context;
    }

    private static void Route<T>(Context context) where T : P2PMessage =>
        context.Session.When(session => session.DeliverMessage(Arg.Any<T>())).Do(call =>
        {
            T message = call.Arg<T>();
            IByteBuffer buffer = context.Chain.Container.Resolve<IMessageSerializationService>().ZeroSerialize(message);
            try
            {
                buffer.SkipBytes(1);
                byte[] body = new byte[buffer.ReadableBytes];
                buffer.ReadBytes(body);
                context.Post?.Invoke(new(message.PacketType, body));
            }
            finally { buffer.SafeRelease(); }
        });

    /// <summary>Delivers every message one side sends to the other side's handler, in order, off the sender's thread.</summary>
    private static Task Connect(Context from, Context to, Channel<WireMessage> queue)
    {
        from.Post = message => queue.Writer.TryWrite(message);
        return Task.Run(async () =>
        {
            await foreach (WireMessage message in queue.Reader.ReadAllAsync()) Receive(to, message);
        });
    }

    private static void Receive(Context context, WireMessage message)
    {
        ZeroPacket packet = new(Unpooled.WrappedBuffer(message.Body)) { PacketType = (byte)message.PacketType, Protocol = Protocol.Eth };
        try { context.Handler.HandleMessage(packet); }
        finally { packet.SafeRelease(); }
    }

    /// <summary>Initializes the handler and returns the Status it sent.</summary>
    private static WireMessage Init(Context context)
    {
        WireMessage? status = null;
        context.Post = message => status ??= message;
        context.Handler.Init();
        context.Post = null;
        Assert.That(status, Is.Not.Null);
        return status!;
    }

    private static WireMessage Encode<T>(Context context, T message) where T : P2PMessage
    {
        WireMessage? encoded = null;
        context.Post = wire => encoded = wire;
        context.Session.DeliverMessage(message);
        context.Post = null;
        return encoded!;
    }

    // Wrapper messages are eth messages: before Status they break the eth handshake like any other, and register no peer.
    [Test]
    public async Task Wrapper_messages_before_status_break_the_handshake()
    {
        using Context context = await Create();
        Init(context);
        WireMessage request = Encode(context, new GetProofWrappersMessage(1, true, [TestItem.KeccakA.ValueHash256]));
        using (Assert.EnterMultipleScope())
        {
            Assert.Throws<SubprotocolException>(() => Receive(context, request));
            Assert.That(context.Transport.PeerCount, Is.Zero);
        }
    }

    [Test]
    public async Task Real_recursive_wrapper_is_fetched_whole_and_validated_before_admission()
    {
        byte[] proof = NativeLeanProofVerifierTests.MixedProof();
        Assert.That(proof.Length, Is.LessThanOrEqualTo(Eip8288Constants.MaxAggregateProofBytes), "the pinned guest proof fits MAX_AGGREGATE_PROOF_BYTES");
        using Context source = await Create();
        using Context target = await Create();
        Channel<WireMessage> toTarget = Channel.CreateUnbounded<WireMessage>(), toSource = Channel.CreateUnbounded<WireMessage>();
        Task targetReader = Connect(source, target, toTarget), sourceReader = Connect(target, source, toSource);
        source.Handler.Init();
        target.Handler.Init();
        Transaction transaction = NativeBlockProductionTests.CreateTransaction(source.Chain);
        source.Chain.Container.Resolve<LeanProofStore>().AddVerified(
            [NativeLeanProofVerifierTests.Dependency("sphincs"), NativeLeanProofVerifierTests.Dependency("stark")], null, proof);
        Assert.That(source.Chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        await using ProofWrapperGossip gossip = source.Chain.Container.Resolve<ProofWrapperGossip>();
        gossip.Start();

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        while (!target.Chain.TxPool.TryGetPendingTransaction(transaction.Hash!.ValueHash256, out _)) await Task.Delay(50, timeout.Token);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.Chain.Container.Resolve<ILeanProofVerifier>(), Is.TypeOf<ProductionProofCache>());
            Assert.That(target.Chain.Container.Resolve<LeanProofStore>().Covers(transaction), Is.True);
            Assert.That(target.Transport.StoredObjects, Is.EqualTo(1), "the validated wrapper is offered onward");
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
    public async Task Eth73_status_registers_the_peer_and_disposal_removes_it()
    {
        using Context context = await Create();
        Receive(context, Init(context));
        Assert.That(context.Transport.PeerCount, Is.EqualTo(1));
        context.Handler.Dispose();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(context.Transport.PeerCount, Is.Zero);
            Assert.That(context.Disconnects, Is.Empty);
        }
    }
}
