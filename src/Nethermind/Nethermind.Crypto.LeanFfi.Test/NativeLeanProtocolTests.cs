// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Buffers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.Eip8288;
using Nethermind.TxPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Network;
using Nethermind.Network.P2P;
using Nethermind.Network.P2P.ProtocolHandlers;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Network.Rlpx;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Stats.Model;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

[NonParallelizable]
public class NativeLeanProtocolTests
{
    private sealed class Scheduler : IBackgroundTaskScheduler
    {
        public Task Completion { get; private set; } = Task.CompletedTask;
        public bool TryScheduleTask<T>(T request, Func<T, CancellationToken, Task> execute, TimeSpan? timeout = null)
            where T : notnull, IBackgroundTaskRequest<T>
        {
            Completion = execute(request, CancellationToken.None);
            return true;
        }
    }

    private sealed class Context(BasicTestBlockchain chain, ISession session, LeanProtocolHandler handler, Scheduler scheduler) : IDisposable
    {
        public BasicTestBlockchain Chain { get; } = chain;
        public ISession Session { get; } = session;
        public LeanProtocolHandler Handler { get; } = handler;
        public Scheduler Scheduler { get; } = scheduler;
        public void Dispose()
        {
            Handler.Dispose();
            Chain.Dispose();
        }
    }

    private static async Task<Context> Create()
    {
        Scheduler scheduler = new();
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
            .AddSingleton<IBackgroundTaskScheduler>(scheduler));
        ISession session = Substitute.For<ISession>();
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 1000, true));
        session.HasAgreedCapability(Arg.Any<Capability>()).Returns(true);
        IProtocolHandlerFactory factory = chain.Container.Resolve<IProtocolHandlerFactory[]>().First(f => f.ProtocolCode == "lean");
        Assert.That(factory.TryCreate(session, 1, out IProtocolHandler? handler), Is.True);
        return new(chain, session, (LeanProtocolHandler)handler!, scheduler);
    }

    private static LeanStatusMessage Status(Context context) => new(context.Chain.BlockTree.ChainId,
        context.Chain.BlockTree.Genesis!.Hash!, Eip8288Constants.AggregatedVk.ToArray());

    private static void Receive<T>(Context context, T message, IZeroMessageSerializer<T> serializer) where T : Nethermind.Network.P2P.Messages.P2PMessage
    {
        IByteBuffer buffer = Unpooled.Buffer();
        try
        {
            serializer.Serialize(buffer, message);
            ZeroPacket packet = new(buffer) { PacketType = (byte)message.PacketType, Protocol = "lean" };
            context.Handler.HandleMessage(packet);
        }
        finally { buffer.Release(); }
    }

    [TestCase("chain")]
    [TestCase("genesis")]
    [TestCase("guest")]
    public async Task Handshake_rejects_another_chain_or_guest(string mismatch)
    {
        using Context context = await Create();
        LeanStatusMessage valid = Status(context);
        LeanStatusMessage status = new(mismatch == "chain" ? valid.ChainId + 1 : valid.ChainId,
            mismatch == "genesis" ? TestItem.KeccakA : valid.GenesisHash,
            mismatch == "guest" ? new byte[32] : valid.VerificationKey);
        Receive(context, status, new LeanStatusMessageSerializer());
        context.Session.Received().InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
    }

    [Test]
    public async Task Wrappers_require_handshake_and_invalid_proofs_disconnect()
    {
        using Context before = await Create();
        Receive(before, new LeanProofWrapperMessage([0]), new LeanProofWrapperMessageSerializer());
        before.Session.Received().InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
        using Context after = await Create();
        Receive(after, Status(after), new LeanStatusMessageSerializer());
        Receive(after, new LeanProofWrapperMessage([0]), new LeanProofWrapperMessageSerializer());
        await after.Scheduler.Completion;
        after.Session.Received().InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
    }

    [Test]
    public async Task Unnegotiated_capability_is_rejected()
    {
        using Context context = await Create();
        context.Session.HasAgreedCapability(Arg.Any<Capability>()).Returns(false);
        Receive(context, Status(context), new LeanStatusMessageSerializer());
        context.Session.Received().InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
    }

    [Test]
    public async Task Two_negotiated_peers_transfer_real_proofs_and_transactions()
    {
        using Context source = await Create();
        using Context target = await Create();
        Receive(source, Status(source), new LeanStatusMessageSerializer());
        Receive(target, Status(source), new LeanStatusMessageSerializer());
        Transaction transaction = NativeBlockProductionTests.CreateTransaction(source.Chain);
        FrameDependency sphincs = NativeLeanProofVerifierTests.Dependency("sphincs");
        FrameDependency stark = NativeLeanProofVerifierTests.Dependency("stark");
        source.Chain.Container.Resolve<LeanProofStore>().AddVerified([sphincs,stark],
            [NativeLeanProofVerifierTests.Witness("sphincs"),NativeLeanProofVerifierTests.Witness("stark")],null);
        Assert.That(source.Chain.TxPool.SubmitTx(transaction,TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        Result<byte[]> wrapper = source.Chain.Container.Resolve<ProofWrapperService>().BuildWrapper(skipEmpty:true);
        Assert.That(wrapper.IsSuccess,Is.True,wrapper.Error);
        Receive(target,new LeanProofWrapperMessage(wrapper.Data!),new LeanProofWrapperMessageSerializer());
        await target.Scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(target.Chain.Container.Resolve<LeanProofStore>().Covers(transaction),Is.True);
            Assert.That(target.Chain.TxPool.TryGetPendingTransaction(transaction.Hash!.ValueHash256,out _),Is.True);
            target.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(),Arg.Any<string>());
        }
    }

    [Test]
    public void Wire_decoding_bounds_before_allocating()
    {
        IByteBuffer buffer = Unpooled.WrappedBuffer(new byte[LeanProofStore.MaxWrapperBytes + 1]);
        try { Assert.Throws<RlpException>(() => new LeanProofWrapperMessageSerializer().Deserialize(buffer)); }
        finally { buffer.Release(); }
    }

    [Test]
    public async Task Valid_negotiation_initializes_and_disposal_stops_protocol()
    {
        using Context context = await Create();
        int initialized = 0;
        context.Handler.ProtocolInitialized += (_, _) => initialized++;
        context.Handler.Init();
        Receive(context, Status(context), new LeanStatusMessageSerializer());
        Assert.That(initialized, Is.EqualTo(1));
        context.Handler.Dispose();
        Receive(context, new LeanProofWrapperMessage([0]), new LeanProofWrapperMessageSerializer());
        context.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }
}
