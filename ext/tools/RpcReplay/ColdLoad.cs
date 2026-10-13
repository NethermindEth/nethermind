using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Evm;
using Nethermind.IlEvm;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace RpcReplay;

/// <summary>
/// A cold process under open-loop load, as the RPC benchmark's warm-up puts a freshly started node: requests arrive at a
/// fixed rate from the first second, at most a fixed number run at once and arrivals beyond them are dropped (k6's
/// constant-arrival-rate executor), and every response is checked afterwards against the interpreter's answer to the
/// same record, taken once the load is over so the measured window starts with nothing warm.
/// </summary>
/// <remarks>
/// Arguments (RPCPOC_ARGS, '|'-separated): corpus | groups | output prefix | plugin on (1/0) | rps | seconds | max in
/// flight | seed. Writes {prefix}.requests.csv (one row per arrival) and {prefix}.timeline.csv (one row per second of
/// process counters), and prints a summary by 10-second window.
/// </remarks>
public static class ColdLoad
{
    private sealed class Sink : IJsonRpcResponseSink
    {
        private readonly MemoryStream _stream = new(1 << 16);

        public long BytesWritten => _stream.Length;
        public bool StopRequested => false;
        public ReadOnlySpan<byte> Written => _stream.GetBuffer().AsSpan(0, (int)_stream.Length);

        public void Reset() => _stream.SetLength(0);

        public async ValueTask WriteSingleAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken)
        {
            PipeWriter writer = PipeWriter.Create(_stream, new StreamPipeWriterOptions(leaveOpen: true));
            await JsonRpcResponseWriter.WriteAsync(writer, response, EthereumJsonSerializer.JsonOptions, false, cancellationToken);
            await writer.FlushAsync(cancellationToken);
            await writer.CompleteAsync();
        }

