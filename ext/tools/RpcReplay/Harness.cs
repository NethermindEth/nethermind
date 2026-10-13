using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Abstractions;
using System.IO.Pipelines;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Container;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Evm;
using Nethermind.IlEvm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace RpcReplay;

/// <summary>
/// Replays raw eth_call JSON-RPC requests through the production JsonRpcProcessor -> JsonRpcService -> EthRpcModule ->
/// BlockchainBridge path of an in-memory chain, once on the interpreter and once with the IL compiler, comparing every
/// response byte for byte and timing each request.
/// </summary>
public static class Harness
{
    private sealed class Sink : IJsonRpcResponseSink
    {
        private readonly MemoryStream _stream = new(1 << 20);

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

    private static double Us(long ticks) => ticks * 1_000_000.0 / Stopwatch.Frequency;

    /// <summary>Turns a captured eth_call request into the same call under another method.</summary>
    /// <remarks>
    /// eth_estimateGas re-runs the call in a binary search, so a gas divergence moves its answer; debug_traceCall with
    /// callTracer reports every frame's gas, output, error and logs, and traces no instructions, so compiled code runs.
    /// </remarks>
    private static string Rewrite(string line, string method)
    {
        if (method == "eth_call") return line;
        System.Text.Json.Nodes.JsonNode node = System.Text.Json.Nodes.JsonNode.Parse(line)!;
        node["method"] = method;
        if (method == "debug_traceCall")
        {
            System.Text.Json.Nodes.JsonArray parameters = node["params"]!.AsArray();
            System.Text.Json.Nodes.JsonNode overrides = parameters.Count > 2 ? parameters[2]?.DeepClone() : null;
            while (parameters.Count > 2) parameters.RemoveAt(parameters.Count - 1);
            System.Text.Json.Nodes.JsonObject options = new()
            {
                ["tracer"] = "callTracer",
                ["tracerConfig"] = new System.Text.Json.Nodes.JsonObject { ["withLog"] = true },
            };
            if (overrides is not null) options["stateOverrides"] = overrides;
            parameters.Add(options);
        }

        return node.ToJsonString();
    }

    private static string Describe(ReadOnlySpan<byte> response)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(response.ToArray());
            if (doc.RootElement.TryGetProperty("error", out JsonElement error))
            {
                string message = error.TryGetProperty("message", out JsonElement m) ? m.GetString() ?? "" : "";
                if (message.Length > 70) message = message[..70];
                return $"error {error.GetProperty("code").GetInt32()} {message}";
            }

