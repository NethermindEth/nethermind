// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Autofac;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;

namespace Nethermind.Tools.LeanBench;

public static partial class Program
{
    private const long MaxProtocolPreparedBytes = 512L * 1024 * 1024;
    private const int MaxProtocolPreparedObjects = 4096;
    private const string BackendCommit = "f33f31bf7c1191667e29a68a3acae63b9164c1c6";
    private static readonly List<BenchmarkRow> Rows = [];
    private static readonly List<BatchSample> Samples = [];
    private static readonly BenchCase[] Cases = [new("sphincs1", 1, 0), new("sphincs4", 4, 0),
        new("sphincs8", 8, 0), new("sphincs16", 16, 0), new("stark1", 0, 1),
        new("mixed1", 1, 1), new("mixed4", 4, 1), new("mixed8", 8, 1), new("mixed16", 16, 1),
        new("shared-sphincs1-tx16", 1, 0, true)];

    public static async Task Main(string[] args)
    {
        string Value(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith("--" + name + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? fallback;
        if (Value("devnet-payload", "") != "")
        {
            InspectDevnetPayload(args);
            return;
        }
        if (Value("devnet-transactions", "false") == "true")
        {
            GenerateDevnetTransactions(args);
            return;
        }
        if (Value("transport-checks", "false") == "true")
        {
            await TransportChecks.RunAsync();
            return;
        }
        if (Value("duplicate-normalization", "false") == "true")
        {
            RunDuplicateNormalization(args);
            return;
        }
        if (Value("mixed-traffic", "false") == "true")
        {
            await RunMixedTraffic(args);
            return;
        }
        string output = Path.GetFullPath(Value("out", "lean-bench-results"));
        string fixtureDirectory = Path.GetFullPath(Value("fixtures", "tools/lean-ffi/target/bench-vectors"));
        double seconds = double.Parse(Value("seconds", "10"), CultureInfo.InvariantCulture);
        int queueCapacity = int.Parse(Value("queue", "8"), CultureInfo.InvariantCulture);
        int warmups = int.Parse(Value("warmups", "3"), CultureInfo.InvariantCulture);
        int repetitions = int.Parse(Value("repetitions", "5"), CultureInfo.InvariantCulture);
        double[] rates = Value("rates", "1,10,25,50,100").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        double[] objectRates = Value("object-rates", "1,10,25,50").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        double[] protocolRates = Value("protocol-rates", "25,100").Split(',').Select(s => double.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        int[] sizes = Value("object-sizes", "1024,1048576,10485760").Split(',').Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        int[] blockCounts = Value("block-counts", "64,128").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToArray();
        HashSet<string> selected = Value("cases", string.Join(',', Cases.Select(c => c.Name))).Split(',').ToHashSet(StringComparer.Ordinal);
        if (!double.IsFinite(seconds) || seconds <= 0 || queueCapacity < 1 || warmups < 0 || repetitions < 1 || blockCounts.Any(c => c < 1 || c > Eip8288Constants.MaxProofDependencies) || sizes.Any(s => s < 1 || s > LeanProofStore.MaxWrapperBytes) || rates.Any(r => !double.IsFinite(r) || r <= 0) || objectRates.Any(r => !double.IsFinite(r) || r <= 0) || protocolRates.Any(r => !double.IsFinite(r) || r <= 0)) throw new ArgumentException("Positive duration, queue and rates required");
        Directory.CreateDirectory(output);
        Fixtures fixtures = new(fixtureDirectory);
        NativeLeanProofVerifier.Instance.EnsureAvailable();
        object metadata = new
        {
            commandLine = Environment.CommandLine,
            sourceRevision = Value("source-revision", "working tree; see compiled hashes"),
            hardware = Value("hardware", "unspecified"),
            compiledAssemblySha256 = Directory.GetFiles(AppContext.BaseDirectory, "Nethermind.*.dll").Append(typeof(Program).Assembly.Location).ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))),
            nativeLibrarySha256 = Directory.GetFiles(AppContext.BaseDirectory, "*nethermind_lean*").ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))),
            utc = DateTimeOffset.UtcNow,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            logicalCpus = Environment.ProcessorCount,
            runtime = RuntimeInformation.FrameworkDescription,
            backendCommit = BackendCommit,
            sphincsWitnessBytes = Eip8288Constants.LeanSphincsWitnessBytes,
            transport = "localhost TCP; production Snappy/AES/MAC RLPx codecs; preset session secrets; shared production wrapper admission; no WAN or capability-handshake timing",
            protocolPreprovedTransport = "lean/1; 64KiB chunks; production reassembly and admission; codec/TCP transport excludes PacketSender",
            protocolSchedulerBackend = typeof(Nethermind.Consensus.Scheduler.BackgroundTaskScheduler).FullName,
            protocolScheduler = "Production BackgroundTaskScheduler from node DI; drain tracking preserves capacity, timeouts and block-processing cancellation; fixed idle chain",
            protocolPreparationByteLimit = MaxProtocolPreparedBytes,
            protocolPreparationObjectLimit = MaxProtocolPreparedObjects,
            percentileMethod = "Nearest rank: sorted[ceil(p * count) - 1]",
            warmupBatches = warmups,
            cryptoRepetitions = repetitions,
            durationSeconds = seconds,
            queueCapacity,
            fixtureGeneration = File.Exists(Path.Combine(fixtureDirectory, "fixture-generation.json")) ? JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(fixtureDirectory, "fixture-generation.json"))) : (JsonElement?)null,
            sphincsFixtureCount = fixtures.Sphincs.Length,
            sphincsDistinctPublicKeys = fixtures.Sphincs.Select(f => f.Dependency.VerificationKey).Distinct().Count(),
            sphincsKeyDistribution = "deterministic key pool cycling across fixture identities; actual distinct key count reported",
            starkFixtureCount = fixtures.Starks.Length,
            methodology = "Open-loop scheduled batch arrivals; bounded FIFO drops whole batches when full; latency includes queue through admission; protocol percentiles use accepted batches and their last admitted transaction; duration includes drain. Fresh distinct claims per consumed batch within each independent run. Pool entries removed after admission without execution so funding/nonce state stays fixed. CPU is process-wide; load-stage CPU intervals may overlap producer/other work and must not be summed. Wire bytes exclude TCP/IP overhead; useful tx goodput excludes proofs and wrappers. Low-rate offered load is quantized by batch size; actual and target rates are separate."
        };
        void Save()
        {
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { metadata, results = Rows, samples = Samples },
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            WriteCsv(Path.Combine(output, "results.csv"), Rows);
            WriteCsv(Path.Combine(output, "samples.csv"), Samples);
        }
        foreach (BenchCase scenario in Cases.Where(c => selected.Contains(c.Name)))
        {
            MeasureCrypto(fixtures, scenario, warmups, repetitions);
            Save();
        }
        foreach (int count in blockCounts)
        {
            MeasureCrypto(fixtures, new("block-sphincs" + count, count, 0), warmups, repetitions);
            Save();
        }
        foreach (BenchCase scenario in Cases.Where(c => selected.Contains(c.Name)))
            foreach (double rate in rates)
            {
                await RunLoad(fixtures, scenario, seconds, rate, queueCapacity, warmups);
                Save();
            }
        HashSet<string> protocolCases = Value("protocol-cases", "sphincs1,sphincs16,stark1").Split(',').ToHashSet(StringComparer.Ordinal);
        foreach (BenchCase scenario in Cases.Where(c => protocolCases.Contains(c.Name)))
            foreach (double rate in protocolRates)
            {
                await RunProtocol(fixtures, scenario, seconds, rate, warmups);
                Save();
            }
        foreach (int size in sizes)
            foreach (double rate in objectRates)
            {
                await RunObjects(size, seconds, rate, queueCapacity, warmups);
                Save();
            }
        File.Copy(Path.Combine(fixtureDirectory, "fixture-generation.csv"), Path.Combine(output, "fixture-generation.csv"), true);
        Console.WriteLine($"Saved {Rows.Count} measured rows to {output}");
    }

    private static void MeasureCrypto(Fixtures fixtures, BenchCase scenario, int warmups, int repetitions)
    {
        List<BatchSample> samples = [];
        double cpu = MeasuredVerifier.CpuMilliseconds();
        long started = Stopwatch.GetTimestamp();
        for (int i = 0; i < warmups + repetitions; i++)
        {
            if (i == warmups) { started = Stopwatch.GetTimestamp(); cpu = MeasuredVerifier.CpuMilliseconds(); }
            AggregationInput input = fixtures.Input(scenario, i);
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(input.Deps);
            long proveStarted = Stopwatch.GetTimestamp();
            double proveCpu = MeasuredVerifier.CpuMilliseconds();
            byte[] proof = NativeLeanProofVerifier.Instance.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk, input);
            double proveMs = Stopwatch.GetElapsedTime(proveStarted).TotalMilliseconds;
            double proveCpuMs = MeasuredVerifier.CpuMilliseconds() - proveCpu;
            MeasuredVerifier verifier = new();
            if (!verifier.VerifyRecursiveStark(hash, Eip8288Constants.AggregatedVk, proof)) throw new InvalidOperationException("Generated proof failed");
            if (i >= warmups) samples.Add(new()
            {
                Case = scenario.Name,
                Sequence = i,
                TransactionCount = 0,
                ProofBytes = proof.Length,
                WrapperBytes = 0,
                ProveWallMs = proveMs,
                ProveCpuMs = proveCpuMs,
                VerifyWallMs = verifier.VerifyWallMs,
                VerifyCpuMs = verifier.VerifyCpuMs,
                LatencyMs = proveMs + verifier.VerifyWallMs
            });
        }
        double duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Rows.Add(Summarize("crypto", scenario, samples, 0, 0, duration, 0, 0, 0, 0, MeasuredVerifier.CpuMilliseconds() - cpu));
        Samples.AddRange(samples);
        Console.WriteLine($"Crypto {scenario.Name}: prove {samples.Average(s => s.ProveWallMs):F2}ms verify {samples.Average(s => s.VerifyWallMs):F2}ms proof {samples.Average(s => s.ProofBytes):F0}B");
    }

    private static async Task RunLoad(Fixtures fixtures, BenchCase scenario, double seconds, double rate, int capacity, int warmups)
    {
        MeasuredVerifier verifier = new();
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
            .AddSingleton<ILeanProofVerifier>(verifier));
        ProofWrapperService service = chain.Container.Resolve<ProofWrapperService>();
        using TcpRlpxLoopback transport = new();
        HashSet<Hash256> accepted = [];
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(seconds + 180));
        for (int i = 0; i < warmups; i++) await Batch(i, Stopwatch.GetTimestamp());
        accepted.Clear();
        List<BatchSample> samples = [];
        Channel<(long Sequence, long Offered)> queue = Channel.CreateBounded<(long, long)>(capacity);
        long offered = 0, dropped = 0;
        int fixtureIndex = warmups;
        long started = Stopwatch.GetTimestamp();
        double cpu = MeasuredVerifier.CpuMilliseconds();
        Task consumer = Task.Run(async () =>
        {
            await foreach ((long sequence, long scheduled) in queue.Reader.ReadAllAsync(stop.Token))
            {
                BatchSample sample = await Batch(fixtureIndex++, scheduled);
                samples.Add(sample);
            }
        });
        double interval = scenario.TransactionsPerBatch / rate;
        for (long sequence = 0; sequence * interval < seconds; sequence++)
        {
            long scheduled = started + (long)(sequence * interval * Stopwatch.Frequency);
            await WaitUntil(scheduled, stop.Token);
            offered++;
            if (!queue.Writer.TryWrite((sequence, scheduled))) dropped++;
        }
        await WaitUntil(started + (long)(seconds * Stopwatch.Frequency), stop.Token);
        queue.Writer.Complete();
        await consumer;
        double duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Rows.Add(Summarize("load", scenario, samples, rate, seconds, duration, offered * scenario.TransactionsPerBatch,
            offered, dropped * scenario.TransactionsPerBatch, dropped, MeasuredVerifier.CpuMilliseconds() - cpu));
        Samples.AddRange(samples);
        BenchmarkRow row = Rows[^1];
        Console.WriteLine($"Load {scenario.Name} target {rate:F0}: accepted {row.AcceptedTxPerSecond:F2}tx/s dropped {row.DroppedTransactions} rejected {row.RejectedTransactions} p95 {row.P95LatencyMs:F1}ms");

        async Task<BatchSample> Batch(int index, long scheduled)
        {
            AggregationInput input = fixtures.Input(scenario, index);
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(input.Deps);
            List<Transaction> transactions = [];
            for (int i = 0; i < scenario.TransactionsPerBatch; i++)
            {
                FrameDependency dep = input.Deps[scenario.SharedSignature ? 0 : i];
                int identity = dep.Scheme == Eip8288Constants.LeanSphincsScheme ? index * scenario.SphincsCount + (scenario.SharedSignature ? 0 : i) : index;
                transactions.Add(Transaction(chain, dep, identity, scenario.SharedSignature ? i : identity % 16, index * 16 + i));
            }
            long proveStarted = Stopwatch.GetTimestamp();
            double proveCpu = MeasuredVerifier.CpuMilliseconds();
            byte[] proof = NativeLeanProofVerifier.Instance.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk, input);
            double proveMs = Stopwatch.GetElapsedTime(proveStarted).TotalMilliseconds;
            double proveCpuMs = MeasuredVerifier.CpuMilliseconds() - proveCpu;
            MempoolWrapper wrapper = new()
            {
                Transactions = transactions.Select(t => new WrapperTransaction(t)).ToArray(),
                Mode = MempoolWrapper.ModeRecursive,
                Deps = Eip8288Dependencies.Canonicalize(input.Deps),
                RecursiveStark = new(proof, new Hash256(hash))
            };
            byte[] encoded = MempoolWrapperDecoder.Instance.Encode(wrapper).Bytes;
            double networkCpu = MeasuredVerifier.CpuMilliseconds();
            TransportSample delivered = await transport.TransferAsync(encoded, stop.Token);
            double networkCpuMs = MeasuredVerifier.CpuMilliseconds() - networkCpu;
            double verifiedMs = verifier.VerifyWallMs, verifiedCpu = verifier.VerifyCpuMs;
            long admissionStarted = Stopwatch.GetTimestamp();
            Result<Hash256[]> result = await service.AcceptAsync(delivered.Payload, stop.Token);
            BatchSample sample = new()
            {
                Case = scenario.Name,
                Sequence = index,
                TransactionCount = transactions.Count,
                ProofBytes = proof.Length,
                WrapperBytes = encoded.Length,
                WireBytes = delivered.WireBytes,
                ProveWallMs = proveMs,
                ProveCpuMs = proveCpuMs,
                EncodeWallMs = delivered.EncodeMs,
                TransferWallMs = delivered.TransferMs,
                NetworkCpuMs = networkCpuMs,
                AdmissionWallMs = Stopwatch.GetElapsedTime(admissionStarted).TotalMilliseconds,
                VerifyWallMs = verifier.VerifyWallMs - verifiedMs,
                VerifyCpuMs = verifier.VerifyCpuMs - verifiedCpu,
                RejectionReason = result.IsSuccess ? null : result.Error
            };
            sample.LatencyMs = Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds;
            foreach (Transaction tx in transactions)
                if (chain.TxPool.TryGetPendingTransaction(tx.Hash!.ValueHash256, out _))
                {
                    if (accepted.Add(tx.Hash)) { sample.AcceptedUniqueTransactions++; sample.GoodputBytes += Rlp.Encode(tx).Bytes.Length; }
                    else sample.RepeatedAcceptances++;
                    chain.TxPool.RemoveTransaction(tx.Hash);
                }
            return sample;
        }
    }

    private static Transaction Transaction(BasicTestBlockchain chain, FrameDependency dep, int identity, int nonce, int bodyIdentity)
    {
        byte[] data = new byte[24];
        BitConverter.TryWriteBytes(data.AsSpan(), identity);
        BitConverter.TryWriteBytes(data.AsSpan(8), nonce);
        BitConverter.TryWriteBytes(data.AsSpan(16), bodyIdentity);
        TxFrame self = new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null, FrameTxTestFrames.PrefixFrameGas, UInt256.Zero, data);
        TxFrame dependency = new(FrameMode.DepVerify, FrameFlags.None, null,
            dep.Scheme == Eip8288Constants.LeanSphincsScheme ? Eip8288Constants.LeanSphincsVerificationGas : Eip8288Constants.LeanStarkVerificationGas,
            UInt256.Zero, Eip8288Dependencies.Serialize([dep]));
        TxFrame[] frames = [self, dependency];
        PrivateKey key = dep.Scheme == Eip8288Constants.LeanSphincsScheme ? TestItem.PrivateKeyB : TestItem.PrivateKeyC;
        Transaction tx = new()
        {
            Type = TxType.FrameTx,
            ChainId = chain.SpecProvider.ChainId,
            SenderAddress = key.Address,
            Nonce = (ulong)nonce,
            NonceKeys = [UInt256.Zero],
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(tx, key, key.Address);
        tx.Hash = tx.CalculateHash();
        return tx;
    }

    private static async Task RunProtocol(Fixtures fixtures, BenchCase scenario, double seconds, double rate, int warmups)
    {
        MeasuredVerifier verifier = new();
        using ProtocolReceiver receiver = await ProtocolReceiver.Create(verifier);
        using TcpRlpxLoopback transport = new(receiver.Receive);
        double requestedBatches = Math.Ceiling(seconds * rate / scenario.TransactionsPerBatch);
        if (!double.IsFinite(requestedBatches) || requestedBatches > MaxProtocolPreparedObjects - warmups)
            throw new ArgumentException("Protocol preparation exceeds the 4096-object bound; reduce duration, rate or warmups");
        int count = (int)requestedBatches;
        long preparedBytes = 0;
        ConcurrentDictionary<Hash256, (BatchSample Sample, int Bytes, long Scheduled)> transactions = new();
        Dictionary<ValueHash256, BatchSample> byCommitment = [];
        List<(BatchSample Sample, byte[] Wrapper, Hash256[] Hashes)> prepared = [];
        long preparationStarted = Stopwatch.GetTimestamp();
        for (int index = 0; index < count + warmups; index++)
        {
            if (preparedBytes > MaxProtocolPreparedBytes - LeanProofStore.MaxWrapperBytes)
                throw new ArgumentException("Protocol preparation exceeds 512 MiB; reduce duration or rate");
            AggregationInput input = fixtures.Input(scenario, index);
            ValueHash256 commitment = Eip8288Dependencies.ComputeDepsHash(input.Deps);
            List<Transaction> txs = [];
            for (int i = 0; i < scenario.TransactionsPerBatch; i++)
            {
                FrameDependency dep = input.Deps[scenario.SharedSignature ? 0 : i];
                int identity = dep.Scheme == Eip8288Constants.LeanSphincsScheme ? index * scenario.SphincsCount + (scenario.SharedSignature ? 0 : i) : index;
                txs.Add(Transaction(receiver.Chain, dep, identity, scenario.SharedSignature ? i : identity % 16, index * 16 + i));
            }
            byte[] proof = NativeLeanProofVerifier.Instance.ProveRecursiveStark(commitment, Eip8288Constants.AggregatedVk, input);
            byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper()
            {
                Mode = MempoolWrapper.ModeRecursive,
                Deps = Eip8288Dependencies.Canonicalize(input.Deps),
                RecursiveStark = new(proof, new Hash256(commitment)),
                Transactions = txs.Select(t => new WrapperTransaction(t)).ToArray()
            }).Bytes;
            BatchSample sample = new()
            {
                Case = scenario.Name,
                Sequence = index,
                TransactionCount = txs.Count,
                ProofBytes = proof.Length,
                WrapperBytes = wrapper.Length
            };
            preparedBytes += wrapper.Length;
            byCommitment.Add(commitment, sample);
            foreach (Transaction tx in txs) transactions.TryAdd(tx.Hash!, (sample, Rlp.Encode(tx).Bytes.Length, 0));
            prepared.Add((sample, wrapper, txs.Select(tx => tx.Hash!).ToArray()));
        }
        double preparationSeconds = Stopwatch.GetElapsedTime(preparationStarted).TotalSeconds;
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(seconds + 180));
        verifier.Verified = (commitment, wall, cpuMs) =>
        {
            BatchSample sample = byCommitment[commitment];
            sample.ProofVerified = true;
            sample.VerifyWallMs += wall;
            sample.VerifyCpuMs += cpuMs;
        };
        receiver.Chain.TxPool.NewPending += (_, args) =>
        {
            if (transactions.TryRemove(args.Transaction.Hash!, out (BatchSample Sample, int Bytes, long Scheduled) pending))
            {
                pending.Sample.AcceptedUniqueTransactions++;
                pending.Sample.GoodputBytes += pending.Bytes;
                pending.Sample.LatencyMs = Stopwatch.GetElapsedTime(pending.Scheduled).TotalMilliseconds;
                receiver.Chain.TxPool.RemoveTransaction(args.Transaction.Hash);
            }
        };
        for (int index = 0; index < warmups; index++)
        {
            long scheduled = Stopwatch.GetTimestamp();
            foreach (Hash256 hash in prepared[index].Hashes)
                if (transactions.TryGetValue(hash, out (BatchSample Sample, int Bytes, long Scheduled) pending))
                    transactions[hash] = (pending.Sample, pending.Bytes, scheduled);
            await transport.TransferAsync(prepared[index].Wrapper, stop.Token);
            await receiver.DrainAsync(stop.Token);
            if (prepared[index].Sample.AcceptedUniqueTransactions != scenario.TransactionsPerBatch)
                throw new InvalidOperationException("Protocol warmup was not fully admitted");
        }
        prepared = prepared.Skip(warmups).ToList();
        long started = Stopwatch.GetTimestamp();
        double cpu = MeasuredVerifier.CpuMilliseconds();
        for (int index = 0; index < prepared.Count; index++)
        {
            long scheduled = started + (long)(index * scenario.TransactionsPerBatch / rate * Stopwatch.Frequency);
            await WaitUntil(scheduled, stop.Token);
            BatchSample sample = prepared[index].Sample;
            foreach (Hash256 hash in prepared[index].Hashes)
                if (transactions.TryGetValue(hash, out (BatchSample Sample, int Bytes, long Scheduled) pending))
                    transactions[hash] = (sample, pending.Bytes, scheduled);
            double networkCpu = MeasuredVerifier.CpuMilliseconds();
            TransportSample delivered = await transport.TransferAsync(prepared[index].Wrapper, stop.Token);
            sample.WireBytes = delivered.WireBytes;
            sample.EncodeWallMs = delivered.EncodeMs;
            sample.TransferWallMs = delivered.TransferMs;
            sample.NetworkCpuMs = MeasuredVerifier.CpuMilliseconds() - networkCpu;
        }
        double actualSendDuration = Stopwatch.GetElapsedTime(started).TotalSeconds;
        await WaitUntil(started + (long)(seconds * Stopwatch.Frequency), stop.Token);
        await receiver.DrainAsync(stop.Token);
        double duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
        if (receiver.DisconnectReason is not null) throw new InvalidOperationException(receiver.DisconnectReason);
        List<BatchSample> samples = prepared.Select(p => p.Sample).ToList();
        long replaced = samples.Count(s => !s.ProofVerified);
        foreach (BatchSample sample in samples)
            if (!sample.ProofVerified) sample.RejectionReason = "Local ingress, pending-wrapper or scheduler capacity/deadline drop";
            else if (sample.AcceptedUniqueTransactions != sample.TransactionCount) sample.RejectionReason = "Local pool admission decline";
        Rows.Add(Summarize("protocol-preproved", scenario, samples, rate, seconds, duration,
            count * scenario.TransactionsPerBatch, count, replaced * scenario.TransactionsPerBatch, replaced,
            MeasuredVerifier.CpuMilliseconds() - cpu, preparationSeconds, actualSendDuration));
        Samples.AddRange(samples);
        Console.WriteLine($"Protocol {scenario.Name} target {rate:F0}: accepted {Rows[^1].AcceptedTxPerSecond:F2}tx/s ingress drops {replaced} preparation {preparationSeconds:F2}s");
    }

    private static async Task RunObjects(int size, double seconds, double rate, int capacity, int warmups)
    {
        byte[] payload = new byte[size];
        new Random(17342).NextBytes(payload);
        using TcpRlpxLoopback transport = new();
        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(seconds + 180));
        for (int i = 0; i < warmups; i++) await transport.TransferAsync(payload, stop.Token);
        BenchCase scenario = new("object-" + size, 0, 0);
        List<BatchSample> samples = [];
        Channel<(long Sequence, long Offered)> queue = Channel.CreateBounded<(long, long)>(capacity);
        long offered = 0, dropped = 0, started = Stopwatch.GetTimestamp();
        double cpu = MeasuredVerifier.CpuMilliseconds();
        Task consumer = Task.Run(async () =>
        {
            await foreach ((long sequence, long scheduled) in queue.Reader.ReadAllAsync(stop.Token))
            {
                double networkCpu = MeasuredVerifier.CpuMilliseconds();
                TransportSample delivered = await transport.TransferAsync(payload, stop.Token);
                if (!delivered.Payload.AsSpan().SequenceEqual(payload)) throw new InvalidOperationException("Transport corruption");
                samples.Add(new()
                {
                    Case = scenario.Name,
                    Sequence = sequence,
                    TransactionCount = 0,
                    ProofBytes = 0,
                    WrapperBytes = size,
                    GoodputBytes = size,
                    WireBytes = delivered.WireBytes,
                    EncodeWallMs = delivered.EncodeMs,
                    TransferWallMs = delivered.TransferMs,
                    NetworkCpuMs = MeasuredVerifier.CpuMilliseconds() - networkCpu,
                    LatencyMs = Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds
                });
            }
        });
        for (long sequence = 0; sequence / rate < seconds; sequence++)
        {
            long scheduled = started + (long)(sequence / rate * Stopwatch.Frequency);
            await WaitUntil(scheduled, stop.Token);
            offered++;
            if (!queue.Writer.TryWrite((sequence, scheduled))) dropped++;
        }
        await WaitUntil(started + (long)(seconds * Stopwatch.Frequency), stop.Token);
        queue.Writer.Complete();
        await consumer;
        double duration = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Rows.Add(Summarize("transport", scenario, samples, 0, seconds, duration, 0, offered, 0, dropped, MeasuredVerifier.CpuMilliseconds() - cpu));
        Samples.AddRange(samples);
        Console.WriteLine($"TCP object {size}B target {rate:F0}/s: {Rows[^1].GoodputMbps:F1}Mbps delivered {samples.Count} dropped {dropped}");
    }

    private static BenchmarkRow Summarize(string kind, BenchCase scenario, List<BatchSample> samples, double targetRate,
        double offeredDuration, double duration, long offeredTransactions, long offeredObjects, long droppedTransactions, long droppedObjects, double cpu, double preparationSeconds = 0, double actualSendDuration = 0)
    {
        double[] latency = samples.Where(s => kind != "protocol-preproved" || s.AcceptedUniqueTransactions > 0).Select(s => s.LatencyMs).Order().ToArray();
        return new()
        {
            Kind = kind,
            Case = scenario.Name,
            SphincsCount = scenario.SphincsCount,
            StarkCount = scenario.StarkCount,
            TransactionsPerBatch = kind is "load" or "protocol-preproved" ? scenario.TransactionsPerBatch : 0,
            MeasuredBatches = samples.Count,
            TargetTxPerSecond = targetRate,
            OfferedTxPerSecond = offeredDuration == 0 ? 0 : offeredTransactions / Math.Max(offeredDuration, actualSendDuration),
            OfferedObjectsPerSecond = offeredDuration == 0 ? 0 : offeredObjects / offeredDuration,
            OfferedDurationSeconds = offeredDuration,
            ActualSendDurationSeconds = actualSendDuration,
            DurationSeconds = duration,
            PreparationSeconds = preparationSeconds,
            OfferedTransactions = offeredTransactions,
            OfferedObjects = offeredObjects,
            DroppedTransactions = droppedTransactions,
            DroppedObjects = droppedObjects,
            RejectedTransactions = samples.Sum(s => s.TransactionCount - s.AcceptedUniqueTransactions - s.RepeatedAcceptances) - (kind == "protocol-preproved" ? droppedTransactions : 0),
            AcceptedUniqueTransactions = samples.Sum(s => s.AcceptedUniqueTransactions),
            RepeatedAcceptances = samples.Sum(s => s.RepeatedAcceptances),
            GoodputBytes = samples.Sum(s => (long)s.GoodputBytes),
            PayloadBytes = samples.Sum(s => (long)s.WrapperBytes),
            WireBytes = samples.Sum(s => (long)s.WireBytes),
            ProofBytesMean = samples.Count == 0 ? 0 : samples.Average(s => s.ProofBytes),
            WrapperBytesMean = samples.Count == 0 ? 0 : samples.Average(s => s.WrapperBytes),
            P50LatencyMs = NearestRank(latency, .50),
            P95LatencyMs = NearestRank(latency, .95),
            ProveWallMs = samples.Sum(s => s.ProveWallMs),
            VerifyWallMs = samples.Sum(s => s.VerifyWallMs),
            EncodeWallMs = samples.Sum(s => s.EncodeWallMs),
            TransferWallMs = samples.Sum(s => s.TransferWallMs),
            AdmissionWallMs = samples.Sum(s => s.AdmissionWallMs),
            ProveCpuMs = samples.Sum(s => s.ProveCpuMs),
            VerifyCpuMs = samples.Sum(s => s.VerifyCpuMs),
            NetworkCpuMs = samples.Sum(s => s.NetworkCpuMs),
            ProcessCpuMs = cpu,
            RejectionReasons = samples.Where(s => s.RejectionReason is not null).GroupBy(s => s.RejectionReason!).ToDictionary(g => g.Key, g => g.Count())
        };
    }

    private static double NearestRank(double[] ordered, double percentile)
        => ordered.Length == 0 ? 0 : ordered[(int)Math.Ceiling(ordered.Length * percentile) - 1];

    private static async Task WaitUntil(long timestamp, CancellationToken token)
    {
        while (Stopwatch.GetTimestamp() < timestamp)
        {
            double remaining = (timestamp - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, remaining)), token);
        }
    }

    private static void WriteCsv<T>(string file, List<T> records)
    {
        System.Reflection.PropertyInfo[] properties = typeof(T).GetProperties();
        using StreamWriter writer = new(file);
        writer.WriteLine(string.Join(',', properties.Select(p => char.ToLowerInvariant(p.Name[0]) + p.Name[1..])));
        foreach (T record in records)
            writer.WriteLine(string.Join(',', properties.Select(p => '"' + (p.GetValue(record) is IDictionary<string, int> reasons ? JsonSerializer.Serialize(reasons) : Convert.ToString(p.GetValue(record), CultureInfo.InvariantCulture)!).Replace("\"", "\"\"") + '"')));
    }
}
