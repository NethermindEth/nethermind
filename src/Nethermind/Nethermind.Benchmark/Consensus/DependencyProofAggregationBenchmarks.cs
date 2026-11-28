// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#nullable enable

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Benchmarks.Consensus;

/// <summary>EIP-8288 builder aggregation: folds the leanSPHINCS witnesses of <see cref="Claims"/> claims into the recursive
/// STARK of one <c>0x01</c> dependency proof, as <see cref="AggregatingBlockProcessor"/> does for a block it builds.</summary>
/// <remarks>
/// <para><see cref="Prove"/> is the production schedule: the fewest equal leaves of up to
/// <see cref="Eip8288Constants.MaxRawWitnessesPerStep"/> signatures, merged eight at a time. <see cref="ProveNarrow"/> is the
/// former one, leaves of sixteen merged pairwise, kept as the baseline the wide schedule has to beat.</para>
/// <para>Linux only: the native backend and the tool that signs the claims ship for linux-x64 and linux-arm64. Each case
/// appends a JSON line of what BenchmarkDotNet does not measure (CPU cores used, peak RSS, native calls, proof sizes, the
/// broadcast time of each proof form and the final proof's verify time) to <c>$EIP8288_BENCH_RESULTS/builder.jsonl</c>. Run
/// under <c>--quick</c>, as <c>scripts/eip8288-bench/builder.sh</c> does: each case runs in a process of its own, so its peak
/// RSS is its own.</para>
/// </remarks>
public class DependencyProofAggregationBenchmarks
{
    /// <summary>EIP-7870's upload bandwidth for a local block builder, in bits per second.</summary>
    private const double BuilderUploadBitsPerSecond = 50_000_000;

    private const int NarrowLeafSignatures = 16;

    [ParamsSource(nameof(ClaimCounts))]
    public int Claims { get; set; }

    /// <summary>The claim counts to run: <c>$EIP8288_BENCH_CLAIMS</c> (comma-separated), or 16, 64, 256 and 500.</summary>
    public static IEnumerable<int> ClaimCounts() =>
        Environment.GetEnvironmentVariable("EIP8288_BENCH_CLAIMS") is { Length: > 0 } counts
            ? counts.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(int.Parse)
            : [16, 64, 256, 500];

    private AggregationInput _input = null!;
    private ValueHash256 _depsHash;
    private byte[] _rawProof = [];
    private CountingVerifier _verifier = null!;
    private byte[] _lastProof = [];
    private readonly Stopwatch _wall = new();
    private readonly List<double> _iterationMs = [];
    private TimeSpan _cpu;
    private TimeSpan _cpuAtStart;
    private bool _narrow;

    [GlobalSetup]
    public void GlobalSetup()
    {
        NativeLeanProofVerifier.Instance.EnsureAvailable();
        (ValueHash256 key, List<(ValueHash256 Message, byte[] Signature)> claims) = SignaturePool.Load(Claims);

        List<AggregatedDependency> deps = [];
        Dictionary<AggregatedDependency, ReadOnlyMemory<byte>> witnessOf = [];
        List<byte[]> rawWitnesses = [];
        foreach ((ValueHash256 message, byte[] signature) in claims)
        {
            AggregatedDependency dependency = Eip8288Dependencies.Map(key, message);
            byte[] witness = new byte[Eip8288Constants.LeanSphincsWitnessBytes];
            key.Bytes.CopyTo(witness);
            signature.CopyTo(witness.AsSpan(ValueHash256.MemorySize));
            deps.Add(dependency);
            witnessOf[dependency] = witness;
            rawWitnesses.Add(signature);
        }

        List<AggregatedDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
        _input = new AggregationInput { Deps = canonical, Witnesses = [.. canonical.Select(d => witnessOf[d])] };
        _depsHash = Eip8288Dependencies.ComputeDepsHash(canonical);
        _rawProof = FrameTxDependencyProof.Encode(rawWitnesses);
        _verifier = new CountingVerifier(NativeLeanProofVerifier.Instance);
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _cpuAtStart = Process.GetCurrentProcess().TotalProcessorTime;
        _wall.Restart();
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        _wall.Stop();
        _iterationMs.Add(_wall.Elapsed.TotalMilliseconds);
        _cpu += Process.GetCurrentProcess().TotalProcessorTime - _cpuAtStart;
    }

