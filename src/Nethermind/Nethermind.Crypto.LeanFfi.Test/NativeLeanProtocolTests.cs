// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using DotNetty.Buffers;
using Nethermind.Consensus.Scheduler;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.TxPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
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
        public bool Defer { get; set; }
        public CancellationToken Token { get; set; }
        private Func<Task>? _pending;
        public int Scheduled { get; private set; }
        public Task Completion { get; private set; } = Task.CompletedTask;
        public bool TryScheduleTask<T>(T request, Func<T, CancellationToken, Task> execute, TimeSpan? timeout = null)
            where T : notnull, IBackgroundTaskRequest<T>
        {
            Scheduled++;
            if (Defer) _pending = () => execute(request, Token);
            else Completion = execute(request, Token);
            return true;
        }

        public Task RunPending() => Completion = (_pending ?? throw new InvalidOperationException("No pending receive"))();
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

    private static async Task<Context> Create(ILeanProofVerifier? verifier = null, byte version = 1)
    {
        Scheduler scheduler = new();
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder =>
        {
            builder.AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
                .AddSingleton<IBackgroundTaskScheduler>(scheduler);
            if (verifier is not null) builder.AddSingleton(verifier);
        });
        ISession session = Substitute.For<ISession, ILeanBulkSession>();
        ((ILeanBulkSession)session).EnableLeanBulk().Returns(true);
        session.Node.Returns(new Node(TestItem.PublicKeyA, "127.0.0.1", 1000, true));
        session.HasAgreedCapability(Arg.Any<Capability>()).Returns(true);
        IProtocolHandler? handler = null;
        foreach (IProtocolHandlerFactory factory in chain.Container.Resolve<IProtocolHandlerFactory[]>())
            if (factory.ProtocolCode == "lean" && factory.TryCreate(session, version, out handler)) break;
        Assert.That(handler, Is.Not.Null);
        return new(chain, session, (LeanProtocolHandler)handler!, scheduler);
    }

    private static LeanStatusMessage Status(Context context) => new(context.Chain.BlockTree.ChainId,
        context.Chain.BlockTree.Genesis!.Hash!, Eip8288Constants.AggregatedVk.ToArray());

    private static void Receive<T>(Context context, T message, IZeroMessageSerializer<T> serializer) where T : Nethermind.Network.P2P.Messages.P2PMessage
    {
        if (message is LeanProofWrapperMessage wrapper)
        {
            ReceiveWrapper(context, wrapper.Wrapper);
            return;
        }
        IByteBuffer buffer = Unpooled.Buffer();
        try
        {
            serializer.Serialize(buffer, message);
            ZeroPacket packet = new(buffer) { PacketType = (byte)message.PacketType, Protocol = "lean" };
            context.Handler.HandleMessage(packet);
        }
        finally { buffer.Release(); }
    }

    private static void ReceiveWrapper(Context context, byte[] wrapper)
    {
        int size = LeanProofChunkMessage.DefaultChunkSize;
        int count = (wrapper.Length + size - 1) / size;
        ValueHash256 hash = ValueKeccak.Compute(wrapper);
        for (int index = 0; index < count; index++)
        {
            int offset = index * size;
            Receive(context, new LeanProofChunkMessage(hash, wrapper.Length, index, count, size,
                wrapper.AsMemory(offset, Math.Min(size, wrapper.Length - offset))), new LeanProofChunkMessageSerializer());
        }
    }

    [TestCase("chain")]
    [TestCase("genesis")]
    [TestCase("guest")]
    public async Task Handshake_mismatch_disables_lean_without_dropping_other_protocols(string mismatch)
    {
        using Context context = await Create();
        LeanStatusMessage valid = Status(context);
        LeanStatusMessage status = new(mismatch == "chain" ? valid.ChainId + 1 : valid.ChainId,
            mismatch == "genesis" ? TestItem.KeccakA : valid.GenesisHash,
            mismatch == "guest" ? new byte[32] : valid.VerificationKey);
        context.Handler.Init();
        Receive(context, status, new LeanStatusMessageSerializer());
        Receive(context, new LeanProofWrapperMessage([0]), new LeanProofWrapperMessageSerializer());
        context.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        Assert.That(context.Scheduler.Scheduled, Is.Zero);
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
        Assert.That(after.Chain.Container.Resolve<LeanReassemblyBudget>().RetainedAssemblies, Is.Zero);
    }

    [TestCase((byte)1)]
    public async Task Unnegotiated_capability_is_rejected(byte version)
    {
        using Context context = await Create(version: version);
        foreach (IProtocolHandlerFactory factory in context.Chain.Container.Resolve<IProtocolHandlerFactory[]>())
            if (factory.ProtocolCode == "lean")
            {
                Assert.That(factory.TryCreate(context.Session, 2, out IProtocolHandler? unsupported), Is.False);
                Assert.That(unsupported, Is.Null);
            }
        context.Session.HasAgreedCapability(Arg.Any<Capability>()).Returns(false);
        Receive(context, Status(context), new LeanStatusMessageSerializer());
        context.Session.Received().InitiateDisconnect(DisconnectReason.BreachOfProtocol, Arg.Any<string>());
    }

    [Test]
    public async Task Lean1_chunks_reassemble_a_real_recursive_wrapper_before_admission()
    {
        using Context source = await Create(version: 1);
        using Context target = await Create(version: 1);
        using LeanP2PCapabilityResolver resolver = new(source.Chain.BlockTree, source.Chain.SpecProvider);
        HashSet<Capability> capabilities = [];
        resolver.Resolve(capabilities);
        Assert.That(capabilities, Does.Contain(new Capability("lean", 1)).And.Not.Contain(new Capability("lean", 2)));
        source.Session.HasAgreedCapability(Arg.Any<Capability>())
            .Returns(call => call.Arg<Capability>().ProtocolCode == "lean" && call.Arg<Capability>().Version == 1);
        target.Session.HasAgreedCapability(Arg.Any<Capability>())
            .Returns(call => call.Arg<Capability>().ProtocolCode == "lean" && call.Arg<Capability>().Version == 1);
        TaskCompletionSource<int> complete = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int earlyAdmissions = 0;
        ((ILeanBulkSession)source.Session).DeliverLeanChunkAsync(Arg.Any<LeanProofChunkMessage>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                LeanProofChunkMessage chunk = call.Arg<LeanProofChunkMessage>();
                Receive(target, chunk, new LeanProofChunkMessageSerializer());
                if (chunk.Index < chunk.Count - 1 && target.Scheduler.Scheduled != 0)
                    Interlocked.Increment(ref earlyAdmissions);
                if (chunk.Index == chunk.Count - 1) complete.TrySetResult(chunk.Count);
                return new ValueTask<int>(LeanProofChunkMessage.HeaderSize + chunk.Data.Length);
            });
        source.Handler.Init();
        target.Handler.Init();
        Receive(source, Status(source), new LeanStatusMessageSerializer());
        Receive(target, Status(source), new LeanStatusMessageSerializer());
        Transaction transaction = NativeBlockProductionTests.CreateTransaction(source.Chain);
        source.Chain.Container.Resolve<LeanProofStore>().AddVerified(
            [NativeLeanProofVerifierTests.Dependency("sphincs"), NativeLeanProofVerifierTests.Dependency("stark")],
            [NativeLeanProofVerifierTests.Witness("sphincs"), NativeLeanProofVerifierTests.Witness("stark")], null);
        Assert.That(source.Chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));
        int chunks = await complete.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await target.Scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(source.Handler.ProtocolVersion, Is.EqualTo(1));
            Assert.That(chunks, Is.GreaterThan(1));
            Assert.That(earlyAdmissions, Is.Zero);
            Assert.That(target.Scheduler.Scheduled, Is.EqualTo(1));
            Assert.That(target.Chain.Container.Resolve<ILeanProofVerifier>(), Is.TypeOf<NativeLeanProofVerifier>());
            Assert.That(target.Chain.Container.Resolve<LeanProofStore>().Covers(transaction), Is.True);
            Assert.That(target.Chain.TxPool.TryGetPendingTransaction(transaction.Hash!.ValueHash256, out _), Is.True);
            target.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
    }

    private static byte[] CreateValidWrapper(Context context, ulong nonce = 0)
    {
        Transaction transaction = NativeBlockProductionTests.CreateTransaction(context.Chain);
        transaction.Nonce = nonce;
        transaction.Hash = null;
        transaction.ClearPreHash();
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        FrameDependency sphincs = NativeLeanProofVerifierTests.Dependency("sphincs");
        FrameDependency stark = NativeLeanProofVerifierTests.Dependency("stark");
        return MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(transaction)],
            Deps = Eip8288Dependencies.Canonicalize([sphincs, stark]),
            Mode = MempoolWrapper.ModeDirect,
            Proofs = Eip8288Dependencies.Canonicalize([sphincs, stark]).Select(dependency =>
                NativeLeanProofVerifierTests.Witness(dependency.Scheme == Eip8288Constants.LeanSphincsScheme ? "sphincs" : "stark")).ToList()
        }).Bytes;
    }

    [Test]
    public async Task Chunk_admission_and_disposal_share_the_global_receive_budget()
    {
        using Context context = await Create();
        Receive(context, Status(context), new LeanStatusMessageSerializer());
        context.Scheduler.Defer = true;
        LeanReassemblyBudget budget = context.Chain.Container.Resolve<LeanReassemblyBudget>();
        List<IDisposable> leases = [];
        try
        {
            for (int i = 0; i < LeanReassemblyBudget.MaxAssemblies; i++)
                leases.Add(budget.TryRent(1, static _ => { }, incomplete: false) ?? throw new InvalidOperationException("Could not fill receive budget"));
            byte[] wrapper = CreateValidWrapper(context);
            Receive(context, new LeanProofWrapperMessage(wrapper), new LeanProofWrapperMessageSerializer());
            Assert.That(context.Scheduler.Scheduled, Is.Zero);
            leases[^1].Dispose();
            Receive(context, new LeanProofWrapperMessage(wrapper), new LeanProofWrapperMessageSerializer());
            Assert.That(budget.RetainedAssemblies, Is.EqualTo(LeanReassemblyBudget.MaxAssemblies));
            await context.Scheduler.RunPending();
            Assert.That(context.Chain.TxPool.GetPendingTransactionsCount(), Is.EqualTo(1));
            Assert.That(budget.RetainedAssemblies, Is.EqualTo(LeanReassemblyBudget.MaxAssemblies - 1));
            ReceiveWrapper(context, CreateValidWrapper(context, nonce: 1));
            Assert.That(budget.RetainedAssemblies, Is.EqualTo(LeanReassemblyBudget.MaxAssemblies));
            context.Handler.Dispose();
            Assert.That(budget.RetainedAssemblies, Is.EqualTo(LeanReassemblyBudget.MaxAssemblies - 1));
        }
        finally
        {
            foreach (IDisposable lease in leases) lease.Dispose();
        }
        Assert.That(budget.RetainedBytes, Is.Zero);
        Assert.That(budget.RetainedAssemblies, Is.Zero);
    }

    [Test]
    public async Task Busy_peer_drops_a_third_stream_without_penalty_then_retries_after_admission()
    {
        using Context context = await Create();
        Receive(context, Status(context), new LeanStatusMessageSerializer());
        context.Scheduler.Defer = true;
        byte[] first = CreateValidWrapper(context);
        byte[] second = CreateValidWrapper(context, nonce: 1);
        byte[] third = CreateValidWrapper(context, nonce: 2);
        ReceiveWrapper(context, first);
        ReceiveWrapper(context, second);
        ReceiveWrapper(context, third);
        Assert.That(context.Scheduler.Scheduled, Is.EqualTo(1));
        Assert.That(context.Chain.Container.Resolve<LeanReassemblyBudget>().RetainedAssemblies, Is.EqualTo(2));
        await context.Scheduler.RunPending();
        Assert.That(context.Scheduler.Scheduled, Is.EqualTo(2));
        await context.Scheduler.RunPending();
        Assert.That(context.Chain.TxPool.GetPendingTransactionsCount(), Is.EqualTo(2));
        context.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        ReceiveWrapper(context, third);
        Assert.That(context.Scheduler.Scheduled, Is.EqualTo(3));
        await context.Scheduler.RunPending();
        Assert.That(context.Chain.TxPool.GetPendingTransactionsCount(), Is.EqualTo(3));
        Assert.That(context.Chain.Container.Resolve<LeanReassemblyBudget>().RetainedAssemblies, Is.Zero);
    }

    [Test]
    public async Task Gossip_retries_a_blocked_peer_without_resending_to_a_writable_peer()
    {
        using Context context = await Create();
        Result<Hash256[]> admitted = await context.Chain.Container.Resolve<ProofWrapperService>().AcceptAsync(CreateValidWrapper(context));
        Assert.That(admitted.IsSuccess, Is.True);
        LeanProofGossip gossip = context.Chain.Container.Resolve<LeanProofGossip>();
        int blockedAttempts = 0;
        int writableAttempts = 0;
        TaskCompletionSource retried = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gossip.AddPeer((_, _, _) =>
        {
            if (Interlocked.Increment(ref blockedAttempts) < 2) return new ValueTask<bool>(false);
            retried.TrySetResult();
            return new ValueTask<bool>(true);
        });
        gossip.AddPeer((_, _, _) => { Interlocked.Increment(ref writableAttempts); return new ValueTask<bool>(true); });
        await retried.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(1200);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(blockedAttempts, Is.EqualTo(2));
            Assert.That(writableAttempts, Is.EqualTo(1));
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
    public async Task Disposing_a_pending_receive_does_not_report_peer_failure()
    {
        using Context context = await Create();
        Receive(context, Status(context), new LeanStatusMessageSerializer());
        context.Scheduler.Defer = true;
        Receive(context, new LeanProofWrapperMessage([0]), new LeanProofWrapperMessageSerializer());
        context.Handler.Dispose();
        await context.Scheduler.RunPending();
        context.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
    }

    private sealed class BlockingVerifier : ILeanProofVerifier, IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _block = 1;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => _release.Set();
        public void EnsureAvailable() => NativeLeanProofVerifier.Instance.EnsureAvailable();
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 key, ReadOnlySpan<byte> witness)
        {
            if (Interlocked.Exchange(ref _block, 0) == 1)
            {
                Started.TrySetResult();
                _release.Wait();
            }
            return NativeLeanProofVerifier.Instance.VerifyLeanSphincs(in dataHash, in key, witness);
        }
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 key, ReadOnlySpan<byte> witness)
            => NativeLeanProofVerifier.Instance.VerifyLeanStark(in dataHash, in key, witness);
        public bool VerifyRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, ReadOnlySpan<byte> proof)
            => NativeLeanProofVerifier.Instance.VerifyRecursiveStark(in hash, key, proof);
        public byte[] ProveRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, AggregationInput input)
            => NativeLeanProofVerifier.Instance.ProveRecursiveStark(in hash, key, input);
        public void Dispose() { _release.Set(); _release.Dispose(); }
    }

    [Test]
    public async Task Receive_releases_pending_when_disposal_flag_wins_completion()
    {
        using BlockingVerifier verifier = new();
        using Context context = await Create(verifier);
        Receive(context, Status(context), new LeanStatusMessageSerializer());
        ReceiveWrapper(context, CreateValidWrapper(context));
        await verifier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        ReceiveWrapper(context, CreateValidWrapper(context, nonce: 1));
        LeanReassemblyBudget budget = context.Chain.Container.Resolve<LeanReassemblyBudget>();
        Assert.That(budget.RetainedAssemblies, Is.EqualTo(2));
        System.Reflection.FieldInfo disposed = typeof(LeanProtocolHandler).GetField("_disposed", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        try
        {
            // Pause Dispose after publishing its flag, before it acquires the receive lock.
            disposed.SetValue(context.Handler, 1);
            verifier.Release();
            await context.Scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(budget.RetainedBytes, Is.Zero);
                Assert.That(budget.RetainedAssemblies, Is.Zero);
                Assert.That(context.Chain.TxPool.GetPendingTransactionsCount(), Is.EqualTo(1));
            }
            context.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        finally
        {
            verifier.Release();
            disposed.SetValue(context.Handler, 0);
            context.Handler.Dispose();
        }
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Busy_admission_retries_the_retained_wrapper_and_releases_it_on_cancellation(bool cancel)
    {
        using BlockingVerifier verifier = new();
        using Context context = await Create(verifier);
        using CancellationTokenSource cancellation = new();
        context.Scheduler.Token = cancellation.Token;
        Receive(context, Status(context), new LeanStatusMessageSerializer());
        ProofWrapperService wrappers = context.Chain.Container.Resolve<ProofWrapperService>();
        Task<ProofWrapperAcceptance> occupying = wrappers.AcceptDetailedAsync(CreateValidWrapper(context));
        try
        {
            await verifier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            byte[] pending = CreateValidWrapper(context, nonce: 1);
            ReceiveWrapper(context, pending);
            LeanReassemblyBudget budget = context.Chain.Container.Resolve<LeanReassemblyBudget>();
            Assert.That(context.Scheduler.Completion.IsCompleted, Is.False, "local Busy must retain the completed wrapper for retry");
            Assert.That(budget.RetainedAssemblies, Is.EqualTo(1));
            if (cancel)
            {
                cancellation.Cancel();
                await context.Scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(budget.RetainedAssemblies, Is.Zero);
                Assert.That(context.Chain.TxPool.GetPendingTransactionsCount(), Is.Zero);
            }
            verifier.Release();
            Assert.That((await occupying.WaitAsync(TimeSpan.FromSeconds(10))).Status, Is.EqualTo(ProofWrapperAcceptanceStatus.Accepted));
            if (cancel)
            {
                context.Scheduler.Token = CancellationToken.None;
                ReceiveWrapper(context, pending);
            }
            await context.Scheduler.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.That(context.Chain.TxPool.GetPendingTransactionsCount(), Is.EqualTo(2));
            Assert.That(budget.RetainedAssemblies, Is.Zero);
            context.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        }
        finally
        {
            verifier.Release();
            await occupying.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private sealed class CancellingVerifier(CancellationTokenSource cancellation) : ILeanProofVerifier
    {
        public void EnsureAvailable() => NativeLeanProofVerifier.Instance.EnsureAvailable();
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 key, ReadOnlySpan<byte> witness)
        {
            bool verified = NativeLeanProofVerifier.Instance.VerifyLeanSphincs(in dataHash, in key, witness);
            cancellation.Cancel();
            return verified;
        }
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 key, ReadOnlySpan<byte> witness)
            => NativeLeanProofVerifier.Instance.VerifyLeanStark(in dataHash, in key, witness);
        public bool VerifyRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, ReadOnlySpan<byte> proof)
            => NativeLeanProofVerifier.Instance.VerifyRecursiveStark(in hash, key, proof);
        public byte[] ProveRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, AggregationInput input)
            => NativeLeanProofVerifier.Instance.ProveRecursiveStark(in hash, key, input);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task Scheduler_cancellation_before_or_during_real_verification_preserves_the_session(bool duringVerification)
    {
        using CancellationTokenSource cancellation = new();
        using Context context = await Create(duringVerification ? new CancellingVerifier(cancellation) : null);
        context.Scheduler.Token = cancellation.Token;
        Receive(context, Status(context), new LeanStatusMessageSerializer());
        byte[] wrapper = CreateValidWrapper(context);
        if (!duringVerification) cancellation.Cancel();
        Receive(context, new LeanProofWrapperMessage(wrapper), new LeanProofWrapperMessageSerializer());
        await context.Scheduler.Completion;
        context.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
        Assert.That(context.Chain.TxPool.GetPendingTransactionsCount(), Is.Zero);
        Assert.That(context.Chain.Container.Resolve<LeanReassemblyBudget>().RetainedAssemblies, Is.Zero);
        context.Scheduler.Token = CancellationToken.None;
        Receive(context, new LeanProofWrapperMessage(wrapper), new LeanProofWrapperMessageSerializer());
        await context.Scheduler.Completion;
        Assert.That(context.Chain.TxPool.GetPendingTransactionsCount(), Is.EqualTo(1));
    }

    [Test]
    public async Task Unknown_hash_only_wrapper_preserves_the_session()
    {
        using Context context = await Create();
        Receive(context, Status(context), new LeanStatusMessageSerializer());
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [new WrapperTransaction(TestItem.KeccakA)],
            Deps = [],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = []
        }).Bytes;
        Receive(context, new LeanProofWrapperMessage(wrapper), new LeanProofWrapperMessageSerializer());
        await context.Scheduler.Completion;
        context.Session.DidNotReceive().InitiateDisconnect(Arg.Any<DisconnectReason>(), Arg.Any<string>());
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
