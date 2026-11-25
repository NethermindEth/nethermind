// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Tools.Eip8288Bench;

/// <summary>Scenario B: a builder folds the fresh leanSPHINCS signatures of N mode-0 wrappers into the block's single
/// dependency proof in one hop, through the production <see cref="DependencyProofAggregator.Prove"/> path.</summary>
internal static class BuilderBench
{
    private sealed record Run(int Signatures, int Wrappers, int Repeat, bool Warmup, double ProveMs, double CpuMs, double CpuUtilisation,
        long PeakRssKiB, long BaselineRssKiB, int NativeCalls, int ProofBytes, double VerifyMs, double WrapperVerifyMs);

    public static int Execute(Options options)
    {
        int[] counts = options.IntList("counts", [20, 100, 200, 300, 500]);
        int warmups = options.Int("warmups", 1);
        int repeats = options.Int("repeats", 5);
        int slotMs = options.Int("slot-ms", 12_000);
        int reserveMs = options.Int("reserve-ms", 4_000);
        string outDir = options.Path("out", "eip8288-builder-results");
        string profile = options.String("profile", "unspecified");
        Directory.CreateDirectory(outDir);

        NativeLeanProofVerifier.Instance.EnsureAvailable();
        int max = 0;
        foreach (int count in counts) max = Math.Max(max, count);
        SignaturePool pool = SignaturePool.LoadOrCreate(options.Path("pool", Path.Combine(outDir, "signatures.bin")), max,
            options.Int("sign-parallelism", Environment.ProcessorCount));
        bool peakResets = HostInfo.ResetPeakRss();

        List<Run> runs = [];
        foreach (int count in counts)
        {
            List<FrameDependency> deps = [];
            for (int i = 0; i < count; i++) deps.Add(pool.Dependency(i));
            List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
            if (canonical.Count != count) throw new InvalidOperationException("signature pool holds repeated claims");
            Dictionary<FrameDependency, byte[]> witnessOf = [];
            for (int i = 0; i < count; i++) witnessOf[deps[i]] = pool.Records[i].Witness;
            List<ReadOnlyMemory<byte>> witnesses = new(count);
            foreach (FrameDependency dependency in canonical) witnesses.Add(witnessOf[dependency]);
            AggregationInput input = new() { Deps = canonical, Witnesses = witnesses };
            ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(canonical);
            int wrappers = (count + Eip8288Constants.MaxLeanSigDepsPerWrapper - 1) / Eip8288Constants.MaxLeanSigDepsPerWrapper;

            for (int repeat = 0; repeat < warmups + repeats; repeat++)
            {
                bool warmup = repeat < warmups;
                Run run = Measure(count, wrappers, repeat - warmups, warmup, input, canonical, depsHash, peakResets);
                runs.Add(run);
                Console.Error.WriteLine($"N={count,4} {(warmup ? "warmup" : $"rep {run.Repeat}")}: prove {run.ProveMs,9:F0} ms, cpu {run.CpuUtilisation,5:F1} cores, " +
                    $"peak {run.PeakRssKiB / 1024,6} MiB, {run.NativeCalls} native calls, proof {run.ProofBytes} B, verify {run.VerifyMs:F1} ms");
            }
            // Rewritten after every set, so a set the memory limit kills still leaves the smaller ones.
            Write(outDir, profile, runs, [.. counts.Where(c => runs.Exists(r => r.Signatures == c))], slotMs, reserveMs, repeats, warmups, peakResets);
        }
        return 0;
    }

