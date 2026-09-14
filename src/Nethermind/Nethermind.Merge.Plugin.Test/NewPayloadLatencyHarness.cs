// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Linq;
using System.Reflection;
using System.Runtime;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Config;
using Nethermind.Consensus;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Core.Test.Modules;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.JsonRpc.Modules;
using Nethermind.Merge.Plugin.BlockProduction;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Json;
using Nethermind.Specs.Forks;
using Nethermind.Synchronization.ParallelSync;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>
/// Replays the benchmarkoor "compute" cadence against an in-process node: the head stays pinned while sibling
/// setup/test pairs stream in, and every engine call is timed together with the GC pauses it absorbed.
/// </summary>
/// <remarks>
/// Knobs come from the environment so a run can be reshaped without recompiling: <c>NP_SENDERS</c> (transactions
/// per block, default 9523), <c>NP_ITERATIONS</c> (sibling pairs, default 40), <c>NP_SLEEP_MS</c> (pause between
/// tests, default 200), <c>NP_JSON</c> (1 routes newPayload through the JSON-RPC service, 0 calls the module),
/// <c>NP_OUT</c> (a file that receives a copy of the report), <c>NP_BALLAST_MB</c> (live heap to add so background GCs
/// take realistic time), <c>NP_BGC_BEFORE_TEST</c> (1 starts a background gen2 right before each test payload) and
/// <c>NP_ALLOC_PROFILE</c> (1 samples allocations by type inside the timed calls).
/// Run with <c>DOTNET_gcServer=1 DOTNET_GCHeapCount=6</c> to match the benchmark container.
/// </remarks>
[TestFixture]
[Explicit("Latency harness for engine_newPayloadV5 under the benchmarkoor cadence; run by hand.")]
public class NewPayloadLatencyHarness : BaseEngineModuleTests
{
    private static readonly Address Precompile0x100 = new("0x0000000000000000000000000000000000000100");
    private static readonly UInt256 SenderFunding = 1_000_000_000_000_000; // 0.001 ether: covers value 1 plus 21000 gas at 1 gwei
    private const ulong GasLimit = 1_000_000_000_000;
    private const long TransferGas = 21_000;

    private static readonly Lazy<System.IO.TextWriter?> _sink = new(() =>
    {
        string? path = Environment.GetEnvironmentVariable("NP_OUT");
        return path is null ? null : new System.IO.StreamWriter(path, append: true) { AutoFlush = true };
    });

    /// <summary>The test host only surfaces stdout for failed tests, so results also go to the file named by NP_OUT.</summary>
    private static void Log(string line = "")
    {
        Console.WriteLine(line);
        _sink.Value?.WriteLine(line);
    }

    private static int Knob(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out int value) ? value : fallback;

    [Test]
    public async Task Sibling_pairs_under_pinned_head()
    {
        int senders = Knob("NP_SENDERS", 9523);
        int iterations = Knob("NP_ITERATIONS", 40);
        int sleepMs = Knob("NP_SLEEP_MS", 200);
        bool viaJson = Knob("NP_JSON", 1) != 0;
        bool bgcBeforeTest = Knob("NP_BGC_BEFORE_TEST", 0) != 0;
        bool allocProfile = Knob("NP_ALLOC_PROFILE", 0) != 0;
        int ballastMb = Knob("NP_BALLAST_MB", 0);

        using GcEventListener gcEvents = new(allocProfile);
        using LatencyModeSampler regionSampler = new();
        // Production GC defaults are restored below: the merge test chain switches the no-GC region and forced GCs
        // off, and its static sync mode never reports the synced state that the GC strategy requires.
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance,
            configurer: builder => builder
                .WithGenesisPostProcessor((block, _) => block.Header.GasLimit = GasLimit)
                .AddSingleton<ILogManager>(new TestLogManager(LogLevel.Info))
                .Intercept<IInitConfig>(initConfig => initConfig.DisableGcOnNewPayload = true)
                .AddSingleton<ISyncModeSelector>(new StaticSelector(SyncMode.WaitingForBlock)));
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Log($"ServerGC={GCSettings.IsServerGC} Latency={GCSettings.LatencyMode} heap={System.GC.GetGCMemoryInfo().HeapSizeBytes / 1024 / 1024} MB senders={senders} iterations={iterations} sleep={sleepMs}ms json={viaJson} bgcBeforeTest={bgcBeforeTest} allocProfile={allocProfile}");

