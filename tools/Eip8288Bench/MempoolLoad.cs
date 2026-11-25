// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Tools.Eip8288Bench;

/// <summary>Scenario A: realistic mode-0 wrapper traffic submitted to a running eth/75 or eth/76 network, timed by each node's
/// <c>newPendingTransactions</c> subscription.</summary>
/// <remarks>Every transaction comes from its own funded account at nonce 0, so admission never waits on nonce order. A wrapper is
/// propagated to a node once all its transactions are pending there; latency runs from the wrapper's first submission.</remarks>
internal static class MempoolLoad
{
    private const ulong ChainId = 8288;

    private static PrivateKey Account(int index)
    {
        Span<byte> seed = stackalloc byte[4 + 20];
        "eip8288-bench-acct-v1"u8.CopyTo(seed);
        BinaryPrimitives.WriteInt32BigEndian(seed[20..], index);
        return new PrivateKey(ValueKeccak.Compute(seed).ToByteArray());
    }

    /// <summary>Writes a geth-style genesis: all forks through Prague and the EIP-8288 prototype at genesis, the load accounts funded.</summary>
    public static int WriteGenesis(Options options)
    {
        int accounts = options.Int("accounts", 20_000);
        JsonObject alloc = [];
        for (int i = 0; i < accounts; i++) alloc[Account(i).Address.ToString()] = new JsonObject { ["balance"] = "0x56bc75e2d63100000" };
        JsonObject config = new()
        {
            ["chainId"] = ChainId,
            ["homesteadBlock"] = 0,
            ["eip150Block"] = 0,
            ["eip155Block"] = 0,
            ["eip158Block"] = 0,
            ["byzantiumBlock"] = 0,
            ["constantinopleBlock"] = 0,
            ["petersburgBlock"] = 0,
            ["istanbulBlock"] = 0,
            ["berlinBlock"] = 0,
            ["londonBlock"] = 0,
            ["mergeNetsplitBlock"] = 0,
            ["terminalTotalDifficulty"] = 0,
            ["terminalTotalDifficultyPassed"] = true,
            ["shanghaiTime"] = 0,
            ["cancunTime"] = 0,
            ["pragueTime"] = 0,
            ["eip8288PrototypeTime"] = 0,
            ["depositContractAddress"] = "0x00000000219ab540356cbb839cbe05303d7705fa",
            ["blobSchedule"] = new JsonObject
            {
                ["cancun"] = new JsonObject { ["target"] = 3, ["max"] = 6, ["baseFeeUpdateFraction"] = 3338477 },
                ["prague"] = new JsonObject { ["target"] = 6, ["max"] = 9, ["baseFeeUpdateFraction"] = 5007716 },
            },
        };
        JsonObject genesis = new()
        {
            ["config"] = config,
            ["nonce"] = "0x0",
            ["timestamp"] = "0x" + DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString("x", CultureInfo.InvariantCulture),
            ["extraData"] = "0x",
            ["gasLimit"] = "0x2255100",
            ["difficulty"] = "0x0",
            ["mixHash"] = "0x" + new string('0', 64),
            ["coinbase"] = "0x" + new string('0', 40),
            ["baseFeePerGas"] = "0x3b9aca00",
            ["alloc"] = alloc,
        };
        File.WriteAllText(options.Path("out", "genesis.json"), genesis.ToJsonString());
        return 0;
    }

    private sealed record Wrapper(int Index, double AtMs, int Origin, int? Duplicate, byte[] Encoded, Hash256[] Transactions, int Deps, int SharedDeps);

    private sealed class Submission
    {
        public double SentMs;
        public double RepliedMs;
        public string? Error;
    }