    private static Run Measure(int count, int wrappers, int repeat, bool warmup, AggregationInput input, List<FrameDependency> canonical,
        ValueHash256 depsHash, bool peakResets)
    {
        // A builder receiving mode-0 wrappers checks each direct witness before it folds them.
        Stopwatch wrapperVerify = Stopwatch.StartNew();
        for (int i = 0; i < canonical.Count; i++)
            if (!NativeLeanProofVerifier.Instance.VerifyLeanSphincs(canonical[i].DataHash, canonical[i].VerificationKey, input.Witnesses[i].Span))
                throw new InvalidOperationException("a pooled signature failed verification");
        wrapperVerify.Stop();

        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        if (peakResets) HostInfo.ResetPeakRss();
        long baseline = HostInfo.RssKiB();
        CountingVerifier verifier = new();
        Process process = Process.GetCurrentProcess();
        process.Refresh();
        TimeSpan cpuBefore = process.TotalProcessorTime;
        Stopwatch prove = Stopwatch.StartNew();
        // A fresh cache per repeat: the production wrapper verifies every step it proves, and must not reuse an earlier repeat's steps.
        byte[] proof = DependencyProofAggregator.Prove(input, new DependencyProofCache(verifier), Eip8288Verifier.Pinned, in depsHash);
        prove.Stop();
        process.Refresh();
        double cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
        long peak = HostInfo.PeakRssKiB();

        // O(1) block-side check: the median of five verifications of the final proof.
        double[] verify = new double[5];
        for (int i = 0; i < verify.Length; i++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            bool valid = NativeLeanProofVerifier.Instance.VerifyDependencyProof(in depsHash, Eip8288Constants.AggregatedVk, proof);
            verify[i] = watch.Elapsed.TotalMilliseconds;
            if (!valid) throw new InvalidOperationException($"the {count}-signature proof does not verify");
        }
        Array.Sort(verify);
        return new(count, wrappers, repeat, warmup, prove.Elapsed.TotalMilliseconds, cpuMs,
            cpuMs / prove.Elapsed.TotalMilliseconds, peak, baseline, verifier.ProofCalls, proof.Length, verify[verify.Length / 2],
            wrapperVerify.Elapsed.TotalMilliseconds);
    }