        using (chain.Container.Resolve<global::Nethermind.Merge.Plugin.GC.GCKeeper>().TryStartNoGCRegion())
        {
            Log($"GCKeeper.TryStartNoGCRegion -> mode={GCSettings.LatencyMode} (NoGCRegion expected)");
        }

        // Ballast stands in for a large live heap: background GC mark time grows with the object count.
        List<byte[]> ballast = new(ballastMb * 12_000);
        for (long bytes = 0; bytes < (long)ballastMb * 1024 * 1024; bytes += 88) ballast.Add(new byte[64]);
        if (ballastMb > 0) System.GC.Collect(2, GCCollectionMode.Forced, blocking: true);
        Log($"ballast {ballastMb} MB in {ballast.Count} objects; heap {System.GC.GetTotalMemory(false) / 1024 / 1024} MB");

        Block baseBlock = chain.BlockTree.Head!;
        ulong chainId = chain.SpecProvider.ChainId;

        PrivateKey[] keys = new PrivateKey[senders];
        for (int i = 0; i < senders; i++)
        {
            keys[i] = new PrivateKey(Keccak.Compute($"np-latency-sender-{i}").BytesToArray());
        }

        // Setup block: the genesis account funds every sender. Test block: every sender sends 1 wei to 0x100.
        Transaction[] fundingTxs = new Transaction[senders];
        for (int i = 0; i < senders; i++)
        {
            fundingTxs[i] = Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(chainId).WithNonce((ulong)i)
                .WithTo(keys[i].Address).WithValue(SenderFunding).WithGasLimit(1_000_000)
                .WithMaxFeePerGas(Unit.GWei).WithMaxPriorityFeePerGas(Unit.GWei)
                .SignedAndResolved(chain.EthereumEcdsa, TestItem.PrivateKeyA).TestObject;
        }
        Transaction[] transferTxs = new Transaction[senders];
        for (int i = 0; i < senders; i++)
        {
            transferTxs[i] = Build.A.Transaction.WithType(TxType.EIP1559).WithChainId(chainId).WithNonce(0)
                .WithTo(Precompile0x100).WithValue(UInt256.One).WithGasLimit(TransferGas)
                .WithMaxFeePerGas(Unit.GWei).WithMaxPriorityFeePerGas(Unit.GWei)
                .SignedAndResolved(chain.EthereumEcdsa, keys[i]).TestObject;
        }

        ListTxSource txSource = new();
        IBlockProducerEnv env = chain.Container.Resolve<IBlockProducerEnvFactory>().CreatePersistent();
        PostMergeBlockProducer producer = chain.Container.Resolve<PostMergeBlockProducerFactory>().Create(env, txSource);

        await using IContainer rpcContainer = new ContainerBuilder()
            .AddModule(new TestNethermindModule(new JsonRpcConfig { EnabledModules = [typeof(IEngineRpcModule).GetCustomAttribute<RpcModuleAttribute>()!.ModuleType] }))
            .RegisterBoundedJsonRpcModule<IEngineRpcModule, AutoRpcModuleFactory<IEngineRpcModule>>(1, new JsonRpcConfig().Timeout)
            .AddScoped<IEngineRpcModule>(rpc)
            .Build();
        IJsonRpcService jsonRpcService = rpcContainer.Resolve<IJsonRpcService>();
        EthereumJsonSerializer serializer = new();

        List<CallSample> setupSamples = [];
        List<CallSample> testSamples = [];
        Hash256 baseHash = baseBlock.Hash!;
        string testJson = string.Empty;
        ulong baseTimestamp = baseBlock.Timestamp;

