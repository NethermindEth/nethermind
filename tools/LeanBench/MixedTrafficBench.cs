// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Crypto;
using Nethermind.Consensus.Eip8288;

namespace Nethermind.Tools.LeanBench;

public static partial class Program
{
    private sealed record MixedPayload(string Name, byte[] Bytes, int Claims, int ProofBytes, double ProveMs, double VerifyMs);
    private sealed record MixedRow(string Kind, string Case, int ObjectBytes, int ChunkBytes, int Repetitions,
        double SimulatedWireMbps, double ProbeIntervalMs, double DurationSeconds, double ObjectDurationSeconds,
        long PayloadBytes, long WireBytes, double GoodputMbps, double ControlP50Ms, double ControlP95Ms,
        double ControlMaxMs, int ControlsOffered, int ControlsDelivered, int ControlDrops, int WriteDrops,
        double ProcessCpuMs, int Claims, int ProofBytes, double PreparationProveMs, double PreparationVerifyMs);
    private sealed record ObjectSample(int Sequence, double LatencyMs, int Bytes);

    private static async Task RunMixedTraffic(string[] args)
    {
        string Value(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith("--" + name + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? fallback;
        string output = Path.GetFullPath(Value("out", "lean-mixed-results"));
        int repetitions = int.Parse(Value("repetitions", "3"), CultureInfo.InvariantCulture);
        int[] chunks = Value("chunks", "0,32768,65536,131072").Split(',').Select(int.Parse).ToArray();
        int[] sizes = Value("object-sizes", "1048576,10485760").Split(',').Where(s => s.Length != 0).Select(int.Parse).ToArray();
        double wireMbps = double.Parse(Value("wire-mbps", "32"), CultureInfo.InvariantCulture);
        double probeMs = double.Parse(Value("probe-ms", "20"), CultureInfo.InvariantCulture);
        if (repetitions < 1 || repetitions > 100 || !double.IsFinite(wireMbps) || wireMbps <= 0
            || !double.IsFinite(probeMs) || probeMs < 5 || chunks.Any(c => c is not (0 or 32768 or 65536 or 131072))
            || sizes.Any(s => s < 1 || s > LeanProofStore.MaxWrapperBytes)) throw new ArgumentException("Invalid mixed-traffic bounds");
        Directory.CreateDirectory(output);
        List<MixedPayload> payloads = [];
        foreach (int size in sizes)
        {
            byte[] bytes = new byte[size];
            new Random(17342).NextBytes(bytes);
            payloads.Add(new("object-" + size, bytes, 0, 0, 0, 0));
        }
        string realCases = Value("proof-cases", "sphincs1,sphincs16,stark1,stark16");
        if (realCases.Length != 0)
        {
            Fixtures fixtures = new(Path.GetFullPath(Value("fixtures", "tools/lean-ffi/target/bench-vectors")));
            NativeLeanProofVerifier.Instance.EnsureAvailable();
            using BasicTestBlockchain chain = await BasicTestBlockchain.Create();
            foreach (string name in realCases.Split(','))
            {
                bool sphincs = name.StartsWith("sphincs", StringComparison.Ordinal);
                int count = name switch { "sphincs1" or "stark1" => 1, "sphincs16" or "stark16" => 16, _ => throw new ArgumentException("Unknown proof case") };
                WitnessFixture[] vectors = sphincs ? fixtures.Sphincs : fixtures.Starks;
                List<FrameDependency> deps = [];
                List<ReadOnlyMemory<byte>> witnesses = [];
                List<WrapperTransaction> transactions = [];
                for (int index = 0; index < count; index++)
                {
                    deps.Add(vectors[index].Dependency);
                    witnesses.Add(vectors[index].Witness);
                    transactions.Add(new(Transaction(chain, vectors[index].Dependency, index, index, index)));
                }
                AggregationInput input = new() { Deps = deps, Witnesses = witnesses };
                ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(deps);
                long proveStarted = Stopwatch.GetTimestamp();
                byte[] proof = NativeLeanProofVerifier.Instance.ProveRecursiveStark(hash, Eip8288Constants.AggregatedVk, input);
                double proveMs = Stopwatch.GetElapsedTime(proveStarted).TotalMilliseconds;
                long verifyStarted = Stopwatch.GetTimestamp();
                if (!NativeLeanProofVerifier.Instance.VerifyRecursiveStark(hash, Eip8288Constants.AggregatedVk, proof)) throw new InvalidDataException("Prepared proof invalid");
                double verifyMs = Stopwatch.GetElapsedTime(verifyStarted).TotalMilliseconds;
                byte[] bytes = name == "stark16" ? proof : MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper()
                {
                    Mode = MempoolWrapper.ModeRecursive,
                    Transactions = transactions,
                    Deps = Eip8288Dependencies.Canonicalize(deps),
                    RecursiveStark = new(proof, new Hash256(hash))
                }).Bytes;
                payloads.Add(new(name == "stark16" ? "block-stark16" : name, bytes, count, proof.Length, proveMs, verifyMs));
            }
        }
        List<MixedRow> rows = [];
        List<object> samples = [];
        foreach (MixedPayload payload in payloads)
            foreach (int chunk in chunks)
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
                await using MixedTrafficTransport transport = new(chunk, wireMbps);
                int offered = 0, controlDrops = 0;
                bool finished = false;
                long started = Stopwatch.GetTimestamp();
                double cpu = MeasuredVerifier.CpuMilliseconds();
                Task controls = Task.Run(async () =>
                {
                    for (ulong sequence = 0; !Volatile.Read(ref finished); sequence++)
                    {
                        long scheduled = started + (long)(sequence * probeMs / 1000 * Stopwatch.Frequency);
                        await WaitUntil(scheduled, timeout.Token);
                        offered++;
                        if (!transport.Probe(sequence, scheduled, (int)(sequence % 3))) controlDrops++;
                    }
                }, timeout.Token);
                await Task.Delay(50, timeout.Token);
                long objectStarted = Stopwatch.GetTimestamp();
                List<ObjectSample> objects = [];
                for (int sequence = 0; sequence < repetitions; sequence++)
                {
                    long scheduled = Stopwatch.GetTimestamp();
                    await transport.TransferAsync(payload.Bytes, timeout.Token);
                    objects.Add(new(sequence, Stopwatch.GetElapsedTime(scheduled).TotalMilliseconds, payload.Bytes.Length));
                }
                double objectSeconds = Stopwatch.GetElapsedTime(objectStarted).TotalSeconds;
                Volatile.Write(ref finished, true);
                await controls;
                while (transport.Controls.Length < offered - controlDrops)
                {
                    if (transport.WriteDrops != 0) throw new IOException("Paced TCP queue overflow; run is incomplete");
                    await Task.Delay(1, timeout.Token);
                }
                double seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
                ControlSample[] delivered = transport.Controls;
                double[] latencies = delivered.Select(s => s.LatencyMs).Order().ToArray();
                double Percentile(double p) => latencies.Length == 0 ? 0 : latencies[(int)Math.Ceiling(p * latencies.Length) - 1];
                MixedRow row = new("mixed-traffic", payload.Name, payload.Bytes.Length, chunk, repetitions,
                    wireMbps, probeMs, seconds, objectSeconds, (long)payload.Bytes.Length * repetitions, transport.WireBytes,
                    payload.Bytes.Length * repetitions * 8.0 / objectSeconds / 1_000_000,
                    Percentile(.5), Percentile(.95), latencies.Length == 0 ? 0 : latencies[^1], offered,
                    delivered.Length, controlDrops, transport.WriteDrops, MeasuredVerifier.CpuMilliseconds() - cpu,
                    payload.Claims, payload.ProofBytes, payload.ProveMs, payload.VerifyMs);
                rows.Add(row);
                samples.Add(new { payload.Name, chunkBytes = chunk, objects, controls = delivered });
                Console.WriteLine($"Mixed {payload.Name} chunk {chunk / 1024}KiB: {row.GoodputMbps:F2}Mbit/s control p95 {row.ControlP95Ms:F2}ms max {row.ControlMaxMs:F2}ms drops {row.ControlDrops}/{row.WriteDrops}");
                Save();
            }
        void Save()
        {
            object metadata = new
            {
                commandLine = Environment.CommandLine,
                sourceRevision = Value("source-revision", "working tree; see compiled hashes"),
                hardware = Value("hardware", "unspecified"),
                utc = DateTimeOffset.UtcNow,
                os = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription,
                logicalCpus = Environment.ProcessorCount,
                compiledAssemblySha256 = Directory.GetFiles(AppContext.BaseDirectory, "Nethermind.*.dll").Append(typeof(Program).Assembly.Location)
                    .ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))),
                nativeLibrarySha256 = Directory.GetFiles(AppContext.BaseDirectory, "*nethermind_lean*")
                    .ToDictionary(p => Path.GetFileName(p)!, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))),
                transport = "Real localhost TCP, production PacketSender/Snappy/AES/MAC RLPx; preset session secrets; no handshake timing",
                pacing = "Application wire-byte cap at terminal socket writer, 4KiB send quanta; not kernel network shaping; no simulated loss or WAN RTT",
                methodology = "Same payload repeated per row; fresh TCP codec state per row and production reassembly state per repetition to measure transfer rather than duplicate suppression. Controls cycle real serialized eth GetBlockHeaders, one-header BlockHeaders responses and p2p Ping. Latency is scheduled request-to-receiver delivery, not response RTT. Whole wrapper awaits one actual write; chunks await each actual write and yield. Wire bytes include controls and encrypted RLPx, excluding TCP/IP. Synthetic objects are incompressible. Real objects are natively proved and verified before timing; block-stark16 is a raw block proof envelope, not a mempool-valid wrapper (wrapper limit is one generic dependency); repeated delivery measures transport only, not admission or crypto throughput. Object goodput uses transfer interval; CPU uses whole row. Preparation cost is one observation per proof case, not a warmed crypto benchmark."
            };
            File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { metadata, results = rows, samples },
                new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
            WriteCsv(Path.Combine(output, "results.csv"), rows);
        }
    }
}