    public static async Task<int> Execute(Options options)
    {
        string[] rpc = options.String("rpc", "http://127.0.0.1:8545").Split(',', StringSplitOptions.RemoveEmptyEntries);
        string[] ws = options.String("ws", "ws://127.0.0.1:8546").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (rpc.Length != ws.Length) throw new ArgumentException("--rpc and --ws must list the same nodes");
        double rate = options.Double("rate", 5);
        double seconds = options.Double("seconds", 60);
        double graceSeconds = options.Double("grace-seconds", 20);
        double dupFraction = options.Double("dup-fraction", 0.1);
        double sharedFraction = options.Double("shared-fraction", 0.1);
        int hotSet = options.Int("hot-set", 32);
        int seed = options.Int("seed", 8288);
        (int Value, double Weight)[] depsDist = Distribution(options.String("deps", "1:60,2:25,4:10,8:5"));
        (int Value, double Weight)[] txsDist = Distribution(options.String("txs-per-wrapper", "1:80,2:15,4:5"));
        string outDir = options.Path("out", "eip8288-mempool-results");
        Directory.CreateDirectory(outDir);

        NativeLeanProofVerifier.Instance.EnsureAvailable();
        Random random = new(seed);
        List<(double AtMs, int Txs, int[] Deps)> plan = [];
        int freshNeeded = hotSet;
        for (double at = Exponential(random, rate); at < seconds * 1000; at += Exponential(random, rate))
        {
            int txs = Draw(random, txsDist);
            int[] deps = new int[txs];
            for (int t = 0; t < txs; t++)
            {
                deps[t] = Draw(random, depsDist);
                freshNeeded += deps[t];
            }
            plan.Add((at, txs, deps));
        }
        SignaturePool pool = SignaturePool.LoadOrCreate(options.Path("pool", Path.Combine(outDir, "signatures.bin")), freshNeeded,
            options.Int("sign-parallelism", Environment.ProcessorCount));

        List<Wrapper> wrappers = Build(plan, pool, random, rpc.Length, dupFraction, sharedFraction, hotSet);
        Console.Error.WriteLine($"built {wrappers.Count} wrappers, {wrappers.Sum(w => w.Transactions.Length)} transactions, " +
            $"{wrappers.Sum(w => (long)w.Encoded.Length) / 1024} KiB");

        Stopwatch clock = Stopwatch.StartNew();
        ConcurrentDictionary<(int Node, Hash256 Tx), double> seen = new();
        using CancellationTokenSource stop = new();
        Task[] listeners = new Task[ws.Length];
        for (int n = 0; n < ws.Length; n++) listeners[n] = Listen(n, new Uri(ws[n]), clock, seen, stop.Token);
        await Task.Delay(TimeSpan.FromSeconds(2));

        using HttpClient http = new() { Timeout = TimeSpan.FromSeconds(60) };
        Submission[] primary = new Submission[wrappers.Count];
        Submission?[] duplicate = new Submission?[wrappers.Count];
        List<Task> sends = [];
        double startMs = clock.Elapsed.TotalMilliseconds;
        foreach (Wrapper wrapper in wrappers)
        {
            double due = startMs + wrapper.AtMs - clock.Elapsed.TotalMilliseconds;
            if (due > 1) await Task.Delay(TimeSpan.FromMilliseconds(due));
            primary[wrapper.Index] = new();
            sends.Add(Send(http, rpc[wrapper.Origin], wrapper.Encoded, primary[wrapper.Index], clock));
            if (wrapper.Duplicate is int other)
            {
                Submission dup = duplicate[wrapper.Index] = new();
                sends.Add(DelayedSend(http, rpc[other], wrapper.Encoded, dup, clock, random.Next(0, 200)));
            }
        }
        double offeredEndMs = clock.Elapsed.TotalMilliseconds;
        await Task.WhenAll(sends);
        await Task.Delay(TimeSpan.FromSeconds(graceSeconds));
        await stop.CancelAsync();
        try { await Task.WhenAll(listeners); }
        catch (OperationCanceledException) { }

        Write(outDir, options, wrappers, primary, duplicate, seen, rpc.Length, startMs, offeredEndMs);
        return 0;
    }