        public ValueTask BeginBatchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask WriteBatchItemAsync(JsonRpcResponse response, RpcReport report, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask EndBatchAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>A plugin counter by name, or 0 for a build without it, so the harness runs against older plugin builds too.</summary>
    private static long Counter(string name) =>
        typeof(IlEvmSettings).GetProperty(name)?.GetValue(null) is { } value ? Convert.ToInt64(value) : 0;

    // Outcome codes per arrival.
    // Rejected: the node's answer to an eth_call past its in-flight cap (-32005, HTTP 503 on a node), which k6 counts as failed.
    private const byte Dropped = 0, Ok = 1, Mismatch = 2, Failed = 3, Unfinished = 4, Rejected = 5;

    public static async Task<int> Run(string[] a)
    {
        string corpusPath = a[0], groupsPath = a[1], prefix = a[2];
        bool on = a[3] == "1";
        int rps = int.Parse(a[4]), seconds = int.Parse(a[5]), maxInFlight = int.Parse(a[6]), seed = int.Parse(a[7]);

        List<byte[]> requests = File.ReadLines(corpusPath).Where(static l => l.Length > 0).Select(static l => Encoding.UTF8.GetBytes(l)).ToList();
        string[] groups = File.ReadAllLines(groupsPath).Take(requests.Count).ToArray();
        int n = requests.Count;
        Console.WriteLine($"cold load: {n} requests, plugin {(on ? "on" : "off")}, {rps} rps x {seconds} s, max in flight {maxInFlight}, seed {seed}, " +
            $"GC server {System.Runtime.GCSettings.IsServerGC} latency mode {System.Runtime.GCSettings.LatencyMode}, cpus {Environment.ProcessorCount}");
        Console.WriteLine($"settings: threshold {IlEvmSettings.Threshold}, opcodes {IlEvmSettings.OpcodeThreshold}, hot rate {IlEvmSettings.HotOpcodesPerSecond}, " +
            $"sync {IlEvmSettings.CompileSynchronously}, self-check {IlEvmSettings.SelfCheck} after {IlEvmSettings.SelfCheckDelayMs} ms, max pending {IlEvmSettings.MaxPendingCompiles}");

        IlEvmSettings.Enabled = false;
        // The runner's node caps in-flight eth_calls at its CPU count, 16; past it calls fail fast.
        int callCap = int.TryParse(Environment.GetEnvironmentVariable("RPCPOC_CALL_CAP"), out int cap) ? cap : 16;
        JsonRpcConfig rpcConfig = new() { Timeout = 600_000, GasCap = 100_000_000, EnabledModules = [ModuleType.Eth], EthModuleConcurrentInstances = callCap };
        Stopwatch setup = Stopwatch.StartNew();
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance) { AllowTestChainOverride = false })
            .AddSingleton<IJsonRpcConfig>(rpcConfig)
            .AddSingleton<IReadOnlyQueryModule>(new IlEvmQueryModule(ReadOnlyQueryKind.Call)));
        IJsonRpcService service = chain.Container.Resolve<IJsonRpcService>();
        JsonRpcProcessor processor = new(service, rpcConfig, chain.Container.Resolve<IFileSystem>(), NullLogManager.Instance);
        JsonRpcUrl url = new("http", "127.0.0.1", 8545, RpcEndpoint.Http, false, [ModuleType.Eth]);
        JsonRpcProcessingOptions options = new(JsonRpcInputMode.SingleDocument);
        Console.WriteLine($"chain ready in {setup.ElapsedMilliseconds} ms");

        async Task<byte[]> Send(int i, Sink sink)
        {
            sink.Reset();
            using (JsonRpcContext context = JsonRpcContext.Http(url))
            {
                await processor.ProcessAsync(requests[i], context, sink, options, CancellationToken.None);
            }

            return SHA256.HashData(sink.Written);
        }

        // The request each arrival sends: uniform over the corpus, as json-bench's class weights (record counts) and
        // in-class sampling amount to.
        int total = rps * seconds;
        Random random = new(seed);
        int[] record = new int[total];
        for (int k = 0; k < total; k++) record[k] = random.Next(n);

        long[] scheduledAt = new long[total], startedAt = new long[total], endedAt = new long[total];
        byte[] outcome = new byte[total];
        byte[][] answers = new byte[total][];
        string[] heads = new string[total];
        int inFlight = 0, dropped = 0, failures = 0;
        ConcurrentBag<Sink> sinks = [];
        for (int s = 0; s < maxInFlight; s++) sinks.Add(new Sink());

        // One row per second of what the process did, from just before the first arrival.
        using Process self = Process.GetCurrentProcess();
        List<string> timeline = ["second,compiled,failed,deferred,pending_compiles,compile_busy_ms,jit_ms,jit_methods,gc0,gc1,gc2,gc_pause_ms,allocated_mb,cpu_ms,working_set_mb,threads,pool_threads,pool_pending,in_flight,kept,probation,given_back"];
        long origin = Stopwatch.GetTimestamp();
        using CancellationTokenSource stopSampler = new();
        Thread sampler = new(() =>
        {
            int second = 0;
            while (true)
            {
                self.Refresh();
                (int kept, int probation, int givenBack) = IlEvm.SelfCheckVerdicts;
                lock (timeline)
                {
                    timeline.Add(string.Join(',',
                        second, IlEvmSettings.CompiledContracts, IlEvmSettings.FailedContracts, IlEvmSettings.DeferredCompiles,
                        Counter("PendingCompiles"), Ms(Counter("CompileBusyTicks")).ToString("F0"),
                        System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds.ToString("F0"), System.Runtime.JitInfo.GetCompiledMethodCount(),
                        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration().TotalMilliseconds.ToString("F0"),
                        GC.GetTotalAllocatedBytes() >> 20, self.TotalProcessorTime.TotalMilliseconds.ToString("F0"), self.WorkingSet64 >> 20,
                        self.Threads.Count, ThreadPool.ThreadCount, ThreadPool.PendingWorkItemCount, Volatile.Read(ref inFlight),
                        kept, probation, givenBack));
                }

                second++;
                long next = origin + (long)second * Stopwatch.Frequency;
                long wait = next - Stopwatch.GetTimestamp();
                if (stopSampler.Token.WaitHandle.WaitOne(wait > 0 ? (int)Ms(wait) : 0)) break;
            }
        }) { IsBackground = true, Name = "cold-load sampler" };
        sampler.Start();

        IlEvmSettings.Enabled = on;
        timeBeginPeriod(1);
        Stopwatch clock = Stopwatch.StartNew();
        long period = Stopwatch.Frequency / rps;
        long start = Stopwatch.GetTimestamp();
        List<Task> running = new(total);
        for (int k = 0; k < total; k++)
        {
            long due = start + k * period;
            while (true)
            {
                long left = due - Stopwatch.GetTimestamp();
                if (left <= 0) break;
                if (left > Stopwatch.Frequency / 500) Thread.Sleep(1);
                else Thread.SpinWait(50);
            }

            scheduledAt[k] = due - origin;
            if (Interlocked.Increment(ref inFlight) > maxInFlight)
            {
                Interlocked.Decrement(ref inFlight);
                outcome[k] = Dropped;
                dropped++;
                continue;
            }

            int arrival = k;
            outcome[arrival] = Unfinished;
            running.Add(Task.Run(async () =>
            {
                sinks.TryTake(out Sink sink);
                sink ??= new Sink();
                startedAt[arrival] = Stopwatch.GetTimestamp() - origin;
                try
                {
                    answers[arrival] = await Send(record[arrival], sink);
                    endedAt[arrival] = Stopwatch.GetTimestamp() - origin;
                    heads[arrival] = Encoding.UTF8.GetString(sink.Written[..Math.Min(sink.Written.Length, 200)]);
                    outcome[arrival] = heads[arrival].Contains("\"code\":-32005") ? Rejected : Ok;
                }
                catch (Exception e)
                {
                    endedAt[arrival] = Stopwatch.GetTimestamp() - origin;
                    outcome[arrival] = Failed;
                    if (Interlocked.Increment(ref failures) <= 20)
                        Console.WriteLine($"  FAILED arrival {arrival} record {record[arrival]}: {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    sinks.Add(sink);
                    Interlocked.Decrement(ref inFlight);
                }
            }));
        }

        double arrivalSeconds = clock.Elapsed.TotalSeconds;
        bool drained = Task.WaitAll([.. running], TimeSpan.FromSeconds(120));
        double drainSeconds = clock.Elapsed.TotalSeconds - arrivalSeconds;
        timeEndPeriod(1);
        stopSampler.Cancel();
        sampler.Join();
        IlEvmSettings.Enabled = false;

        // The interpreter's answer to every record, now that the measured window is over.
        byte[][] expected = new byte[n][];
        Sink referenceSink = new();
        for (int i = 0; i < n; i++) expected[i] = await Send(i, referenceSink);
        int mismatches = 0;
        for (int k = 0; k < total; k++)
        {
            if (outcome[k] != Ok || answers[k].AsSpan().SequenceEqual(expected[record[k]])) continue;
            outcome[k] = Mismatch;
            if (++mismatches <= 12)
                Console.WriteLine($"  MISMATCH arrival {k} record {record[k]} ({groups[record[k]]}) at {Ms(endedAt[k]) / 1000:F1} s: {heads[k]}");
        }

        foreach (IGrouping<string, int> kind in Enumerable.Range(0, total).Where(k => outcome[k] == Mismatch)
                     .GroupBy(k => System.Text.RegularExpressions.Regex.Match(heads[k] ?? "", "\"message\":\"[^\"]{0,90}").Value is { Length: > 0 } m ? m : "no error message")
                     .OrderByDescending(static g => g.Count()))
            Console.WriteLine($"  mismatch kind: {kind.Count()}x {kind.Key}");

        using (StreamWriter csv = new(prefix + ".requests.csv"))
        {
            csv.WriteLine("arrival,record,group,scheduled_ms,started_ms,ended_ms,outcome");
            for (int k = 0; k < total; k++)
            {
                csv.WriteLine(string.Join(',', k, record[k], groups[record[k]], Ms(scheduledAt[k]).ToString("F2"),
                    outcome[k] == Dropped ? "" : Ms(startedAt[k]).ToString("F2"),
                    outcome[k] is Ok or Mismatch or Failed or Rejected ? Ms(endedAt[k]).ToString("F2") : "",
                    outcome[k] switch { Dropped => "dropped", Ok => "ok", Mismatch => "mismatch", Failed => "failed", Rejected => "rejected", _ => "unfinished" }));
            }
        }

        lock (timeline) File.WriteAllLines(prefix + ".timeline.csv", timeline);

        static double Percentile(List<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];

        Console.WriteLine($"arrivals took {arrivalSeconds:F1} s, drain {drainSeconds:F1} s{(drained ? "" : " (NOT drained after 120 s)")}; " +
            $"dropped {dropped}, rejected {outcome.Count(static o => o == Rejected)}, mismatches {mismatches}, failures {failures}, unfinished {outcome.Count(static o => o == Unfinished)}; in-flight eth_call cap {callCap}");
        Console.WriteLine("window      arrivals  dropped rejected  done   mean ms   p50 ms   p90 ms   p99 ms    max ms  lag p99 ms");
        for (int w = 0; w * 10 < seconds; w++)
        {
            List<double> latencies = [], lags = [];
            int arrivals = 0, droppedHere = 0, rejectedHere = 0;
            for (int k = w * 10 * rps; k < Math.Min(total, (w + 1) * 10 * rps); k++)
            {
                arrivals++;
                if (outcome[k] == Dropped) { droppedHere++; continue; }
                if (outcome[k] == Rejected) rejectedHere++;
                if (outcome[k] is Ok or Mismatch) latencies.Add(Ms(endedAt[k] - startedAt[k]));
                if (outcome[k] != Dropped) lags.Add(Ms(startedAt[k] - scheduledAt[k]));
            }

            latencies.Sort();
            lags.Sort();
            Console.WriteLine($"{w * 10,3}-{w * 10 + 10,3} s  {arrivals,8} {droppedHere,8} {rejectedHere,8} {latencies.Count,5} {(latencies.Count > 0 ? latencies.Average() : double.NaN),9:F2} {Percentile(latencies, 0.5),8:F2} " +
                $"{Percentile(latencies, 0.9),8:F2} {Percentile(latencies, 0.99),8:F2} {(latencies.Count > 0 ? latencies[^1] : double.NaN),9:F1} {Percentile(lags, 0.99),11:F2}");
        }

        Console.WriteLine($"compiled contracts {IlEvmSettings.CompiledContracts}, failed {IlEvmSettings.FailedContracts}, deferred {IlEvmSettings.DeferredCompiles}, " +
            $"compile thread busy {Ms(Counter("CompileBusyTicks")) / 1000:F1} s");
        (int keptAll, int probationAll, int givenBackAll) = IlEvm.SelfCheckVerdicts;
        Console.WriteLine($"self-check: {keptAll} kept compiled, {probationAll} put on probation, {givenBackAll} given back");
        chain.Dispose();
        return mismatches == 0 && failures == 0 ? 0 : 1;
    }
}