        for (int iteration = 0; iteration < iterations; iteration++)
        {
            ulong slot = (ulong)(2 * iteration + 1);
            // Blocks are produced out of band (benchmarkoor holds prebuilt payloads); the test block can only be built
            // once its setup sibling has been processed, so production of it sits between the two timed calls.
            txSource.Transactions = fundingTxs;
            Block setupBlock = await Produce(producer, env, baseBlock.Header, baseTimestamp + 12 * slot, slot);
            Assert.That(setupBlock.Transactions.Length, Is.EqualTo(senders), "setup block dropped transactions");
            string setupJson = serializer.Serialize(ExecutionPayloadV4.Create(setupBlock));

            await Task.Delay(sleepMs);

            await Fcu(rpc, baseHash);
            gcEvents.InsideTimedCall = true;
            setupSamples.Add(await TimedNewPayload(rpc, jsonRpcService, setupBlock, setupJson, viaJson, gcEvents));
            gcEvents.InsideTimedCall = false;
            await Fcu(rpc, setupBlock.Hash!);

            txSource.Transactions = transferTxs;
            Block testBlock = await Produce(producer, env, setupBlock.Header, baseTimestamp + 12 * (slot + 1), slot + 1);
            Assert.That(testBlock.Transactions.Length, Is.EqualTo(senders), "test block dropped transactions");
            testJson = serializer.Serialize(ExecutionPayloadV4.Create(testBlock));

            if (bgcBeforeTest)
            {
                // A background gen2 in flight is what a busy node hands the next payload; start one and call at once.
                System.GC.Collect(2, GCCollectionMode.Forced, blocking: false);
            }
            gcEvents.InsideTimedCall = true;
            testSamples.Add(await TimedNewPayload(rpc, jsonRpcService, testBlock, testJson, viaJson, gcEvents));
            gcEvents.InsideTimedCall = false;
            await Fcu(rpc, testBlock.Hash!);

            CallSample s = setupSamples[^1];
            CallSample t = testSamples[^1];
            Log($"[{iteration,3}] setup {s.Format()} | test {t.Format()} | heap {System.GC.GetTotalMemory(false) / 1024 / 1024,6} MB");
            foreach ((string label, CallSample sample) in new[] { ("setup", s), ("test", t) })
            {
                foreach ((DateTime start, DateTime end) in regionSampler.RegionWindows(sample.StartUtc, sample.EndUtc))
                {
                    Log($"        {label}: no-GC region +{(start - sample.StartUtc).TotalMilliseconds,6:F1} ms for {(end - start).TotalMilliseconds,6:F1} ms");
                }
                foreach (GcRecord gc in gcEvents.Records.Where(r => r.Start <= sample.EndUtc && r.End >= sample.StartUtc).OrderBy(r => r.Start))
                {
                    Log($"        {label}: GC gen{gc.Depth} {gc.ReasonName} {gc.TypeName} at +{(gc.Start - sample.StartUtc).TotalMilliseconds,6:F1} ms, ran {(gc.End - gc.Start).TotalMilliseconds,6:F1} ms, suspended {gc.SuspendedMs,6:F1} ms");
                }
            }
        }

        Log();
        Log("setup newPayload: " + Summary(setupSamples));
        Log("test  newPayload: " + Summary(testSamples));
        Log();
        Log("GCs observed (UTC time, gen, reason, type, ms suspended, inside call):");
        List<(DateTime start, DateTime end, string label)> windows = [];
        windows.AddRange(setupSamples.Select(s => (s.StartUtc, s.EndUtc, "setup")));
        windows.AddRange(testSamples.Select(s => (s.StartUtc, s.EndUtc, "test")));
        foreach (GcRecord gc in gcEvents.Records.OrderBy(r => r.Start))
        {
            string inside = windows.FirstOrDefault(w => gc.Start <= w.end && gc.End >= w.start).label ?? "-";
            Log($"  {gc.Start:HH:mm:ss.fff} gen{gc.Depth} {gc.ReasonName,-18} {gc.TypeName,-14} {gc.SuspendedMs,8:F1} ms  {inside}");
        }

        if (allocProfile)
        {
            Log();
            long sampled = 0;
            foreach (KeyValuePair<string, long> kv in gcEvents.AllocatedByType) sampled += kv.Value;
            Log($"Sampled allocations inside timed calls: {sampled / 1024 / 1024} MB across {gcEvents.AllocatedByType.Count} types; top 40:");
            foreach ((string type, long bytes) in gcEvents.AllocatedByType.OrderByDescending(kv => kv.Value).Take(40))
            {
                Log($"  {bytes / 1024.0 / 1024.0,9:F1} MB {100.0 * bytes / sampled,5:F1}%  {type}");
            }
        }

