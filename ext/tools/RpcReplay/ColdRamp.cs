using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.IO;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.IlEvm;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace RpcReplay;

/// <summary>
/// A cold process's first calls, one at a time from the first, as a freshly started node serves them: the corpus's
/// records in a fixed shuffled order, cycled until the time is up, each request timed and placed by its index, so that two
/// processes compare the same requests at the same point of their lives. Interleaved, each request runs twice, on the
/// interpreter and with the plugin, in alternating order, so one process compares the two paths at the same warmth.
/// </summary>
/// <remarks>
/// Arguments (RPCPOC_ARGS, '|'-separated): corpus | groups | output prefix | plugin on (1/0) | seconds | interleave (1/0)
/// | seed. With the plugin on under the Adaptive rules the plugin's startup step runs first, as on a node
/// (<see cref="Harness.StartAdaptive"/>), so its RPC list is compiled before the first call; <c>RPCPOC_RPC_LIST=0</c> leaves
/// it out. <c>RPCPOC_TIERS=1</c> also reports when the plugin's methods that compiled code calls reached each JIT tier.
/// Writes {prefix}.requests.csv, one row per request and path, and prints the mean latency per index bin, group and path.
/// </remarks>
public static class ColdRamp
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

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private readonly record struct Sample(int Index, bool Plugin, int Record, long StartTicks, long Ticks);

    public static async Task<int> Run(string[] a)
    {
        using TierListener tiers = Environment.GetEnvironmentVariable("RPCPOC_TIERS") == "1" ? new TierListener() : null;
        long processStart = Stopwatch.GetTimestamp() - (long)((DateTime.Now - Process.GetCurrentProcess().StartTime).TotalSeconds * Stopwatch.Frequency);
        string corpusPath = a[0], groupsPath = a[1], prefix = a[2];
        bool on = a[3] == "1";
        int seconds = int.Parse(a[4]);
        bool interleave = a[5] == "1";
        int seed = int.Parse(a[6]);

        List<byte[]> requests = File.ReadLines(corpusPath).Where(static l => l.Length > 0).Select(static l => Encoding.UTF8.GetBytes(l)).ToList();
        string[] groups = File.ReadAllLines(groupsPath).Take(requests.Count).ToArray();
        int n = requests.Count;
        bool adaptive = IlEvmSettings.Mode == IlMode.Adaptive;
        Console.WriteLine($"cold ramp: {n} requests, plugin {(on ? "on" : "off")}, {(adaptive ? "Adaptive" : "Legacy")}, {seconds} s, interleave {interleave}, seed {seed}, " +
            $"GC server {System.Runtime.GCSettings.IsServerGC}, cpus {Environment.ProcessorCount}");

        IlEvmSettings.Enabled = false;
        JsonRpcConfig rpcConfig = new() { Timeout = 600_000, GasCap = 100_000_000, EnabledModules = [ModuleType.Eth] };
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance) { AllowTestChainOverride = false })
            .AddSingleton<IJsonRpcConfig>(rpcConfig)
            .AddSingleton<IReadOnlyQueryModule>(new IlEvmQueryModule(ReadOnlyQueryKind.Call)));
        Console.WriteLine($"chain ready at {Ms(Stopwatch.GetTimestamp() - processStart):F0} ms since the process started");
        if (on && adaptive)
            await Harness.StartAdaptive(chain);
        IJsonRpcService service = chain.Container.Resolve<IJsonRpcService>();
        JsonRpcProcessor processor = new(service, rpcConfig, chain.Container.Resolve<IFileSystem>(), NullLogManager.Instance);
        JsonRpcUrl url = new("http", "127.0.0.1", 8545, RpcEndpoint.Http, false, [ModuleType.Eth]);
        JsonRpcProcessingOptions options = new(JsonRpcInputMode.SingleDocument);
        Sink sink = new();

        async Task<(long Start, long Ticks, byte[] Hash)> Send(int record, bool plugin)
        {
            IlEvmSettings.Enabled = plugin;
            sink.Reset();
            long start = Stopwatch.GetTimestamp();
            using (JsonRpcContext context = JsonRpcContext.Http(url))
            {
                await processor.ProcessAsync(requests[record], context, sink, options, CancellationToken.None);
            }

            long ticks = Stopwatch.GetTimestamp() - start;
            return (start, ticks, SHA256.HashData(sink.Written));
        }

        // The same order in every process of a seed: the first pass meets every record once.
        int[] order = Enumerable.Range(0, n).ToArray();
        new Random(seed).Shuffle(order);
        List<Sample> samples = new(1 << 16);
        List<(int Record, byte[] Hash)> answers = new(1 << 16);
        Dictionary<string, int> turns = [];
        int mismatches = 0;
        long first = Stopwatch.GetTimestamp();
        Console.WriteLine($"first call at {Ms(first - processStart):F0} ms since the process started");
        long until = first + seconds * Stopwatch.Frequency;
        for (int k = 0; Stopwatch.GetTimestamp() < until; k++)
        {
            int record = order[k % n];
            if (!interleave)
            {
                (long start, long ticks, byte[] hash) = await Send(record, on);
                samples.Add(new Sample(k, on, record, start - first, ticks));
                if (on)
                    answers.Add((record, hash));
                continue;
            }

            // Alternating order, so neither path always runs on what the other just warmed, as the first run of a record
            // pays the misses of its code caches: each group's requests take turns, flipped every pass, so within a group
            // either path runs first as often as the other in any stretch of requests, and each record does over passes.
            byte[] interpreted = null, compiled = null;
            int seen = turns.TryGetValue(groups[record], out int count) ? count : 0;
            turns[groups[record]] = seen + 1;
            foreach (bool plugin in ((seen ^ (k / n)) & 1) == 0 ? new[] { false, true } : new[] { true, false })
            {
                (long start, long ticks, byte[] hash) = await Send(record, plugin);
                samples.Add(new Sample(k, plugin, record, start - first, ticks));
                if (plugin) compiled = hash;
                else interpreted = hash;
            }

            if (!compiled.AsSpan().SequenceEqual(interpreted))
            {
                if (++mismatches <= 10)
                    Console.WriteLine($"  MISMATCH request {k} record {record} ({groups[record]})");
            }
        }

        IlEvmSettings.Enabled = false;
        double elapsed = Ms(Stopwatch.GetTimestamp() - first) / 1000;
        int calls = samples.Count == 0 ? 0 : samples[^1].Index + 1;
        Console.WriteLine($"{calls} requests in {elapsed:F1} s");

        // The plugin's answers against the interpreter's, now that the measured run is over.
        if (answers.Count > 0)
        {
            byte[][] expected = new byte[n][];
            foreach ((int record, byte[] hash) in answers)
            {
                expected[record] ??= (await Send(record, false)).Hash;
                if (!hash.AsSpan().SequenceEqual(expected[record]) && ++mismatches <= 10)
                    Console.WriteLine($"  MISMATCH record {record} ({groups[record]})");
            }
        }

        using (StreamWriter csv = new(prefix + ".requests.csv"))
        {
            csv.WriteLine("index,path,record,group,start_ms,latency_us");
            foreach (Sample s in samples)
                csv.WriteLine(string.Join(',', s.Index, s.Plugin ? "plugin" : "interpreter", s.Record, groups[s.Record], Ms(s.StartTicks).ToString("F3"), (Ms(s.Ticks) * 1000).ToString("F1")));
        }

        // Per index bin of doubling width, from the first request: when it ran, and each group's mean latency per path.
        string[] names = [.. groups.Distinct().Order()];
        string Header(string g) => interleave ? $"{g + " interp",11} {g + " plugin",11} {g + " ratio",8}" : $"{g + " us",9}";
        Console.WriteLine($"bin (requests)  {"from s",7} {"to s",7}  " + string.Join("  ", names.Select(Header)));
        for (int low = 0, high = 1; low < calls; low = high, high *= 2)
        {
            List<Sample> bin = samples.Where(s => s.Index >= low && s.Index < high).ToList();
            StringBuilder line = new($"{low + 1,6}-{Math.Min(high, calls),-7}  {Ms(bin.Min(static s => s.StartTicks)) / 1000,7:F2} {Ms(bin.Max(static s => s.StartTicks)) / 1000,7:F2}  ");
            foreach (string g in names)
            {
                double Mean(bool plugin)
                {
                    List<Sample> of = bin.Where(s => groups[s.Record] == g && s.Plugin == plugin).ToList();
                    return of.Count == 0 ? double.NaN : of.Average(static s => Ms(s.Ticks) * 1000);
                }

                if (interleave)
                {
                    double i = Mean(false), p = Mean(true);
                    line.Append($"{i,11:F1} {p,11:F1} {p / i,8:F3}  ");
                }
                else
                {
                    line.Append($"{Mean(on),9:F1}  ");
                }
            }

            Console.WriteLine(line.ToString().TrimEnd());
        }

        if (on && adaptive)
        {
            Console.WriteLine($"plugin: {IlLive.Describe()}; {IlGate.Describe()}; {IlCheck.Describe()}; {IlScheduler.DescribeCurrent()}");
            Console.WriteLine($"code states: {IlRuntime.StatesResolved} resolved, {IlRuntime.StatesRefreshed} refreshed, {IlRuntime.StatesAttached} attached, " +
                $"{IlRuntime.ContentHashes} content hashes, {IlRuntime.Lookups} lookups, {IlRuntime.ProfileCount} profiles, over {calls} requests");
        }
        tiers?.Report(first, processStart);
        Console.WriteLine($"mismatches {mismatches}");
        chain.Dispose();
        return mismatches == 0 ? 0 : 1;
    }

    /// <summary>The JIT's method loads of the plugin and the core, by tier, from the runtime's events.</summary>
    private sealed class TierListener : EventListener
    {
        private readonly ConcurrentQueue<(long Ticks, ulong Id, string Type, string Name, int Tier)> _loads = new();
        private static readonly long StopwatchAtUtcZero = Stopwatch.GetTimestamp() - DateTime.UtcNow.Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;

        private static readonly string[] TierNames = ["unknown", "minopts", "optimized", "tier0", "tier1", "tier1-osr", "r2r", "prejit", "tier0-instr", "tier1-instr"];

        protected override void OnEventSourceCreated(EventSource source)
        {
            if (source.Name == "Microsoft-Windows-DotNETRuntime")
                EnableEvents(source, EventLevel.Verbose, (EventKeywords)0x10);
        }

        private static string Field(EventWrittenEventArgs e, string name)
        {
            ReadOnlyCollection<string> names = e.PayloadNames;
            int index = names?.IndexOf(name) ?? -1;
            return index >= 0 ? e.Payload[index]?.ToString() ?? "" : "";
        }

        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if (e.EventName is null || !e.EventName.StartsWith("MethodLoadVerbose", StringComparison.Ordinal)) return;
            string type = Field(e, "MethodNamespace");
            if (!type.StartsWith("Nethermind.", StringComparison.Ordinal)) return;
            uint flags = uint.TryParse(Field(e, "MethodFlags"), out uint f) ? f : 0;
            ulong id = ulong.TryParse(Field(e, "MethodID"), out ulong m) ? m : 0;
            long ticks = StopwatchAtUtcZero + e.TimeStamp.ToUniversalTime().Ticks * Stopwatch.Frequency / TimeSpan.TicksPerSecond;
            _loads.Enqueue((ticks, id, type, Field(e, "MethodName"), (int)((flags >> 7) & 0xF)));
        }

        /// <summary>The helpers compiled code calls out of line - the plugin's called twins and its embedded-body call - and when each reached each tier, from the first call.</summary>
        public void Report(long first, long processStart)
        {
            Thread.Sleep(1500);
            var loads = _loads.ToArray();
            static bool Helper((long Ticks, ulong Id, string Type, string Name, int Tier) l) =>
                l.Type == "Nethermind.IlEvm.IlEvm" && (l.Name.EndsWith("Called", StringComparison.Ordinal) || l.Name is "IlEmbedded" or "IlCarry" or "IlBorrow");
            var helpers = loads.Where(Helper).GroupBy(static l => l.Id).ToList();
            Console.WriteLine($"tiers: {loads.Length} method loads of Nethermind code seen; {helpers.Count} helper instantiations compiled code calls");
            Dictionary<string, int> byTierAtFirst = [];
            List<double> toTier1 = [];
            int neverTier1 = 0;
            foreach (var helper in helpers)
            {
                var ordered = helper.OrderBy(static l => l.Ticks).ToList();
                string firstTier = TierNames[Math.Min(ordered[0].Tier, TierNames.Length - 1)];
                byTierAtFirst[firstTier] = byTierAtFirst.GetValueOrDefault(firstTier) + 1;
                var tier1 = ordered.FirstOrDefault(static l => l.Tier is 4 or 2);
                if (tier1.Id == 0) neverTier1++;
                else toTier1.Add(Ms(tier1.Ticks - first) / 1000);
            }

            toTier1.Sort();
            Console.WriteLine($"tiers: helpers first loaded as {string.Join(", ", byTierAtFirst.Select(static kv => $"{kv.Value} {kv.Key}"))}; " +
                $"{toTier1.Count} reached optimized code, {neverTier1} never did");
            if (toTier1.Count > 0)
            {
                Console.WriteLine($"tiers: optimized code of the helpers installed, in s from the first call: first {toTier1[0]:F2}, " +
                    $"10% {toTier1[toTier1.Count / 10]:F2}, median {toTier1[toTier1.Count / 2]:F2}, 90% {toTier1[toTier1.Count * 9 / 10]:F2}, last {toTier1[^1]:F2}");
            }

            foreach (var helper in helpers.OrderBy(static h => h.Min(static l => l.Ticks)).Take(int.TryParse(Environment.GetEnvironmentVariable("RPCPOC_TIERS_LIST"), out int list) ? list : 0))
            {
                Console.WriteLine($"  {helper.First().Name}#{helper.Key:x}: " + string.Join(", ", helper.OrderBy(static l => l.Ticks).Select(l => $"{TierNames[Math.Min(l.Tier, TierNames.Length - 1)]} at {Ms(l.Ticks - first) / 1000:F2} s")));
            }
        }
    }
}
