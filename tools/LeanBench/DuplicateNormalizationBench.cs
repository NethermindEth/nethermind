// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;

namespace Nethermind.Tools.LeanBench;

public static partial class Program
{
    private sealed record DuplicateSample(string Case, int Repetition, int ParentCount, int NativeProveCalls,
        int ShortWitnessBytes, int LongWitnessBytes, int OutputBytes, double FoldWallMs, double FoldCpuMs, double VerifyWallMs);

    private sealed class CountingProver : ILeanProofVerifier
    {
        public int Calls { get; private set; }
        public void EnsureAvailable() => NativeLeanProofVerifier.Instance.EnsureAvailable();
        public bool VerifyLeanSphincs(in ValueHash256 hash, in ValueHash256 key, ReadOnlySpan<byte> witness)
            => NativeLeanProofVerifier.Instance.VerifyLeanSphincs(hash, key, witness);
        public bool VerifyLeanStark(in ValueHash256 hash, in ValueHash256 key, ReadOnlySpan<byte> witness)
            => NativeLeanProofVerifier.Instance.VerifyLeanStark(hash, key, witness);
        public bool VerifyRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, ReadOnlySpan<byte> proof)
            => NativeLeanProofVerifier.Instance.VerifyRecursiveStark(hash, key, proof);
        public byte[] ProveRecursiveStark(in ValueHash256 hash, ReadOnlySpan<byte> key, AggregationInput input)
        {
            Calls++;
            return NativeLeanProofVerifier.Instance.ProveRecursiveStark(hash, key, input);
        }
    }

    private static void RunDuplicateNormalization(string[] args)
    {
        string Value(string name, string fallback) => args.FirstOrDefault(a => a.StartsWith("--" + name + "=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? fallback;
        string directory = Path.GetFullPath(Value("fixtures", "tools/lean-ffi/target/bench-vectors"));
        string output = Path.GetFullPath(Value("out", "lean-duplicate-results"));
        int warmups = int.Parse(Value("warmups", "1"), CultureInfo.InvariantCulture);
        int repetitions = int.Parse(Value("repetitions", "3"), CultureInfo.InvariantCulture);
        if (warmups < 0 || warmups > 100 || repetitions < 1 || repetitions > 100)
            throw new ArgumentException("Duplicate benchmark warmups/repetitions must be within 0/1–100");
        NativeLeanProofVerifier backend = NativeLeanProofVerifier.Instance;
        backend.EnsureAvailable();
        string[] fixtureNames = ["generic-duplicate-deps.bin", "generic-duplicate-short.bin", "generic-duplicate-long.bin", "generic-duplicate-unique.bin"];
        byte[][] fixtureBytes = fixtureNames.Select(name => File.ReadAllBytes(Path.Combine(directory, name))).ToArray();
        if (fixtureBytes[0].Length != 2 * Eip8288Constants.DependencyTripleLength)
            throw new InvalidDataException("Expected two duplicate benchmark dependency declarations");
        List<FrameDependency> dependencies = Eip8288Dependencies.Parse(fixtureBytes[0]);
        FrameDependency a = dependencies[0], b = dependencies[1];
        byte[] shorter = fixtureBytes[1], longer = fixtureBytes[2], unique = fixtureBytes[3];
        if (shorter.Length >= longer.Length || a.Equals(b)
            || !backend.VerifyLeanStark(a.DataHash, a.VerificationKey, shorter)
            || !backend.VerifyLeanStark(a.DataHash, a.VerificationKey, longer)
            || !backend.VerifyLeanStark(b.DataHash, b.VerificationKey, unique))
            throw new InvalidDataException("Duplicate fixtures must be distinct-length valid proofs for the same claim and one unique claim");
        long preparationStarted = Stopwatch.GetTimestamp();
        RecursiveProofInput shortParent = Parent(a, shorter), longParent = Parent(a, longer), uniqueParent = Parent(b, unique);
        double preparationSeconds = Stopwatch.GetElapsedTime(preparationStarted).TotalSeconds;
        (string Name, RecursiveProofInput[] Parents)[] cases =
        [
            ("two-unique-parents", [shortParent, uniqueParent]),
            ("three-equal-short-parents", [shortParent, uniqueParent, shortParent]),
            ("three-long-then-short-parents", [longParent, uniqueParent, shortParent])
        ];
        ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(dependencies);
        List<DuplicateSample> samples = [];
        foreach ((string name, RecursiveProofInput[] parents) in cases)
            for (int repetition = -warmups; repetition < repetitions; repetition++)
            {
                CountingProver counter = new();
                double cpu = MeasuredVerifier.CpuMilliseconds();
                long started = Stopwatch.GetTimestamp();
                byte[] proof = RecursiveStarkAggregator.Prove(new() { RecursiveProofs = parents }, counter, hash);
                double foldMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                double cpuMs = MeasuredVerifier.CpuMilliseconds() - cpu;
                started = Stopwatch.GetTimestamp();
                if (!backend.VerifyRecursiveStark(hash, Eip8288Constants.AggregatedVk, proof))
                    throw new InvalidDataException("Folded native proof did not verify");
                double verifyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Dictionary<FrameDependency, int> lengths = [];
                if (!LeanProofCapacity.TryReadGenericWitnessLengths(proof, lengths)
                    || lengths.Count != 2 || lengths[a] != shorter.Length || lengths[b] != unique.Length)
                    throw new InvalidDataException("Fold did not retain the shortest witness and unique claim");
                if (repetition >= 0)
                    samples.Add(new(name, repetition, parents.Length, counter.Calls, shorter.Length, longer.Length,
                        proof.Length, foldMs, cpuMs, verifyMs));
            }
        Directory.CreateDirectory(output);
        object metadata = new
        {
            sourceRevision = Value("source-revision", "working tree; see compiled hashes"),
            commandLine = Environment.CommandLine,
            utc = DateTimeOffset.UtcNow,
            backendCommit = BackendCommit,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            runtime = RuntimeInformation.FrameworkDescription,
            hardware = Value("hardware", "unspecified"),
            warmups,
            repetitions,
            preparationSeconds,
            fixtureSha256 = fixtureNames.Select((name, index) => (name, index)).ToDictionary(pair => pair.name,
                pair => Convert.ToHexString(SHA256.HashData(fixtureBytes[pair.index]))),
            compiledAssemblySha256 = Directory.GetFiles(AppContext.BaseDirectory, "Nethermind.*.dll").Append(typeof(Program).Assembly.Location)
                .ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))),
            nativeLibrarySha256 = Directory.GetFiles(AppContext.BaseDirectory, "*nethermind_lean*")
                .ToDictionary(path => Path.GetFileName(path)!, path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))),
            methodology = "Real sameclaim CPU STARKs using expansion factors 1/2, authenticated before timing; parents generated before timing. Sequential managed RecursiveStarkAggregator.Prove includes pruning, child merges and final native proof calls; final verification reported separately. Two-parent reference uses fast path; three-parent cases exercise slow fold. Equal-short repeated parent is a controlled direct-input comparison; production Combine deduplicates identical parent hashes. No pool admission, transport or SPHINCS work. Carries and reverifies generic witnesses; no generic recursive compression."
        };
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { metadata, samples },
            new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        WriteCsv(Path.Combine(output, "samples.csv"), samples);
        foreach (IGrouping<string, DuplicateSample> group in samples.GroupBy(sample => sample.Case))
            Console.WriteLine($"{group.Key}: fold {group.Average(sample => sample.FoldWallMs):F2}ms, native calls {group.First().NativeProveCalls}, output {group.First().OutputBytes} bytes");

        RecursiveProofInput Parent(FrameDependency dependency, byte[] witness)
        {
            ValueHash256 parentHash = Eip8288Dependencies.ComputeDepsHash([dependency]);
            byte[] proof = backend.ProveRecursiveStark(parentHash, Eip8288Constants.AggregatedVk,
                new() { Deps = [dependency], Witnesses = [witness] });
            if (!backend.VerifyRecursiveStark(parentHash, Eip8288Constants.AggregatedVk, proof))
                throw new InvalidDataException("Prepared parent did not verify");
            return new([dependency], proof);
        }
    }
}
