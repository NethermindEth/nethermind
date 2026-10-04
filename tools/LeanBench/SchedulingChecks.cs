// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Autofac;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Threading;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Network.P2P.Subprotocols.Lean;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NSubstitute;

namespace Nethermind.Tools.LeanBench;

internal static class SchedulingChecks
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task RunAsync(string[] args)
    {
        string Value(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith("--" + name + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? fallback;
        string output = Path.GetFullPath(Value("out", "lean-scheduling-results"));
        bool requireFresh = bool.Parse(Value("require-fresh", "false"));
        List<object> rows = [await ReuseAsync()];
        Dictionary<string, object> builder = await BackgroundToBlockAsync();
        rows.Add(builder);
        Dictionary<string, object> responsive = await CompletedWrapperWhileBusyAsync();
        rows.Add(responsive);
        foreach (int busyTicks in new[] { 1, 3, 10 }) rows.Add(await SlowCompletionAsync(busyTicks, removeWhileBusy: false));
        Dictionary<string, object> removed = await SlowCompletionAsync(3, removeWhileBusy: true);
        rows.Add(removed);
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "scheduling.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            metadata = new
            {
                commandLine = Environment.CommandLine,
                utc = DateTimeOffset.UtcNow,
                sourceRevision = Value("source-revision", "working tree; see assembly hashes"),
                assemblySha256 = new[] { typeof(SchedulingChecks).Assembly.Location, typeof(LeanProofGossip).Assembly.Location,
                    typeof(ProofWrapperService).Assembly.Location }.ToDictionary(file => Path.GetFileName(file)!,
                    file => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))),
                backend = "Synthetic statement-checked scheduling backend; no native cryptography or performance measurements",
                pool = "Timer rows use controlled synthetic pool snapshots; the background-to-block row admits a real signed FrameTx to the production pool",
                clock = "Manual monotonic clock, one-second timer notifications; busyTicks is virtual elapsed time, not native proving duration",
                scope = "Actual wrapper selection/store, gossip scheduler and exact-parent builder reuse; no network, services or Lean prover changes"
            },
            results = rows
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Scheduling checks wrote {rows.Count} rows to {path}");
        Require(!requireFresh || !(bool)removed["staleDelivery"], "A removed transaction was delivered after proof completion; baseline recorded in scheduling.json");
        Require(!requireFresh || (int)builder["builderAdditionalProofCalls"] == 0,
            "BlockProcessor repeated proving for the completed background statement; baseline recorded in scheduling.json");
        Require(!requireFresh || (int)responsive["eligibleCompletedWrapperSendsWhileBlocked"] > 0,
            "A slow new proof blocked dissemination of eligible completed work; baseline recorded in scheduling.json");
    }

    private static async Task<Dictionary<string, object>> CompletedWrapperWhileBusyAsync()
    {
        using ControlledVerifier verifier = new();
        SnapshotPool pool = new();
        ManualTimeProvider time = new();
        using BasicTestBlockchain chain = await CreateAsync(pool, verifier, time);
        LeanProofStore store = chain.Container.Resolve<LeanProofStore>();
        FrameDependency firstDependency = Dependency("completed-wrapper-a");
        FrameDependency secondDependency = Dependency("blocked-wrapper-b");
        store.AddVerified([firstDependency, secondDependency], [[1], [1]], null);
        Transaction first = Transaction(firstDependency, 0);
        Transaction second = Transaction(secondDependency, 1);
        pool.Set(first);
        byte[] completedWrapper = Build(chain.Container.Resolve<ProofWrapperService>());
        ValueHash256 completedHash = ValueKeccak.Compute(completedWrapper);
        pool.Set(first, second);
        verifier.BlockNext();
        LeanProofGossip gossip = chain.Container.Resolve<LeanProofGossip>();
        int completedSends = 0;
        int changedSends = 0;
        int warmupDeclines = 0;
        gossip.AddPeer((_, hash, _) =>
        {
            if (hash == completedHash)
            {
                if (!verifier.IsBlocked)
                {
                    Interlocked.Increment(ref warmupDeclines);
                    return new(false);
                }
                Interlocked.Increment(ref completedSends);
            }
            else Interlocked.Increment(ref changedSends);
            return new(true);
        });
        List<Task> notifications = [];
        try
        {
            await time.TimerCreated.WaitAsync(Timeout);
            notifications.Add(Task.Run(() => time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1))));
            await verifier.Entered.Task.WaitAsync(Timeout);
            const int busyTicks = 3;
            for (int i = 0; i < busyTicks; i++)
            {
                notifications.Add(Task.Run(() => time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1))));
                await Task.Delay(10);
            }
            int completedSendsWhileBlocked = Volatile.Read(ref completedSends);
            int changedSendsWhileBlocked = Volatile.Read(ref changedSends);
            Require(changedSendsWhileBlocked == 0, "Incomplete work was published before proving returned");
            verifier.Release();
            await verifier.Completed.Task.WaitAsync(Timeout);
            await Task.WhenAll(notifications).WaitAsync(Timeout);
            for (int i = 0; i < 50 && Volatile.Read(ref changedSends) == 0; i++)
            {
                await Task.Run(() => time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1)));
                await Task.Delay(10);
            }
            Require(Volatile.Read(ref changedSends) == 1, "Completed changed selection was not delivered exactly once");
            Require(verifier.ProofCalls == 2 && verifier.MaxActive == 1, "Pending ticks repeated or overlapped the new proof");
            return new()
            {
                ["scenario"] = "eligible-completed-wrapper-during-new-proof",
                ["virtualBusySeconds"] = busyTicks,
                ["bothTransactionsRemainPending"] = true,
                ["peer"] = "Declines warmup A before proving is blocked; writable while blocked, so early AddPeer delivery cannot count as cadence progress",
                ["declinedWarmupDeliveries"] = warmupDeclines,
                ["eligibleCompletedWrapperSendsWhileBlocked"] = completedSendsWhileBlocked,
                ["incompleteWrapperSendsWhileBlocked"] = changedSendsWhileBlocked,
                ["completedNewWrapperSends"] = changedSends,
                ["totalProofCalls"] = verifier.ProofCalls,
                ["maxConcurrentProofCalls"] = verifier.MaxActive
            };
        }
        finally
        {
            verifier.Release();
            await Task.WhenAll(notifications).WaitAsync(Timeout);
            await gossip.DisposeAsync();
        }
    }

    private static async Task<Dictionary<string, object>> BackgroundToBlockAsync()
    {
        using ControlledVerifier verifier = new();
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
            .AddSingleton<ILeanProofVerifier>(verifier));
        FrameDependency dependency = Dependency("background-to-block");
        chain.Container.Resolve<LeanProofStore>().AddVerified([dependency], [[1]], null);
        TxFrame[] frames =
        [
            FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))
        ];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            NonceKeys = [UInt256.Zero],
            ChainId = chain.SpecProvider.ChainId,
            SenderAddress = TestItem.PrivateKeyB.Address,
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        Require(chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast) == AcceptTxResult.Accepted,
            "Production pool rejected the signed scheduling transaction");
        Build(chain.Container.Resolve<ProofWrapperService>());
        int backgroundCalls = verifier.ProofCalls;
        Block? first = await chain.BlockProducer.BuildBlock();
        Require(first is not null && first.Transactions.Length == 1, "Production builder omitted the signed scheduling transaction");
        Require(first!.Header.RecursiveStark is not null
            && first.Header.RecursiveStark.StarkProof.AsSpan().SequenceEqual(Eip8288Dependencies.ComputeBlockDepsHash(first).Bytes),
            "Production header proof does not match its actual body");
        int callsAfterFirst = verifier.ProofCalls;
        first.Header.RecursiveStark!.StarkProof[0] ^= 1;
        Block? second = await chain.BlockProducer.BuildBlock();
        Require(second is not null && second.Transactions.Length == 1
            && second.Header.RecursiveStark!.StarkProof.AsSpan().SequenceEqual(Eip8288Dependencies.ComputeBlockDepsHash(second).Bytes),
            "Returned header mutation contaminated retained proof ownership");
        Require(verifier.ProofCalls == callsAfterFirst, "An unchanged improvement pass repeated proving");
        return new()
        {
            ["scenario"] = "background-proof-to-production-blockprocessor",
            ["transaction"] = "Real secp256k1 signed FrameTx admitted to production pool; controlled dependency verifier",
            ["backgroundProofCalls"] = backgroundCalls,
            ["builderAdditionalProofCalls"] = callsAfterFirst - backgroundCalls,
            ["unchangedImprovementAdditionalProofCalls"] = verifier.ProofCalls - callsAfterFirst,
            ["actualBodyTransactions"] = second!.Transactions.Length,
            ["returnedProofMutationIsolated"] = true
        };
    }

    private static async Task<object> ReuseAsync()
    {
        using ControlledVerifier verifier = new();
        SnapshotPool pool = new();
        using BasicTestBlockchain chain = await CreateAsync(pool, verifier, new ManualTimeProvider());
        LeanProofStore store = chain.Container.Resolve<LeanProofStore>();
        ProofWrapperService service = chain.Container.Resolve<ProofWrapperService>();
        FrameDependency dependency = Dependency("reuse");
        store.AddVerified([dependency], [[1]], null);
        pool.Set(Transaction(dependency, 0));
        byte[] initial = Build(service);
        const int repeats = 120;
        for (int i = 0; i < repeats; i++) Require(Build(service).AsSpan().SequenceEqual(initial), "Unchanged wrapper changed");
        int callsAfterUnchanged = verifier.ProofCalls;
        pool.Set(Transaction(dependency, 1));
        byte[] changed = Build(service);
        Require(!changed.AsSpan().SequenceEqual(initial), "Changed transaction body did not change the wrapper");
        Require(verifier.ProofCalls == callsAfterUnchanged, "Changing only the body repeated dependency proving");
        Require(store.TryGetRecursiveProof([dependency], out byte[]? proof), "Completed proof was not retained");
        AggregationInput input = new() { RecursiveProofs = [new RecursiveProofInput([dependency], proof!)] };
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash([dependency]);
        int callsBeforeBuilder = verifier.ProofCalls;
        int verificationsBeforeBuilder = verifier.VerificationCalls;
        for (int i = 0; i < repeats; i++)
            Require(RecursiveStarkAggregator.Prove(input, verifier, hash).AsSpan().SequenceEqual(proof), "Builder changed the reused proof");
        Require(verifier.ProofCalls == callsBeforeBuilder, "Exact-parent builder reuse called the prover");
        int builderVerifications = verifier.VerificationCalls - verificationsBeforeBuilder;
        Require(builderVerifications == repeats, "Exact-parent reuse skipped statement verification");
        byte[] damaged = (byte[])proof!.Clone();
        damaged[0] ^= 1;
        bool rejected = false;
        try { RecursiveStarkAggregator.Prove(new() { RecursiveProofs = [new([dependency], damaged)] }, verifier, hash); }
        catch (InvalidOperationException) { rejected = true; }
        Require(rejected, "A damaged parent passed exact-proof reuse");
        return new
        {
            scenario = "unchanged-wrapper-body-change-and-exact-builder-reuse",
            repeatedWrapperSelections = repeats,
            proofCalls = verifier.ProofCalls,
            proofCallsAfterFirstSelection = callsAfterUnchanged,
            changedBodyAdditionalProofCalls = callsBeforeBuilder - callsAfterUnchanged,
            exactBuilderAttempts = repeats,
            exactBuilderAdditionalProofCalls = verifier.ProofCalls - callsBeforeBuilder,
            exactBuilderVerificationCalls = builderVerifications,
            damagedParentRejected = rejected
        };
    }

    private static async Task<Dictionary<string, object>> SlowCompletionAsync(int busyTicks, bool removeWhileBusy)
    {
        Console.WriteLine($"Scheduling controlled case: busy ticks={busyTicks}, removal={removeWhileBusy}");
        using ControlledVerifier verifier = new(block: true);
        SnapshotPool pool = new();
        ManualTimeProvider time = new();
        using BasicTestBlockchain chain = await CreateAsync(pool, verifier, time);
        Require(verifier.ProofCalls == 0, "Fixture setup invoked the blocked prover");
        FrameDependency dependency = Dependency($"slow:{busyTicks}:{removeWhileBusy}");
        chain.Container.Resolve<LeanProofStore>().AddVerified([dependency], [[1]], null);
        pool.Set(Transaction(dependency, 0));
        LeanProofGossip gossip = chain.Container.Resolve<LeanProofGossip>();
        int sends = 0;
        TaskCompletionSource delivered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        gossip.AddPeer((_, _, _) =>
        {
            Interlocked.Increment(ref sends);
            delivered.TrySetResult();
            return new(true);
        });
        long started = Stopwatch.GetTimestamp();
        List<Task> notifications = [];
        try
        {
            await time.TimerCreated.WaitAsync(Timeout);
            notifications.Add(Task.Run(() => time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1))));
            await verifier.Entered.Task.WaitAsync(Timeout);
            if (removeWhileBusy) pool.Set();
            for (int i = 0; i < busyTicks; i++)
            {
                notifications.Add(Task.Run(() => time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1))));
                await Task.Delay(10);
            }
            int callsWhileBlocked = verifier.ProofCalls;
            int sendsWhileBlocked = Volatile.Read(ref sends);
            verifier.Release();
            await verifier.Completed.Task.WaitAsync(Timeout).ConfigureAwait(false);
            await Task.WhenAll(notifications).WaitAsync(Timeout);
            for (int i = 0; i < 20 && !delivered.Task.IsCompleted; i++)
            {
                time.AdvanceAndFireTimer(TimeSpan.FromSeconds(1));
                await Task.Delay(10);
            }
            int sendsAtCompletion = Volatile.Read(ref sends);
            if (!removeWhileBusy)
            {
                Require(sendsAtCompletion == 1, "A completed eligible wrapper was not delivered once");
                time.AdvanceAndFireTimer(TimeSpan.FromSeconds(36));
                for (int i = 0; i < 50 && Volatile.Read(ref sends) < 2; i++) await Task.Delay(10);
                Require(Volatile.Read(ref sends) == 2, "An unchanged wrapper did not refresh after its bounded memo expired");
            }
            await gossip.DisposeAsync();
            Require(verifier.MaxActive == 1 && callsWhileBlocked == 1, "Timer ticks started overlapping proof calls");
            Require(verifier.ProofCalls == 1, "Repeated ticks re-proved an unchanged statement");
            return new()
            {
                ["scenario"] = removeWhileBusy ? "removed-selection-during-proof" : "slow-proof-coalescing",
                ["virtualBusySeconds"] = busyTicks,
                ["timerNotificationsWhileBlocked"] = busyTicks,
                ["proofCallsWhileBlocked"] = callsWhileBlocked,
                ["sendsWhileBlocked"] = sendsWhileBlocked,
                ["proofCallsAfterCompletion"] = verifier.ProofCalls,
                ["maxConcurrentProofCalls"] = verifier.MaxActive,
                ["completedProofCalls"] = verifier.CompletedCalls,
                ["sendsAfterCompletion"] = sendsAtCompletion,
                ["refreshSends"] = sends - sendsAtCompletion,
                ["staleDelivery"] = removeWhileBusy && sends != 0,
                ["harnessWallMilliseconds"] = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            };
        }
        finally
        {
            verifier.Release();
            await Task.WhenAll(notifications).WaitAsync(Timeout);
            await gossip.DisposeAsync();
        }
    }

    private static Task<BasicTestBlockchain> CreateAsync(SnapshotPool pool, ControlledVerifier verifier, ManualTimeProvider clock)
        => BasicTestBlockchain.Create(builder =>
        {
            builder.AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
                .AddSingleton<ITxPool>(pool.Instance)
                .AddSingleton<ILeanProofVerifier>(verifier);
            builder.ConfigureTestConfiguration(configuration => configuration.AddBlockOnStart = false);
            builder.RegisterType<LeanProofGossip>().WithParameter("timeProvider", clock).SingleInstance();
        });

    private static FrameDependency Dependency(string name) => new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(name), default);

    private static Transaction Transaction(FrameDependency dependency, ulong nonce) => new()
    {
        Type = TxType.FrameTx,
        ChainId = 1,
        SenderAddress = Address.Zero,
        Nonce = nonce,
        NonceKeys = [UInt256.Zero],
        Hash = new Hash256(ValueKeccak.Compute($"{dependency.DataHash}:{nonce}")),
        Frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas,
            UInt256.Zero, Eip8288Dependencies.Serialize([dependency]))]
    };

    private static byte[] Build(ProofWrapperService service)
    {
        Result<byte[]> result = service.BuildWrapper(skipEmpty: true);
        Require(result.IsSuccess, result.Error ?? "Wrapper build failed");
        return result.Data!;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SnapshotPool
    {
        private Transaction[] _transactions = [];
        public ITxPool Instance { get; } = Substitute.For<ITxPool>();
        public SnapshotPool()
        {
            Instance.GetPendingTransactions().Returns(_ => Volatile.Read(ref _transactions));
            Instance.GetPendingLightBlobTransactionsBySender().Returns(new Dictionary<AddressAsKey, Transaction[]>());
            Instance.TryGetPendingTransaction(Arg.Any<ValueHash256>(), out Arg.Any<Transaction?>()).Returns(call =>
            {
                ValueHash256 hash = call.ArgAt<ValueHash256>(0);
                Transaction? found = Array.Find(Volatile.Read(ref _transactions), transaction => transaction.Hash!.ValueHash256 == hash);
                call[1] = found;
                return found is not null;
            });
        }
        public void Set(params Transaction[] transactions) => Volatile.Write(ref _transactions, transactions);
    }

    private sealed class ControlledVerifier(bool block = false) : ILeanProofVerifier, IDisposable
    {
        private readonly ManualResetEventSlim _release = new(!block);
        private int _calls;
        private int _active;
        private int _maxActive;
        private int _completed;
        private int _verifications;
        public TaskCompletionSource Entered { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProofCalls => Volatile.Read(ref _calls);
        public int CompletedCalls => Volatile.Read(ref _completed);
        public int VerificationCalls => Volatile.Read(ref _verifications);
        public int MaxActive => Volatile.Read(ref _maxActive);
        public bool IsBlocked => Volatile.Read(ref _active) != 0 && !_release.IsSet;
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => witness.SequenceEqual(new byte[] { 1 });
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => false;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
        {
            Interlocked.Increment(ref _verifications);
            return proof.SequenceEqual(depsHash.Bytes) && aggregatedVk.SequenceEqual(Eip8288Constants.AggregatedVk);
        }
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            Interlocked.Increment(ref _calls);
            int active = Interlocked.Increment(ref _active);
            int observed = MaxActive;
            while (active > observed)
            {
                int previous = Interlocked.CompareExchange(ref _maxActive, active, observed);
                if (previous == observed) break;
                observed = previous;
            }
            Entered.TrySetResult();
            try
            {
                if (!_release.Wait(Timeout)) throw new TimeoutException("Controlled prover was not released");
                Interlocked.Increment(ref _completed);
                Completed.TrySetResult();
                return depsHash.ToByteArray();
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public void Release() => _release.Set();
        public void BlockNext()
        {
            Require(Volatile.Read(ref _active) == 0, "Cannot reset an active controlled prover");
            Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _release.Reset();
        }
        public void Dispose() => _release.Dispose();
    }
}