    [Benchmark]
    public byte[] Prove()
    {
        _verifier.Proofs++;
        return _lastProof = DependencyProofAggregator.Prove(_input, _verifier, _depsHash);
    }

    [Benchmark(Baseline = true)]
    public byte[] ProveNarrow() => _lastProof = ProveInLeavesOf(NarrowLeafSignatures);

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        if (_lastProof.Length == 0) return;

        double[] verifyMs = new double[5];
        for (int i = 0; i < verifyMs.Length; i++)
        {
            long started = Stopwatch.GetTimestamp();
            if (!NativeLeanProofVerifier.Instance.VerifyDependencyProof(in _depsHash, Eip8288Constants.AggregatedVk, _lastProof))
                throw new InvalidOperationException($"the {Claims}-claim proof does not verify");
            verifyMs[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        byte[] aggregatedProof = AggregatedDependencyProof.Encode(_lastProof, []);
        string? directory = Environment.GetEnvironmentVariable("EIP8288_BENCH_RESULTS");
        if (string.IsNullOrEmpty(directory)) return;

        Directory.CreateDirectory(directory);
        string line = JsonSerializer.Serialize(new
        {
            schedule = _narrow ? "narrow" : "wide",
            claims = Claims,
            profile = Environment.GetEnvironmentVariable("EIP8288_BENCH_PROFILE") ?? "unspecified",
            processors = Environment.ProcessorCount,
            proveMs = Median([.. _iterationMs.Skip(1)]),
            slotMs = 12_000,
            cpuCores = _cpu.TotalMilliseconds / Math.Max(1, _iterationMs.Sum()),
            peakRssMiB = Process.GetCurrentProcess().PeakWorkingSet64 / (1024.0 * 1024.0),
            nativeCallsPerProof = _verifier.Calls / (double)Math.Max(1, _verifier.Proofs),
            starkBytes = _lastProof.Length,
            aggregatedProofBytes = aggregatedProof.Length,
            rawProofBytes = _rawProof.Length,
            aggregatedBroadcastMs = BroadcastMs(aggregatedProof.Length),
            rawBroadcastMs = BroadcastMs(_rawProof.Length),
            verifyMs = Median(verifyMs),
        });
        File.AppendAllLines(Path.Combine(directory, "builder.jsonl"), [line]);
    }

    private static double Median(double[] values)
    {
        if (values.Length == 0) return double.NaN;
        Array.Sort(values);
        return values[values.Length / 2];
    }

    private static double BroadcastMs(int bytes) => bytes * 8 / BuilderUploadBitsPerSecond * 1000;

    /// <summary>The former schedule: leaves of <paramref name="leaf"/> signatures in order, merged pairwise.</summary>
    private byte[] ProveInLeavesOf(int leaf)
    {
        _narrow = true;
        _verifier.Proofs++;
        List<RecursiveProofInput> level = [];
        for (int offset = 0; offset < _input.Deps.Count; offset += leaf)
        {
            int take = Math.Min(leaf, _input.Deps.Count - offset);
            List<AggregatedDependency> deps = [.. _input.Deps.Skip(offset).Take(take)];
            level.Add(Step(deps, new AggregationInput { Deps = deps, Witnesses = [.. _input.Witnesses.Skip(offset).Take(take)] }));
        }

        while (level.Count > 1)
        {
            List<RecursiveProofInput> next = [];
            for (int i = 0; i < level.Count; i += 2)
            {
                if (i + 1 == level.Count)
                {
                    next.Add(level[i]);
                    continue;
                }

                List<AggregatedDependency> deps = Eip8288Dependencies.Canonicalize([.. level[i].InnerDeps, .. level[i + 1].InnerDeps]);
                next.Add(Step(deps, new AggregationInput { RecursiveProofs = [level[i], level[i + 1]] }));
            }

            level = next;
        }

        return level[0].Proof.ToArray();
    }

    private RecursiveProofInput Step(List<AggregatedDependency> deps, AggregationInput input) =>
        new(deps, _verifier.ProveDependencyProof(Eip8288Dependencies.ComputeDepsHash(deps), Eip8288Constants.AggregatedVk, input));

    /// <summary>Counts the native proving calls a schedule makes.</summary>
    private sealed class CountingVerifier(ILeanProofVerifier inner) : ILeanProofVerifier
    {
        public int Calls;
        public int Proofs;

        public void EnsureAvailable() => inner.EnsureAvailable();

        public bool VerifyLeanSphincs(in ValueHash256 message, in ValueHash256 publicKey, ReadOnlySpan<byte> witness) =>
            inner.VerifyLeanSphincs(in message, in publicKey, witness);

        public bool VerifyDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) =>
            inner.VerifyDependencyProof(in depsHash, aggregatedVk, proof);

        public byte[] ProveDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            Calls++;
            return inner.ProveDependencyProof(in depsHash, aggregatedVk, input);
        }
    }

    /// <summary>Distinct claims signed once by the package's <c>fixtures</c> tool and cached beside the benchmark.</summary>
    private static class SignaturePool
    {
        /// <summary><c>$EIP8288_BENCH_VECTORS</c>, or <c>lean-vectors</c> beside the benchmark: BenchmarkDotNet builds each case
        /// into a directory of its own, which the package's test assets do not reach.</summary>
        private static string VectorsDirectory =>
            Environment.GetEnvironmentVariable("EIP8288_BENCH_VECTORS") is { Length: > 0 } vectors ? vectors : Path.Combine(AppContext.BaseDirectory, "lean-vectors");

        public static (ValueHash256 Key, List<(ValueHash256, byte[])> Claims) Load(int count)
        {
            ValueHash256 key = new(File.ReadAllBytes(Path.Combine(VectorsDirectory, "sphincs-key.bin")));
            string cache = Path.Combine(Path.GetTempPath(), $"eip8288-bench-claims-{Convert.ToHexString(key.Bytes[..8])}");
            Directory.CreateDirectory(cache);
            (ValueHash256, byte[])[] claims = new (ValueHash256, byte[])[count];
            Parallel.For(0, count, i =>
            {
                ValueHash256 message = Message(i);
                string path = Path.Combine(cache, $"{i}.sig");
                if (!File.Exists(path)) Sign(message, path);
                claims[i] = (message, File.ReadAllBytes(path));
            });

            if (!NativeLeanProofVerifier.Instance.Verify(in key, in claims[0].Item1, claims[0].Item2))
                throw new InvalidDataException($"{cache} does not verify under this package's leanSPHINCS key; delete it to sign again");
            return (key, [.. claims]);
        }

        private static ValueHash256 Message(int index)
        {
            Span<byte> seed = stackalloc byte[20];
            "eip8288-bench-v1"u8.CopyTo(seed);
            BinaryPrimitives.WriteInt32BigEndian(seed[16..], index);
            return ValueKeccak.Compute(seed);
        }

        private static void Sign(ValueHash256 message, string path)
        {
            string output = path + ".tmp";
            using Process process = Process.Start(new ProcessStartInfo(Path.Combine(VectorsDirectory, "fixtures"))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { "--sign-message", Convert.ToHexString(message.Bytes), output }
            })!;
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"fixtures --sign-message failed: {error}");
            byte[] witness = File.ReadAllBytes(output);
            // The tool writes the key ahead of the signature, as the native backend takes a witness.
            File.WriteAllBytes(path, witness.Length == Eip8288Constants.LeanSphincsWitnessBytes ? witness[ValueHash256.MemorySize..] : witness);
            File.Delete(output);
        }
    }
}