    private static List<Wrapper> Build(List<(double AtMs, int Txs, int[] Deps)> plan, SignaturePool pool, Random random, int nodes,
        double dupFraction, double sharedFraction, int hotSet)
    {
        List<Wrapper> wrappers = new(plan.Count);
        int nextFresh = hotSet;
        int account = 0;
        foreach ((double atMs, int txCount, int[] depCounts) in plan)
        {
            List<Transaction> transactions = [];
            Dictionary<FrameDependency, byte[]> witnesses = [];
            int shared = 0;
            for (int t = 0; t < txCount; t++)
            {
                List<FrameDependency> deps = [];
                for (int d = 0; d < depCounts[t]; d++)
                {
                    // Fresh claims stay within one wrapper's MAX_LEANSIG_DEPS_PER_WRAPPER; extra ones come from the hot set.
                    bool hot = random.NextDouble() < sharedFraction || witnesses.Count >= Eip8288Constants.MaxLeanSigDepsPerWrapper;
                    int index = hot ? random.Next(hotSet) : nextFresh++;
                    FrameDependency dependency = pool.Dependency(index);
                    if (witnesses.ContainsKey(dependency) || deps.Contains(dependency)) continue;
                    if (witnesses.Count >= Eip8288Constants.MaxLeanSigDepsPerWrapper) break;
                    if (hot) shared++;
                    deps.Add(dependency);
                    witnesses[dependency] = pool.Records[index].Witness;
                }
                transactions.Add(Transaction(Account(account++), deps));
            }
            transactions.Sort((a, b) => a.Hash!.Bytes.SequenceCompareTo(b.Hash!.Bytes));
            List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(witnesses.Keys);
            List<byte[]> proofs = canonical.ConvertAll(d => witnesses[d]);
            MempoolWrapper wrapper = new() { Transactions = transactions, Mode = MempoolWrapper.ModeDirect, Deps = canonical, Proofs = proofs };
            int origin = random.Next(nodes);
            int? dup = nodes > 1 && random.NextDouble() < dupFraction ? (origin + 1 + random.Next(nodes - 1)) % nodes : null;
            wrappers.Add(new(wrappers.Count, atMs, origin, dup, MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes,
                [.. transactions.Select(t => t.Hash!)], canonical.Count, shared));
        }
        return wrappers;
    }