    private static void Write(string outDir, string profile, List<Run> runs, int[] counts, int slotMs, int reserveMs, int repeats, int warmups,
        bool peakResets)
    {
        int budgetMs = slotMs - reserveMs;
        List<Dictionary<string, object?>> summaries = [];
        StringBuilder table = new();
        table.AppendLine($"Builder aggregation, profile `{profile}`: {repeats} repeats after {warmups} warm-up; slot {slotMs} ms, " +
            $"reserve {reserveMs} ms for block building and propagation, so a proving budget of {budgetMs} ms.");
        table.AppendLine();
        table.AppendLine("| N sigs | wrappers | prove p50 ms | prove max ms | CV | CPU cores | peak RSS MiB | native calls | proof B | verify ms | N-sig check ms | fits budget (max) |");
        table.AppendLine("|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|:---:|");
        foreach (int count in counts)
        {
            List<Run> measured = runs.FindAll(r => r.Signatures == count && !r.Warmup);
            Summary prove = Summary.Of(measured.ConvertAll(r => r.ProveMs));
            Summary cpu = Summary.Of(measured.ConvertAll(r => r.CpuUtilisation));
            Summary peak = Summary.Of(measured.ConvertAll(r => (double)r.PeakRssKiB / 1024));
            Summary verify = Summary.Of(measured.ConvertAll(r => r.VerifyMs));
            Summary wrapperVerify = Summary.Of(measured.ConvertAll(r => r.WrapperVerifyMs));
            Run first = measured[0];
            bool fits = prove.Max <= budgetMs;
            summaries.Add(new()
            {
                ["signatures"] = count,
                ["wrappers"] = first.Wrappers,
                ["proveMs"] = prove,
                ["cpuCores"] = cpu,
                ["peakRssMiB"] = peak,
                ["verifyMs"] = verify,
                ["wrapperVerifyMs"] = wrapperVerify,
                ["nativeCalls"] = first.NativeCalls,
                ["proofBytes"] = first.ProofBytes,
                ["budgetMs"] = budgetMs,
                ["fitsBudgetAtMax"] = fits,
                ["headroomAtMaxMs"] = budgetMs - prove.Max,
            });
            table.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"| {count} | {first.Wrappers} | {prove.P50:F0} | {prove.Max:F0} | {prove.Cv:P1} | {cpu.Mean:F1} | {peak.Max:F0} | {first.NativeCalls} | {first.ProofBytes} | {verify.P50:F1} | {wrapperVerify.P50:F0} | {(fits ? "yes" : "no")} |"));
        }

        Dictionary<string, object?> document = new()
        {
            ["scenario"] = "builder-aggregation",
            ["profile"] = profile,
            ["host"] = HostInfo.Capture(),
            ["versions"] = Versions(),
            ["parameters"] = new Dictionary<string, object?>
            {
                ["counts"] = counts,
                ["warmups"] = warmups,
                ["repeats"] = repeats,
                ["slotMs"] = slotMs,
                ["reserveMs"] = reserveMs,
                ["peakRssPerRepeat"] = peakResets,
            },
            ["summary"] = summaries,
            ["runs"] = runs,
        };
        File.WriteAllText(Path.Combine(outDir, "builder.json"), JsonSerializer.Serialize(document, Json.Options));
        StringBuilder csv = new("signatures,wrappers,repeat,warmup,prove_ms,cpu_ms,cpu_cores,peak_rss_kib,baseline_rss_kib,native_calls,proof_bytes,verify_ms,wrapper_verify_ms\n");
        foreach (Run r in runs)
            csv.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"{r.Signatures},{r.Wrappers},{r.Repeat},{r.Warmup},{r.ProveMs:F1},{r.CpuMs:F1},{r.CpuUtilisation:F2},{r.PeakRssKiB},{r.BaselineRssKiB},{r.NativeCalls},{r.ProofBytes},{r.VerifyMs:F2},{r.WrapperVerifyMs:F1}"));
        File.WriteAllText(Path.Combine(outDir, "builder.csv"), csv.ToString());
        File.WriteAllText(Path.Combine(outDir, "builder.md"), table.ToString());
    }

    internal static Dictionary<string, object?> Versions() => new()
    {
        ["nethermindCommit"] = Environment.GetEnvironmentVariable("BENCH_NETHERMIND_COMMIT"),
        ["leanPackage"] = Environment.GetEnvironmentVariable("BENCH_LEAN_PACKAGE"),
        ["leanAbi"] = NativeLeanProofVerifier.AbiVersion,
        ["aggregatedVk"] = Convert.ToHexString(NativeLeanProofVerifier.AggregatedVerificationKey).ToLowerInvariant(),
        ["maxRawWitnessesPerStep"] = Eip8288Constants.MaxRawWitnessesPerStep,
        ["maxRecursiveInputsPerStep"] = Eip8288Constants.MaxRecursiveInputsPerStep,
    };

    /// <summary>Counts native proving calls, so the summary shows how many leaves and merges a set needed.</summary>
    private sealed class CountingVerifier : ILeanProofVerifier
    {
        private int _proofCalls;
        public int ProofCalls => _proofCalls;
        public void EnsureAvailable() => NativeLeanProofVerifier.Instance.EnsureAvailable();
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
            => NativeLeanProofVerifier.Instance.VerifyLeanSphincs(in dataHash, in verificationKey, witness);
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness)
            => NativeLeanProofVerifier.Instance.VerifyLeanStark(in dataHash, in verificationKey, witness);
        public bool VerifyDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof)
            => NativeLeanProofVerifier.Instance.VerifyDependencyProof(in depsHash, aggregatedVk, proof);
        public byte[] ProveDependencyProof(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            Interlocked.Increment(ref _proofCalls);
            return NativeLeanProofVerifier.Instance.ProveDependencyProof(in depsHash, aggregatedVk, input);
        }
    }
}