            JsonElement resultElement = doc.RootElement.GetProperty("result");
            if (resultElement.ValueKind != JsonValueKind.String) return $"ok {resultElement.ValueKind}";
            string result = resultElement.GetString() ?? "";
            return $"ok {(result.Length - 2) / 2}B";
        }
        catch (Exception e)
        {
            return "unparsable " + e.GetType().Name;
        }
    }

    public static async Task<int> Run(string[] a)
    {
        using JitInlineListener jitTrace = Environment.GetEnvironmentVariable("RPCPOC_JITTRACE") == "1" ? new JitInlineListener() : null;
        string corpusPath = a[0];
        string groupsPath = a[1];
        string outCsv = a[2];
        int rounds = int.Parse(a[3]);
        int threshold = int.Parse(a[4]);
        bool eager = a[5] == "1";
        ulong gasCap = ulong.Parse(a[6]);
        int limit = a.Length > 7 ? int.Parse(a[7]) : int.MaxValue;
        bool runModuleLevel = a.Length > 8 && a[8] == "1";
        string method = a.Length > 9 ? a[9] : "eth_call";
        int concurrency = a.Length > 10 ? int.Parse(a[10]) : 0;
        if (method != "eth_call") runModuleLevel = false;

        List<byte[]> requests = [];
        foreach (string line in File.ReadLines(corpusPath))
        {
            if (line.Length == 0) continue;
            requests.Add(Encoding.UTF8.GetBytes(Rewrite(line, method)));
            if (requests.Count >= limit) break;
        }

        string[] groups = File.ReadAllLines(groupsPath).Take(requests.Count).ToArray();
        int n = requests.Count;
        Console.WriteLine($"requests {n}, rounds {rounds}, threshold {threshold}, eager {eager}, gasCap {gasCap}, GC server {System.Runtime.GCSettings.IsServerGC}, cpus {Environment.ProcessorCount}");

        IlEvmSettings.Enabled = false;
        IlEvmSettings.Threshold = threshold;
        IlEvmSettings.CompileSynchronously = eager;

        JsonRpcConfig rpcConfig = new() { Timeout = 600_000, GasCap = gasCap, EnabledModules = [ModuleType.Eth, ModuleType.Debug] };
        Stopwatch setup = Stopwatch.StartNew();
        // The plugin attaches its frame runner to the read-only query environments, as on a node.
        BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSpecProvider(Osaka.Instance) { AllowTestChainOverride = false })
            .AddSingleton<IJsonRpcConfig>(rpcConfig)
            .AddSingleton<IReadOnlyQueryModule>(new IlEvmQueryModule(ReadOnlyQueryKind.Call)));
        Console.WriteLine($"il-evm: {IlEvmModule.Unsupported() ?? "supported"}");
        Console.WriteLine($"chain ready in {setup.ElapsedMilliseconds} ms, head #{chain.BlockTree.Head?.Number} baseFee {chain.BlockTree.Head?.Header.BaseFeePerGas} gasLimit {chain.BlockTree.Head?.Header.GasLimit}");

        IJsonRpcService service = chain.Container.Resolve<IJsonRpcService>();
        JsonRpcProcessor processor = new(service, rpcConfig, chain.Container.Resolve<IFileSystem>(), NullLogManager.Instance);
        JsonRpcUrl url = new("http", "127.0.0.1", 8545, RpcEndpoint.Http, false, [ModuleType.Eth, ModuleType.Debug]);
        JsonRpcProcessingOptions options = new(JsonRpcInputMode.SingleDocument);
        Sink sink = new();

        async Task<(long Ticks, byte[] Hash, long Ops)> Send(int i)
        {
            sink.Reset();
            long ops = Nethermind.Evm.Metrics.OpCodes;
            long start = Stopwatch.GetTimestamp();
            using (JsonRpcContext context = JsonRpcContext.Http(url))
            {
                await processor.ProcessAsync(requests[i], context, sink, options, CancellationToken.None);
            }

            long elapsed = Stopwatch.GetTimestamp() - start;
            return (elapsed, SHA256.HashData(sink.Written), Nethermind.Evm.Metrics.OpCodes - ops);
        }

        static void Collect()
        {
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        // Pass 0: the interpreter's answers are the reference.
        byte[][] reference = new byte[n][];
        long[] referenceOps = new long[n];
        Dictionary<string, Dictionary<string, int>> outcomes = [];
        long pass0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < n; i++)
        {
            (long _, byte[] hash, long ops) = await Send(i);
            reference[i] = hash;
            referenceOps[i] = ops;
            string outcome = Describe(sink.Written);
            if (!outcomes.TryGetValue(groups[i], out Dictionary<string, int> byOutcome)) outcomes[groups[i]] = byOutcome = [];
            string key = outcome.StartsWith("ok ") ? "ok" : outcome;
            byOutcome[key] = byOutcome.GetValueOrDefault(key) + 1;
        }

        Console.WriteLine($"pass 0 (interpreter, reference) {Us(Stopwatch.GetTimestamp() - pass0) / 1000:F0} ms");
        foreach ((string group, Dictionary<string, int> byOutcome) in outcomes.OrderBy(static g => g.Key))
        {
            Console.WriteLine($"  {group}: {string.Join("; ", byOutcome.OrderByDescending(static o => o.Value).Select(static o => $"{o.Value}x {o.Key}"))}");
        }

        int Verify(string label, int i, byte[] hash, long ops)
        {
            int bad = 0;
            if (!hash.AsSpan().SequenceEqual(reference[i]))
            {
                bad++;
                Console.WriteLine($"  MISMATCH {label} request {i} ({groups[i]}): response differs: {Describe(sink.Written)}");
            }

            if (ops != referenceOps[i])
            {
                bad++;
                Console.WriteLine($"  MISMATCH {label} request {i} ({groups[i]}): opcodes {ops} vs {referenceOps[i]}");
            }

            return bad;
        }

        // Warm-up on the compiler: contracts cross the threshold and compile in the background.
        int mismatches = 0;
        IlEvmSettings.Enabled = true;
        for (int pass = 1; pass <= 2; pass++)
        {
            long start = Stopwatch.GetTimestamp();
            for (int i = 0; i < n; i++)
            {
                (long _, byte[] hash, long ops) = await Send(i);
                mismatches += Verify($"warm-up {pass}", i, hash, ops);
            }

            Console.WriteLine($"pass {pass} (compiler warm-up) {Us(Stopwatch.GetTimestamp() - start) / 1000:F0} ms, compiled {IlEvmSettings.CompiledContracts}, failed {IlEvmSettings.FailedContracts}");
        }

        long settled = -1;
        for (int k = 0; k < 120; k++)
        {
            long now = IlEvmSettings.CompiledContracts + IlEvmSettings.FailedContracts;
            if (now == settled) break;
            settled = now;
            await Task.Delay(500);
        }

        Console.WriteLine($"compile queue settled: compiled {IlEvmSettings.CompiledContracts}, failed {IlEvmSettings.FailedContracts}");
        IlEvmDiagnostics.Contracts.Clear();

        // Measured rounds, ABBA within a round and the order flipped between rounds.
        List<long>[] interpreted = Enumerable.Range(0, n).Select(static _ => new List<long>()).ToArray();
        List<long>[] compiled = Enumerable.Range(0, n).Select(static _ => new List<long>()).ToArray();
        for (int round = 0; round < rounds; round++)
        {
            bool[] modes = round % 2 == 0 ? [false, true, true, false] : [true, false, false, true];
            foreach (bool mode in modes)
            {
                IlEvmSettings.Enabled = mode;
                Collect();
                IlEvmDiagnostics.Reset();
                long start = Stopwatch.GetTimestamp();
                for (int i = 0; i < n; i++)
                {
                    (long ticks, byte[] hash, long ops) = await Send(i);
                    mismatches += Verify(mode ? "compiled" : "interpreter", i, hash, ops);
                    (mode ? compiled : interpreted)[i].Add(ticks);
                }

                Console.WriteLine($"round {round} {(mode ? "compiled   " : "interpreter")} {Us(Stopwatch.GetTimestamp() - start) / 1000:F0} ms, compiled so far {IlEvmSettings.CompiledContracts}, gen0/1/2 {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}");
                if (IlEvmDiagnostics.Enabled) Console.WriteLine($"  diag: {IlEvmDiagnostics.Describe()}");
            }
        }

        if (IlEvmDiagnostics.Enabled)
        {
            Console.WriteLine("IL emitted by category:");
            foreach (string line in IlEvmDiagnostics.DescribeIl(45)) Console.WriteLine("  " + line);
            Console.WriteLine("per code over the measured rounds (interpreter / compiled):");
            foreach (string line in IlEvmDiagnostics.DescribeContracts(40)) Console.WriteLine("  " + line);
        }

        // Module-level: the same calls with pre-parsed arguments, so JSON parsing and serialization are excluded.
        List<long>[] moduleInterpreted = Enumerable.Range(0, n).Select(static _ => new List<long>()).ToArray();
        List<long>[] moduleCompiled = Enumerable.Range(0, n).Select(static _ => new List<long>()).ToArray();
        if (runModuleLevel)
        {
            try
            {
                IEthRpcModule eth = chain.Container.Resolve<IRpcModuleFactory<IEthRpcModule>>().Create();
                EthereumJsonSerializer serializer = new();
                string[] moduleReference = new string[n];

                (SignableTransactionForRpc, Dictionary<Address, AccountOverride>) Parse(int i)
                {
                    using JsonDocument doc = JsonDocument.Parse(requests[i]);
                    JsonElement parameters = doc.RootElement.GetProperty("params");
                    SignableTransactionForRpc tx = serializer.Deserialize<SignableTransactionForRpc>(parameters[0].GetRawText());
                    Dictionary<Address, AccountOverride> overrides = parameters.GetArrayLength() > 2
                        ? serializer.Deserialize<Dictionary<Address, AccountOverride>>(parameters[2].GetRawText())
                        : null;
                    return (tx, overrides);
                }

                long Call(int i, out string signature)
                {
                    (SignableTransactionForRpc tx, Dictionary<Address, AccountOverride> overrides) = Parse(i);
                    long start = Stopwatch.GetTimestamp();
                    using ResultWrapper<HexBytes> result = eth.eth_call(tx, BlockParameter.Latest, overrides);
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    signature = $"{result.Result.ResultType}:{result.ErrorCode}:{result.Result.Error}:{Convert.ToHexString(SHA256.HashData(result.Data.Bytes.Span))}";
                    return elapsed;
                }

                IlEvmSettings.Enabled = false;
                for (int i = 0; i < n; i++) Call(i, out moduleReference[i]);
                for (int round = 0; round < Math.Max(1, rounds / 2); round++)
                {
                    bool[] modes = round % 2 == 0 ? [false, true, true, false] : [true, false, false, true];
                    foreach (bool mode in modes)
                    {
                        IlEvmSettings.Enabled = mode;
                        Collect();
                        long start = Stopwatch.GetTimestamp();
                        for (int i = 0; i < n; i++)
                        {
                            long ticks = Call(i, out string signature);
                            if (signature != moduleReference[i])
                            {
                                mismatches++;
                                Console.WriteLine($"  MISMATCH module {(mode ? "compiled" : "interpreter")} request {i}: {signature} vs {moduleReference[i]}");
                            }

                            (mode ? moduleCompiled : moduleInterpreted)[i].Add(ticks);
                        }

                        Console.WriteLine($"module round {round} {(mode ? "compiled   " : "interpreter")} {Us(Stopwatch.GetTimestamp() - start) / 1000:F0} ms");
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"module-level pass skipped: {e.GetType().Name}: {e.Message}");
                runModuleLevel = false;
            }
        }

        // Closed-loop throughput: `concurrency` workers split the request list, each with its own response sink.
        if (concurrency > 0)
        {
            async Task<(double Seconds, int Bad)> RunConcurrent(bool mode)
            {
                IlEvmSettings.Enabled = mode;
                Collect();
                int bad = 0;
                long start = Stopwatch.GetTimestamp();
                Task[] workers = new Task[concurrency];
                for (int w = 0; w < concurrency; w++)
                {
                    int worker = w;
                    workers[w] = Task.Run(async () =>
                    {
                        Sink own = new();
                        for (int i = worker; i < n; i += concurrency)
                        {
                            own.Reset();
                            using (JsonRpcContext context = JsonRpcContext.Http(url))
                            {
                                await processor.ProcessAsync(requests[i], context, own, options, CancellationToken.None);
                            }

                            if (!SHA256.HashData(own.Written).AsSpan().SequenceEqual(reference[i])) Interlocked.Increment(ref bad);
                        }
                    });
                }

                await Task.WhenAll(workers);
                return (Us(Stopwatch.GetTimestamp() - start) / 1e6, bad);
            }

            foreach (bool mode in new[] { false, true, true, false, false, true })
            {
                (double seconds, int bad) = await RunConcurrent(mode);
                mismatches += bad;
                Console.WriteLine($"concurrency {concurrency} {(mode ? "compiled   " : "interpreter")} {n / seconds,8:F1} req/s ({seconds * 1000:F0} ms, {bad} mismatches)");
            }
        }

        static double Median(List<long> values)
        {
            if (values.Count == 0) return double.NaN;
            long[] sorted = [.. values.Order()];
            return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
        }

        using (StreamWriter csv = new(outCsv))
        {
            csv.WriteLine("index,group,request_bytes,opcodes,rpc_interpreter_us,rpc_compiled_us,module_interpreter_us,module_compiled_us");
            for (int i = 0; i < n; i++)
            {
                csv.WriteLine(string.Join(',',
                    i, groups[i], requests[i].Length, referenceOps[i],
                    (Median(interpreted[i]) * 1_000_000.0 / Stopwatch.Frequency).ToString("F1"),
                    (Median(compiled[i]) * 1_000_000.0 / Stopwatch.Frequency).ToString("F1"),
                    runModuleLevel ? (Median(moduleInterpreted[i]) * 1_000_000.0 / Stopwatch.Frequency).ToString("F1") : "",
                    runModuleLevel ? (Median(moduleCompiled[i]) * 1_000_000.0 / Stopwatch.Frequency).ToString("F1") : ""));
            }
        }

        Console.WriteLine();
        Console.WriteLine("group | n   | Mops/req | rpc interp ms | rpc compiled ms | rpc speedup | geomean ratio | module interp ms | module compiled ms | module speedup");
        foreach (IGrouping<string, int> group in Enumerable.Range(0, n).GroupBy(i => groups[i]).OrderBy(static g => g.Key))
        {
            double sumInterpreted = group.Sum(i => Median(interpreted[i]));
            double sumCompiled = group.Sum(i => Median(compiled[i]));
            double logRatio = group.Average(i => Math.Log(Median(compiled[i]) / Median(interpreted[i])));
            double ops = group.Average(i => (double)referenceOps[i]) / 1e6;
            string module = "";
            if (runModuleLevel)
            {
                double moduleInt = group.Sum(i => Median(moduleInterpreted[i]));
                double moduleComp = group.Sum(i => Median(moduleCompiled[i]));
                module = $" | {Us((long)moduleInt) / 1000 / group.Count(),16:F3} | {Us((long)moduleComp) / 1000 / group.Count(),18:F3} | {moduleInt / moduleComp,14:F2}x";
            }

            Console.WriteLine($"{group.Key,-5} | {group.Count(),3} | {ops,8:F3} | {Us((long)sumInterpreted) / 1000 / group.Count(),13:F3} | {Us((long)sumCompiled) / 1000 / group.Count(),15:F3} | {sumInterpreted / sumCompiled,10:F2}x | {Math.Exp(logRatio),13:F3}{module}");
        }

        Console.WriteLine($"mismatches {mismatches}; compiled contracts {IlEvmSettings.CompiledContracts}, failed {IlEvmSettings.FailedContracts}");
        (int kept, int probation, int givenBack) = IlEvm.SelfCheckVerdicts;
        Console.WriteLine($"self-check: {kept} kept compiled, {probation} put on probation, {givenBack} given back");
        jitTrace?.Report();
        IlEvmSettings.Enabled = false;
        chain.Dispose();
        return mismatches == 0 ? 0 : 1;
    }
}
