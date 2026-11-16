// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

/// <summary>Drives <see cref="RecursiveStarkAggregator.Prove"/> over real packaged signatures, so the managed batching
/// path and the native backend are exercised together rather than the C ABI alone.</summary>
[Explicit("Native proving; run deliberately.")]
public class NativeDirectBatchTests
{
    /// <summary>Distinct leanSTARK dependencies from a pool built with the package's <c>fixtures --prove-stark</c>, as a
    /// direct aggregation input. The pool path comes from <c>LEAN_STARK_POOL</c>; without it the case is skipped, since the
    /// package ships one leanSTARK vector and the guest rejects a repeated claim.</summary>
    /// <remarks>Record layout matches the prover benchmark harness: 96-byte dependency (scheme at [31], data hash at
    /// [32..64], verification key at [64..96]), then the witness length as little-endian <c>u32</c>, then the witness.</remarks>
    private static AggregationInput PooledGenerics(int count)
    {
        string path = Environment.GetEnvironmentVariable("LEAN_STARK_POOL");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            Assert.Ignore("set LEAN_STARK_POOL to a pool built with the package's fixtures tool");
        byte[] pool = File.ReadAllBytes(path);
        List<FrameDependency> deps = new(count);
        List<ReadOnlyMemory<byte>> witnesses = new(count);
        for (int i = 0, offset = 0; i < count; i++)
        {
            Assert.That(offset, Is.LessThan(pool.Length), "the pool holds fewer dependencies than requested");
            int length = BinaryPrimitives.ReadInt32LittleEndian(pool.AsSpan(offset + 96, 4));
            Assert.That(pool[offset + 31], Is.EqualTo(Eip8288Constants.LeanStarkScheme));
            deps.Add(new FrameDependency(Eip8288Constants.LeanStarkScheme, new ValueHash256(pool.AsSpan(offset + 32, 32)),
                new ValueHash256(pool.AsSpan(offset + 64, 32))));
            witnesses.Add(pool.AsSpan(offset + 100, length).ToArray());
            offset += 100 + length;
        }
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
        Assert.That(canonical, Has.Count.EqualTo(count), "the pool must hold distinct claims");
        List<ReadOnlyMemory<byte>> ordered = new(canonical.Count);
        foreach (FrameDependency dependency in canonical) ordered.Add(witnesses[deps.IndexOf(dependency)]);
        return new AggregationInput { Deps = canonical, Witnesses = ordered };
    }

    /// <summary>Appends one measurement to the file named by <c>LEAN_BATCH_REPORT</c>, and to the test output.</summary>
    private static void Report(string line)
    {
        TestContext.Error.WriteLine(line);
        string target = Environment.GetEnvironmentVariable("LEAN_BATCH_REPORT");
        if (!string.IsNullOrEmpty(target)) lock (ReportLock) File.AppendAllText(target, line + Environment.NewLine);
    }

    private static readonly object ReportLock = new();