    private static Transaction Transaction(PrivateKey key, List<FrameDependency> deps)
    {
        TxFrame[] frames =
        [
            new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null, 100_000, UInt256.Zero, default),
            new(FrameMode.DepVerify, FrameFlags.None, null, (ulong)deps.Count * Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero,
                Eip8288Dependencies.Serialize(deps)),
        ];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = ChainId,
            SenderAddress = key.Address,
            NonceKeys = [],
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1_000_000_000,
            DecodedMaxFeePerGas = 100_000_000_000,
            MaxFee = 1_000_000_000_000_000_000,
        };
        transaction.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, key.Address, default,
            new byte[TxFrameSignature.Secp256k1SignatureLength])];
        Signature signature = new Ecdsa().Sign(key, FrameTxSigHash.ComputeValue(transaction));
        byte[] raw = new byte[TxFrameSignature.Secp256k1SignatureLength];
        raw[0] = signature.RecoveryId;
        signature.Bytes.CopyTo(raw.AsSpan(1));
        transaction.FrameSignatures = [new TxFrameSignature(TxFrameSignature.SchemeSecp256k1, key.Address, default, raw)];
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

    private static async Task DelayedSend(HttpClient http, string url, byte[] wrapper, Submission submission, Stopwatch clock, int delayMs)
    {
        await Task.Delay(delayMs);
        await Send(http, url, wrapper, submission, clock);
    }

    private static async Task Send(HttpClient http, string url, byte[] wrapper, Submission submission, Stopwatch clock)
    {
        string body = $$"""{"jsonrpc":"2.0","id":1,"method":"eth_sendProofWrapper","params":["0x{{Convert.ToHexStringLower(wrapper)}}"]}""";
        submission.SentMs = clock.Elapsed.TotalMilliseconds;
        try
        {
            using HttpResponseMessage response = await http.PostAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));
            using JsonDocument reply = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (reply.RootElement.TryGetProperty("error", out JsonElement error))
                submission.Error = error.TryGetProperty("message", out JsonElement message) ? message.GetString() : error.ToString();
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
        {
            submission.Error = exception.GetType().Name + ": " + exception.Message;
        }
        submission.RepliedMs = clock.Elapsed.TotalMilliseconds;
    }

    private static async Task Listen(int node, Uri uri, Stopwatch clock, ConcurrentDictionary<(int, Hash256), double> seen, CancellationToken token)
    {
        using ClientWebSocket socket = new();
        await socket.ConnectAsync(uri, token);
        await socket.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"eth_subscribe","params":["newPendingTransactions"]}"""u8.ToArray(),
            WebSocketMessageType.Text, true, token);
        byte[] buffer = new byte[64 * 1024];
        using MemoryStream message = new();
        try
        {
            while (!token.IsCancellationRequested)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                double now = clock.Elapsed.TotalMilliseconds;
                using (JsonDocument document = JsonDocument.Parse(message.ToArray()))
                    if (document.RootElement.TryGetProperty("params", out JsonElement parameters)
                        && parameters.TryGetProperty("result", out JsonElement hash) && hash.ValueKind == JsonValueKind.String)
                        seen.TryAdd((node, new Hash256(hash.GetString()!)), now);
                message.SetLength(0);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static void Write(string outDir, Options options, List<Wrapper> wrappers, Submission[] primary, Submission?[] duplicate,
        ConcurrentDictionary<(int Node, Hash256 Tx), double> seen, int nodes, double startMs, double offeredEndMs)
    {
        List<double> propagation = [];
        List<double> admission = [];
        Dictionary<string, int> errors = [];
        int expectedPairs = 0, reachedPairs = 0, accepted = 0;
        StringBuilder csv = new("wrapper,at_ms,origin,duplicate,bytes,txs,deps,shared_deps,admission_ms,error,reached_nodes,max_propagation_ms\n");
        foreach (Wrapper wrapper in wrappers)
        {
            Submission submission = primary[wrapper.Index];
            if (submission.Error is null) accepted++;
            else errors[submission.Error] = errors.GetValueOrDefault(submission.Error) + 1;
            if (duplicate[wrapper.Index]?.Error is { } dupError) errors["duplicate: " + dupError] = errors.GetValueOrDefault("duplicate: " + dupError) + 1;
            admission.Add(submission.RepliedMs - submission.SentMs);
            int reached = 0;
            double worst = double.NaN;
            for (int node = 0; node < nodes; node++)
            {
                double last = double.NegativeInfinity;
                bool all = true;
                foreach (Hash256 tx in wrapper.Transactions)
                {
                    if (seen.TryGetValue((node, tx), out double at)) last = Math.Max(last, at);
                    else all = false;
                }
                if (node == wrapper.Origin || node == wrapper.Duplicate) continue;
                if (submission.Error is null) expectedPairs++;
                if (!all) continue;
                reached++;
                reachedPairs++;
                double latency = last - submission.SentMs;
                propagation.Add(latency);
                worst = double.IsNaN(worst) ? latency : Math.Max(worst, latency);
            }
            csv.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{wrapper.Index},{wrapper.AtMs:F1},{wrapper.Origin},{wrapper.Duplicate?.ToString(CultureInfo.InvariantCulture) ?? ""},{wrapper.Encoded.Length},{wrapper.Transactions.Length},{wrapper.Deps},{wrapper.SharedDeps},{submission.RepliedMs - submission.SentMs:F1},\"{submission.Error?.Replace("\"", "'", StringComparison.Ordinal) ?? ""}\",{reached},{worst:F1}"));
        }

        double offeredSeconds = (offeredEndMs - startMs) / 1000;
        Dictionary<string, object?> document = new()
        {
            ["scenario"] = "mempool-propagation",
            ["host"] = HostInfo.Capture(),
            ["versions"] = BuilderBench.Versions(),
            ["parameters"] = new Dictionary<string, object?>
            {
                ["nodes"] = nodes,
                ["rate"] = options.Double("rate", 5),
                ["seconds"] = options.Double("seconds", 60),
                ["deps"] = options.String("deps", "1:60,2:25,4:10,8:5"),
                ["txsPerWrapper"] = options.String("txs-per-wrapper", "1:80,2:15,4:5"),
                ["dupFraction"] = options.Double("dup-fraction", 0.1),
                ["sharedFraction"] = options.Double("shared-fraction", 0.1),
                ["hotSet"] = options.Int("hot-set", 32),
                ["seed"] = options.Int("seed", 8288),
                ["graceSeconds"] = options.Double("grace-seconds", 20),
            },
            ["offered"] = new Dictionary<string, object?>
            {
                ["wrappers"] = wrappers.Count,
                ["transactions"] = wrappers.Sum(w => w.Transactions.Length),
                ["deps"] = wrappers.Sum(w => w.Deps),
                ["sharedDeps"] = wrappers.Sum(w => w.SharedDeps),
                ["duplicateSubmissions"] = wrappers.Count(w => w.Duplicate is not null),
                ["bytes"] = wrappers.Sum(w => (long)w.Encoded.Length),
                ["seconds"] = offeredSeconds,
                ["wrappersPerSecond"] = wrappers.Count / offeredSeconds,
            },
            ["accepted"] = accepted,
            ["errors"] = errors,
            ["admissionMs"] = Summary.Of(admission),
            ["propagationMs"] = Summary.Of(propagation),
            ["coverage"] = expectedPairs == 0 ? 0 : (double)reachedPairs / expectedPairs,
            ["perNodeSeen"] = Enumerable.Range(0, nodes).Select(n => seen.Keys.Count(k => k.Node == n)).ToArray(),
        };
        File.WriteAllText(Path.Combine(outDir, "load.json"), JsonSerializer.Serialize(document, Json.Options));
        File.WriteAllText(Path.Combine(outDir, "wrappers.csv"), csv.ToString());
        StringBuilder arrivals = new("node,tx,ms_after_start\n");
        foreach (((int node, Hash256 tx), double at) in seen)
            arrivals.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{node},{tx},{at - startMs:F1}"));
        File.WriteAllText(Path.Combine(outDir, "arrivals.csv"), arrivals.ToString());
        Summary p = Summary.Of(propagation);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"wrappers {wrappers.Count} accepted {accepted} coverage {(expectedPairs == 0 ? 0 : (double)reachedPairs / expectedPairs):P1} propagation p50 {p.P50:F0} p95 {p.P95:F0} p99 {p.P99:F0} ms"));
        foreach ((string error, int count) in errors) Console.WriteLine($"  {count} x {error}");
    }

    private static (int, double)[] Distribution(string spec)
    {
        List<(int, double)> result = [];
        foreach (string part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] pair = part.Split(':');
            result.Add((int.Parse(pair[0], CultureInfo.InvariantCulture), double.Parse(pair[1], CultureInfo.InvariantCulture)));
        }
        return [.. result];
    }

    private static int Draw(Random random, (int Value, double Weight)[] distribution)
    {
        double total = 0;
        foreach ((_, double weight) in distribution) total += weight;
        double pick = random.NextDouble() * total;
        foreach ((int value, double weight) in distribution)
        {
            if (pick < weight) return value;
            pick -= weight;
        }
        return distribution[^1].Value;
    }

    private static double Exponential(Random random, double rate) => -Math.Log(1 - random.NextDouble()) / rate * 1000;
}