        Log();
        Log("Phase costs for one test payload (single-threaded where the production path is):");
        await PhaseBreakdown(chain, serializer, testJson);
        System.GC.KeepAlive(ballast);
    }

    private static async Task Fcu(IEngineRpcModule rpc, Hash256 head)
    {
        ResultWrapper<ForkchoiceUpdatedV1Result> result = await rpc.engine_forkchoiceUpdatedV4(new ForkchoiceStateV1(head, Keccak.Zero, Keccak.Zero));
        Assert.That(result.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid), result.Result.Error);
    }

    private static async Task<Block> Produce(PostMergeBlockProducer producer, IBlockProducerEnv env, BlockHeader parent, ulong timestamp, ulong slot)
    {
        // The producer silently yields null until the parent state is visible to its read-only world state.
        long waitStart = Stopwatch.GetTimestamp();
        while (!env.ReadOnlyStateProvider.HasStateForBlock(parent))
        {
            if (Stopwatch.GetElapsedTime(waitStart) > TimeSpan.FromSeconds(10)) Assert.Fail($"state for block {parent.Number} ({parent.StateRoot}) never became visible");
            await Task.Delay(5);
        }
        double waitedMs = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        if (waitedMs > 20) Log($"  waited {waitedMs:F0} ms for state of block {parent.Number}");
        PayloadAttributes attributes = new()
        {
            Timestamp = timestamp,
            PrevRandao = TestItem.KeccakA,
            SuggestedFeeRecipient = TestItem.AddressC,
            Withdrawals = [],
            ParentBeaconBlockRoot = Keccak.Zero,
            SlotNumber = slot,
        };
        // The merge sealer is a no-op for PoS blocks and the processor has already hashed the block, so skip the seal step.
        Block? block = await producer.BuildBlock(parent, null, attributes, IBlockProducer.Flags.DontSeal);
        Assert.That(block, Is.Not.Null, "block production failed");
        return block!;
    }

    private static async Task<CallSample> TimedNewPayload(IEngineRpcModule rpc, IJsonRpcService service, Block block, string payloadJson, bool viaJson, GcEventListener gcEvents)
    {
        CallSample sample = new() { Gen0 = System.GC.CollectionCount(0), Gen1 = System.GC.CollectionCount(1), Gen2 = System.GC.CollectionCount(2) };
        long allocated = System.GC.GetTotalAllocatedBytes(precise: false);
        TimeSpan pause = System.GC.GetTotalPauseDuration();
        sample.StartUtc = DateTime.UtcNow;
        long start = Stopwatch.GetTimestamp();

        if (viaJson)
        {
            // Mirrors the processor: the body is parsed to a document first, then the service binds typed parameters.
            using JsonDocument document = JsonDocument.Parse($"[{payloadJson},[],\"{Keccak.Zero}\",[]]");
            sample.ParseMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            JsonRpcRequest request = new() { JsonRpc = "2.0", Method = "engine_newPayloadV5", Params = document.RootElement, Id = 1 };
            using JsonRpcContext context = new(RpcEndpoint.Http);
            using JsonRpcResponse response = await service.SendRequestAsync(request, context);
            if (response is JsonRpcErrorResponse error) Assert.Fail($"newPayload failed: {error.Error?.Message}");
            PayloadStatusV1 status = ((ResultWrapper<PayloadStatusV1>)response).Data;
            Assert.That(status.Status, Is.EqualTo(PayloadStatus.Valid), status.ValidationError);
        }
        else
        {
            ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV5(ExecutionPayloadV4.Create(block), [], Keccak.Zero, []);
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid), result.Data.ValidationError ?? result.Result.Error);
        }

        sample.TotalMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        sample.EndUtc = DateTime.UtcNow;
        sample.PauseMs = (System.GC.GetTotalPauseDuration() - pause).TotalMilliseconds;
        sample.AllocatedMb = (System.GC.GetTotalAllocatedBytes(precise: false) - allocated) / 1024.0 / 1024.0;
        sample.Gen0 = System.GC.CollectionCount(0) - sample.Gen0;
        sample.Gen1 = System.GC.CollectionCount(1) - sample.Gen1;
        sample.Gen2 = System.GC.CollectionCount(2) - sample.Gen2;
        return sample;
    }

    private static async Task PhaseBreakdown(MergeTestBlockchain chain, EthereumJsonSerializer serializer, string payloadJson)
    {
        Log($"  payload json      {payloadJson.Length / 1024,8} KB");
        for (int round = 0; round < 3; round++)
        {
            long t0 = Stopwatch.GetTimestamp();
            using JsonDocument document = JsonDocument.Parse(payloadJson);
            double parseMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            t0 = Stopwatch.GetTimestamp();
            ExecutionPayloadV4 payload = serializer.Deserialize<ExecutionPayloadV4>(payloadJson)!;
            double bindMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            t0 = Stopwatch.GetTimestamp();
            Transaction[] txs = payload.TryGetTransactions().Data!;
            double txDecodeMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            t0 = Stopwatch.GetTimestamp();
            Block block = payload.TryGetBlock().Data!;
            double blockMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            RecoverSignatures recovery = new(chain.EthereumEcdsa, chain.SpecProvider, chain.LogManager);
            IReleaseSpec spec = chain.SpecProvider.GetSpec(block.Header);
            foreach (Transaction tx in txs) tx.SenderAddress = null;
            t0 = Stopwatch.GetTimestamp();
            recovery.RecoverData(txs, spec);
            double recoverMs = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;

            Log($"  round {round}: document parse {parseMs,7:F1} ms | typed bind {bindMs,7:F1} ms | tx decode {txDecodeMs,7:F1} ms | TryGetBlock (tx root) {blockMs,7:F1} ms | ecrecover {recoverMs,7:F1} ms");
        }
        await Task.CompletedTask;
    }

    private static string Summary(List<CallSample> samples)
    {
        double[] totals = samples.Select(s => s.TotalMs).Order().ToArray();
        double[] pauses = samples.Select(s => s.PauseMs).Order().ToArray();
        static double Pct(double[] a, double p) => a[Math.Min(a.Length - 1, (int)(a.Length * p))];
        return $"n={totals.Length} total p50 {Pct(totals, 0.5):F1} p90 {Pct(totals, 0.9):F1} max {totals[^1]:F1} ms | gc pause p50 {Pct(pauses, 0.5):F1} p90 {Pct(pauses, 0.9):F1} max {pauses[^1]:F1} ms | calls with pause>50ms: {pauses.Count(p => p > 50)}";
    }

    private sealed class ListTxSource : ITxSource
    {
        public Transaction[] Transactions { get; set; } = [];
        public bool SupportsBlobs => false;
        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, BlockHeader targetBlock, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false) => Transactions;
    }

    private sealed class CallSample
    {
        public DateTime StartUtc, EndUtc;
        public double TotalMs, ParseMs, PauseMs, AllocatedMb;
        public int Gen0, Gen1, Gen2;
        public string Format() => $"{TotalMs,7:F1} ms (parse {ParseMs,5:F1}) gc {PauseMs,6:F1} ms g0/1/2 {Gen0}/{Gen1}/{Gen2} alloc {AllocatedMb,6:F0} MB";
    }

    private sealed class GcRecord
    {
        public DateTime Start, End;
        public uint Count, Depth, Reason, Type;
        public double SuspendedMs;
        public string ReasonName => Reason switch
        {
            0 => "AllocSmall",
            1 => "Induced",
            2 => "LowMemory",
            3 => "Empty",
            4 => "AllocLarge",
            5 => "OutOfSpaceSOH",
            6 => "OutOfSpaceLOH",
            7 => "InducedNotForced",
            8 => "Internal",
            9 => "InducedLowMemory",
            10 => "InducedCompacting",
            11 => "LowMemoryHost",
            12 => "PMFullGC",
            13 => "LowMemoryHostBlocking",
            _ => Reason.ToString()
        };
        public string TypeName => Type switch
        {
            0 => "Blocking",
            1 => "Background",
            2 => "Foreground",
            _ => Type.ToString()
        };
    }

    /// <summary>Polls the GC latency mode so the no-GC region a call opens can be placed on the call's timeline.</summary>
    private sealed class LatencyModeSampler : IDisposable
    {
        private readonly List<(DateTime At, GCLatencyMode Mode)> _transitions = [];
        private readonly System.Threading.Thread _thread;
        private volatile bool _stop;

        public LatencyModeSampler()
        {
            _thread = new System.Threading.Thread(Run) { IsBackground = true, Name = "latency-mode-sampler" };
            _thread.Start();
        }

        private void Run()
        {
            GCLatencyMode last = GCSettings.LatencyMode;
            lock (_transitions) _transitions.Add((DateTime.UtcNow, last));
            while (!_stop)
            {
                GCLatencyMode mode = GCSettings.LatencyMode;
                if (mode != last)
                {
                    lock (_transitions) _transitions.Add((DateTime.UtcNow, mode));
                    last = mode;
                }
                // Millisecond resolution places a region on a call's timeline without competing for a core.
                System.Threading.Thread.Sleep(1);
            }
        }

        public IEnumerable<(DateTime Start, DateTime End)> RegionWindows(DateTime from, DateTime to)
        {
            List<(DateTime At, GCLatencyMode Mode)> snapshot;
            lock (_transitions) snapshot = [.. _transitions];
            for (int i = 0; i < snapshot.Count; i++)
            {
                if (snapshot[i].Mode != GCLatencyMode.NoGCRegion) continue;
                DateTime end = i + 1 < snapshot.Count ? snapshot[i + 1].At : DateTime.UtcNow;
                if (snapshot[i].At <= to && end >= from) yield return (snapshot[i].At, end);
            }
        }

        public void Dispose()
        {
            _stop = true;
            _thread.Join(1000);
        }
    }

    /// <summary>Records runtime GC events so each pause can be attributed to the engine call it landed in.</summary>
    private sealed class GcEventListener(bool allocationTicks) : EventListener
    {
        public readonly ConcurrentQueue<GcRecord> Records = new();
        public readonly ConcurrentDictionary<string, long> AllocatedByType = new();
        public volatile bool InsideTimedCall;
        // Keyed by collection index: a foreground gen0/gen1 runs while a background gen2 is in progress.
        private readonly ConcurrentDictionary<uint, GcRecord> _inFlight = new();
        private GcRecord? _last;
        private DateTime _suspendStart;

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
            {
                EnableEvents(eventSource, allocationTicks ? EventLevel.Verbose : EventLevel.Informational, (EventKeywords)0x1);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            switch (e.EventName)
            {
                case "GCAllocationTick_V4":
                    // One tick per ~100 KB allocated; the sampled amount is attributed to the type that crossed the threshold.
                    if (InsideTimedCall)
                    {
                        long amount = Convert.ToInt64(e.Payload![3]);
                        AllocatedByType.AddOrUpdate((string?)e.Payload[5] ?? "?", amount, (_, v) => v + amount);
                    }
                    break;
                case "GCStart_V2":
                    uint count = Convert.ToUInt32(e.Payload![0]);
                    _inFlight[count] = new GcRecord
                    {
                        Start = e.TimeStamp,
                        Count = count,
                        Depth = Convert.ToUInt32(e.Payload[1]),
                        Reason = Convert.ToUInt32(e.Payload[2]),
                        Type = Convert.ToUInt32(e.Payload[3]),
                    };
                    break;
                case "GCSuspendEEBegin_V1":
                    _suspendStart = e.TimeStamp;
                    break;
                case "GCRestartEEEnd_V1":
                    // The most recently started collection owns the suspension: a foreground GC inside a background
                    // one suspends for itself, the background GC only at its own start and end.
                    GcRecord? target = null;
                    foreach (KeyValuePair<uint, GcRecord> inFlight in _inFlight)
                    {
                        if (target is null || inFlight.Key > target.Count) target = inFlight.Value;
                    }
                    target ??= _last;
                    if (target is not null && _suspendStart != default)
                    {
                        target.SuspendedMs += (e.TimeStamp - _suspendStart).TotalMilliseconds;
                        _suspendStart = default;
                    }
                    break;
                case "GCEnd_V1":
                    if (_inFlight.TryRemove(Convert.ToUInt32(e.Payload![0]), out GcRecord? ended))
                    {
                        ended.End = e.TimeStamp;
                        Records.Enqueue(ended);
                        _last = ended;
                    }
                    break;
            }
        }
    }
}