    private static long PeakRssKb()
    {
        foreach (string line in File.ReadLines("/proc/self/status"))
            if (line.StartsWith("VmHWM", StringComparison.Ordinal))
                return long.Parse(line.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1]);
        return 0;
    }

    /// <summary>What a direct leaf of leanSTARK witnesses costs, which <see cref="RecursiveStarkAggregator.DirectBatchSize"/>
    /// bounds alongside signatures.</summary>
    /// <remarks>A leanSTARK witness is about 163 KB against a signature's 6 KB, and mode 1 does not cap generics at one per
    /// wrapper, so a whole leaf of them is reachable. Reports time, native calls and peak RSS; asserts only that the proof verifies, so it holds on any host.</remarks>
    [Test]
    public void Generic_leaf_cost([Values(1, 2, 4, 8, 16)] int count)
    {
        AggregationInput input = PooledGenerics(count);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(input.Deps);
        CountingVerifier verifier = new();
        long witnessBytes = 0;
        foreach (ReadOnlyMemory<byte> witness in input.Witnesses) witnessBytes += witness.Length;

        Stopwatch watch = Stopwatch.StartNew();
        byte[] proof = RecursiveStarkAggregator.Prove(input, verifier, Eip8288Verifier.Pinned, in depsHash);
        TimeSpan elapsed = watch.Elapsed;

        Report($"count={count},ms={elapsed.TotalMilliseconds:F0},calls={verifier.ProofCalls},"
            + $"witness_bytes={witnessBytes},proof_bytes={proof.Length},peak_rss_kb={PeakRssKb()}");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(proof, Has.Length.LessThanOrEqualTo(Eip8288Constants.MaxAggregateProofBytes));
            Assert.That(NativeLeanProofVerifier.Instance.VerifyRecursiveStark(depsHash, Eip8288Constants.AggregatedVk, proof), Is.True);
        }
    }

    /// <summary>The 16 distinct-key leanSPHINCS claims the package ships, as a direct aggregation input.</summary>
    private static AggregationInput PackagedSignatures(int count)
    {
        byte[] fixtures = NativeLeanProofVerifierTests.Fixture("sphincs-multi-keys");
        int entry = 32 + Eip8288Constants.LeanSphincsWitnessBytes;
        Assert.That(count, Is.LessThanOrEqualTo(fixtures.Length / entry), "the package ships fewer claims than requested");
        List<FrameDependency> deps = new(count);
        List<ReadOnlyMemory<byte>> witnesses = new(count);
        for (int i = 0; i < count; i++)
        {
            int offset = i * entry;
            deps.Add(new FrameDependency(Eip8288Constants.LeanSphincsScheme, new ValueHash256(fixtures.AsSpan(offset, 32)),
                new ValueHash256(fixtures.AsSpan(offset + 32, 32))));
            witnesses.Add(fixtures.AsSpan(offset + 32, Eip8288Constants.LeanSphincsWitnessBytes).ToArray());
        }
        List<FrameDependency> canonical = Eip8288Dependencies.Canonicalize(deps);
        List<ReadOnlyMemory<byte>> ordered = new(canonical.Count);
        foreach (FrameDependency dependency in canonical) ordered.Add(witnesses[deps.IndexOf(dependency)]);
        return new AggregationInput { Deps = canonical, Witnesses = ordered };
    }

    /// <summary>A wrapper-sized fresh-signature set takes one native call and its proof verifies under the pinned key.</summary>
    /// <remarks>With a four-signature leaf the same set took seven calls (four leaves and three merges). The assertion is on
    /// the call count, not on wall time, so it holds on any host; the timing is reported for the record.</remarks>
    [Test]
    public void Wrapper_sized_signature_set_proves_in_one_native_call(
        [Values(1, 2, 4, 8, Eip8288Constants.MaxLeanSigDepsPerWrapper)] int count)
    {
        AggregationInput input = PackagedSignatures(count);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(input.Deps);
        CountingVerifier verifier = new();

        Stopwatch watch = Stopwatch.StartNew();
        byte[] proof = RecursiveStarkAggregator.Prove(input, verifier, Eip8288Verifier.Pinned, in depsHash);
        TimeSpan elapsed = watch.Elapsed;

        TestContext.Out.WriteLine($"{count} signatures: {elapsed.TotalMilliseconds:F0} ms, {verifier.ProofCalls} native proof calls, {proof.Length} B");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.ProofCalls, Is.EqualTo(1), "a wrapper-sized set fits one direct leaf");
            Assert.That(proof, Has.Length.LessThanOrEqualTo(Eip8288Constants.MaxAggregateProofBytes));
            Assert.That(NativeLeanProofVerifier.Instance.VerifyRecursiveStark(depsHash, Eip8288Constants.AggregatedVk, proof), Is.True);
        }
    }

    /// <summary>A proven subset extended with fresh signatures still folds recursively, and the result still verifies.</summary>
    /// <remarks>Two calls, not three: the extending step takes the child proof and the remaining fresh witnesses together,
    /// because they fit one leaf. With a four-signature leaf the same input needed a separate leaf and then a merge.</remarks>
    [Test]
    public void Sets_beyond_one_leaf_still_merge_recursively()
    {
        AggregationInput whole = PackagedSignatures(Eip8288Constants.MaxLeanSigDepsPerWrapper);
        List<FrameDependency> deps = [.. whole.Deps];
        List<ReadOnlyMemory<byte>> witnesses = [.. whole.Witnesses];
        AggregationInput first = new() { Deps = deps.GetRange(0, 9), Witnesses = witnesses.GetRange(0, 9) };
        AggregationInput second = new() { Deps = deps.GetRange(9, deps.Count - 9), Witnesses = witnesses.GetRange(9, witnesses.Count - 9) };
        CountingVerifier verifier = new();
        Eip8288Verifier forkVerifier = Eip8288Verifier.Pinned;

        ValueHash256 firstHash = Eip8288Dependencies.ComputeDepsHash(first.Deps);
        byte[] firstProof = RecursiveStarkAggregator.Prove(first, verifier, forkVerifier, in firstHash);
        ValueHash256 wholeHash = Eip8288Dependencies.ComputeDepsHash(whole.Deps);
        AggregationInput extended = new()
        {
            RecursiveProofs = [new RecursiveProofInput(first.Deps, firstProof)],
            Deps = second.Deps,
            Witnesses = second.Witnesses
        };
        byte[] proof = RecursiveStarkAggregator.Prove(extended, verifier, forkVerifier, in wholeHash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(verifier.ProofCalls, Is.EqualTo(2), "one leaf, then one call folding the child with the rest");
            Assert.That(NativeLeanProofVerifier.Instance.VerifyRecursiveStark(wholeHash, Eip8288Constants.AggregatedVk, proof), Is.True);
        }
    }

    private const int GenericLeaf = 4;

    public enum CallShape { FourSignatureLeaf, SixteenSignatureLeaf, MergeOfTwoEightSignatureProofs, FourGenericLeaf, SixteenSignaturesAndFourGenericsLeaf }

    /// <summary>The wall time of one noninterruptible native proving call of each leaf or merge shape.</summary>
    /// <remarks>Background folds yield to a waiting producer only between native calls, so the longest single call bounds
    /// how long a producer can wait behind one. Four leanSTARK witnesses were the longest leaf four-wide leaves could issue;
    /// sixteen signatures with four leanSTARK witnesses would be longer, which is why a leaf holding any leanSTARK
    /// dependency stays four wide. Reports time and peak RSS; asserts only that the proof verifies.</remarks>
    [Test]
    public void Single_call_cost([Values] CallShape shape)
    {
        AggregationInput signatures = PackagedSignatures(Eip8288Constants.MaxLeanSigDepsPerWrapper);
        AggregationInput input = shape switch
        {
            CallShape.FourSignatureLeaf => Take(signatures, 0, 4),
            CallShape.SixteenSignatureLeaf => signatures,
            CallShape.MergeOfTwoEightSignatureProofs => new()
            {
                RecursiveProofs = [ProvenChild(Take(signatures, 0, 8)), ProvenChild(Take(signatures, 8, 8))]
            },
            CallShape.FourGenericLeaf => PooledGenerics(GenericLeaf),
            _ => Union(signatures, PooledGenerics(GenericLeaf)),
        };
        List<FrameDependency> statement = [.. input.Deps];
        foreach (RecursiveProofInput child in input.RecursiveProofs) statement.AddRange(child.InnerDeps);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.Canonicalize(statement));

        // The first call in a process also pays the backend's one-time setup, so the second is the one measured.
        NativeLeanProofVerifier.Instance.ProveRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk, input);
        Stopwatch watch = Stopwatch.StartNew();
        byte[] proof = NativeLeanProofVerifier.Instance.ProveRecursiveStark(in depsHash, Eip8288Constants.AggregatedVk, input);
        TimeSpan elapsed = watch.Elapsed;

        Report($"shape={shape},ms={elapsed.TotalMilliseconds:F0},proof_bytes={proof.Length},peak_rss_kb={PeakRssKb()}");
        Assert.That(NativeLeanProofVerifier.Instance.VerifyRecursiveStark(depsHash, Eip8288Constants.AggregatedVk, proof), Is.True);

        static AggregationInput Take(AggregationInput source, int start, int count) => new()
        {
            Deps = [.. source.Deps.Skip(start).Take(count)],
            Witnesses = [.. source.Witnesses.Skip(start).Take(count)]
        };

        static RecursiveProofInput ProvenChild(AggregationInput leaf)
        {
            ValueHash256 hash = Eip8288Dependencies.ComputeDepsHash(leaf.Deps);
            return new(leaf.Deps, NativeLeanProofVerifier.Instance.ProveRecursiveStark(in hash, Eip8288Constants.AggregatedVk, leaf));
        }

        static AggregationInput Union(AggregationInput a, AggregationInput b)
        {
            List<FrameDependency> aDeps = [.. a.Deps], bDeps = [.. b.Deps];
            List<FrameDependency> deps = Eip8288Dependencies.Canonicalize([.. aDeps, .. bDeps]);
            List<ReadOnlyMemory<byte>> witnesses = new(deps.Count);
            foreach (FrameDependency dependency in deps)
            {
                int i = aDeps.IndexOf(dependency);
                witnesses.Add(i >= 0 ? a.Witnesses[i] : b.Witnesses[bDeps.IndexOf(dependency)]);
            }
            return new() { Deps = deps, Witnesses = witnesses };
        }
    }

    private sealed class CountingVerifier : ILeanProofVerifier
    {
        public int ProofCalls { get; private set; }
        public void EnsureAvailable() => NativeLeanProofVerifier.Instance.EnsureAvailable();
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) =>
            NativeLeanProofVerifier.Instance.VerifyLeanSphincs(in dataHash, in verificationKey, witness);
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) =>
            NativeLeanProofVerifier.Instance.VerifyLeanStark(in dataHash, in verificationKey, witness);
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) =>
            NativeLeanProofVerifier.Instance.VerifyRecursiveStark(in depsHash, aggregatedVk, proof);
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input)
        {
            ProofCalls++;
            return NativeLeanProofVerifier.Instance.ProveRecursiveStark(in depsHash, aggregatedVk, input);
        }
    }
}
