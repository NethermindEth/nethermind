// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core.Specs;

namespace Nethermind.Evm.CodeAnalysis.IlEvm;

/// <summary>
/// The compiled form of one bytecode under one spec: per-block compiled segments plus the
/// program-counter index to find them. Immutable after construction; published to
/// <see cref="CodeInfo"/> with a volatile write and shared by all executing threads.
/// </summary>
public sealed class IlCompiledCode
{
    private readonly AnalyzedCode _analyzed;
    private readonly IlCompiledSegment?[] _segmentsByBlockIndex;
    // One byte per code byte, non-zero only at pcs where a compiled segment starts. The
    // dispatch loop probes EVERY iteration, so the miss path must be a single hot-cache load
    // and a predicted-not-taken branch; the two-array walk happens only on actual hits.
    private readonly byte[] _segmentStartMap;

    internal IlCompiledCode(AnalyzedCode analyzed, IlCompiledSegment?[] segmentsByBlockIndex, IReleaseSpec spec, int segmentCount)
    {
        _analyzed = analyzed;
        _segmentsByBlockIndex = segmentsByBlockIndex;
        Spec = spec;
        SegmentCount = segmentCount;

        _segmentStartMap = new byte[analyzed.CodeLength];
        ReadOnlySpan<BasicBlock> blocks = analyzed.Blocks;
        for (int i = 0; i < segmentsByBlockIndex.Length; i++)
        {
            if (segmentsByBlockIndex[i] is not null)
                _segmentStartMap[blocks[i].Start] = 1;
        }
    }

    /// <summary>The spec the segments were compiled under; a different spec must not use them.</summary>
    public IReleaseSpec Spec { get; }

    public int SegmentCount { get; }

    public bool TryGetSegmentStartingAt(int programCounter, out IlCompiledSegment segment)
    {
        byte[] map = _segmentStartMap;
        if ((uint)programCounter < (uint)map.Length && map[programCounter] != 0
            && _analyzed.TryGetBlockIndexStartingAt(programCounter, out int blockIndex))
        {
            IlCompiledSegment? candidate = _segmentsByBlockIndex[blockIndex];
            if (candidate is not null)
            {
                segment = candidate;
                return true;
            }
        }

        segment = null!;
        return false;
    }
}

/// <summary>
/// IL-EVM tiering: counts executions per <see cref="CodeInfo"/> and, at the threshold, compiles
/// the code's compilable blocks once. Experimental, off by default; enabled with
/// NETHERMIND_ILEVM=1 (threshold override: NETHERMIND_ILEVM_THRESHOLD).
///
/// Concurrency and lifetime contract:
/// - The execution counter uses <see cref="Interlocked.Increment(ref int)"/>, so exactly one
///   thread observes the threshold value and compiles; everyone else keeps interpreting until
///   the immutable artifact appears via <see cref="Volatile.Write{T}(ref T, T)"/>.
/// - The artifact lives on the <see cref="CodeInfo"/> itself, which is cached per code hash
///   with its own eviction — there is no global registry to leak.
/// - The hot path (<see cref="GetForExecution"/>) is two volatile/field reads and a reference
///   compare: zero allocations, no locks.
/// </summary>
public static class IlEvm
{
    /// <summary>Marks code analyzed with nothing worth compiling, so it is never re-analyzed.</summary>
    private static readonly object s_nothingToCompile = new();

    // Volatile: set at startup from the environment, but tests (and any future admin-RPC
    // toggle) write them at runtime from another thread than the executing EVM threads.
    public static volatile bool Enabled = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM") == "1";

    public static volatile int CompileThreshold = ParseThreshold();

    // Compile on the noticing thread instead of the thread pool. For tests and forced-on
    // consensus gates (NETHERMIND_ILEVM_SYNC=1), where executions must deterministically run
    // on compiled code; never for production traffic.
    public static volatile bool SynchronousCompilation = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SYNC") == "1";

    // Observability (Grafana / test assertions). Interlocked: written once per contract, read
    // as exact values. Deliberately NO per-segment-execution counter — a shared counter written
    // by every RPC thread on every segment was measurable cache-line contention on the hot path.
    public static long ContractsCompiled;
    public static long SegmentsCompiled;
    public static long ContractCompilationFailures;

    /// <summary>
    /// Per-frame notice: increments the execution counter and compiles exactly once at the
    /// threshold. Compilation is synchronous on the noticing thread — a one-time cost per code
    /// hash, never repeated (failures publish the sentinel).
    /// </summary>
    /// <remarks>
    /// Compilation is once per CodeInfo lifetime, deliberately: after a hard fork the artifact
    /// remains the old spec's, <see cref="GetForExecution"/> rejects it on the reference
    /// compare, and the contract simply interprets until the CodeInfo is evicted or the node
    /// restarts. Correctness is unaffected; only the speedup lapses. Do NOT "fix" this by
    /// clearing <see cref="CodeInfo.IlEvmArtifact"/> on mismatch — concurrent clear/publish
    /// would race; a proper fix is a per-spec artifact slot, which is not worth it until forks
    /// during uptime matter for this experimental feature.
    /// </remarks>
    public static void NoticeExecution(CodeInfo codeInfo, IReleaseSpec spec)
    {
        if (Volatile.Read(ref codeInfo.IlEvmArtifact) is not null)
            return;

        if (Interlocked.Increment(ref codeInfo.IlEvmExecutionCount) != CompileThreshold)
            return;

        if (SynchronousCompilation)
        {
            Volatile.Write(ref codeInfo.IlEvmArtifact, Compile(codeInfo, spec));
            return;
        }

        // Compile in the background — never on the noticing thread, which is block processing
        // or an RPC call (synchronous compilation of the mainnet hot set measurably stalled
        // block throughput). Until the artifact is published the interpreter keeps serving.
        // The Interlocked threshold compare above guarantees this enqueues exactly once.
        ThreadPool.UnsafeQueueUserWorkItem(
            static state => Volatile.Write(ref state.codeInfo.IlEvmArtifact, Compile(state.codeInfo, state.spec)),
            (codeInfo, spec),
            preferLocal: false);
    }

    public static IlCompiledCode? GetForExecution(CodeInfo codeInfo, IReleaseSpec spec)
    {
        object? artifact = Volatile.Read(ref codeInfo.IlEvmArtifact);
        return artifact is IlCompiledCode compiled && ReferenceEquals(compiled.Spec, spec)
            ? compiled
            : null;
    }

    private static object Compile(CodeInfo codeInfo, IReleaseSpec spec)
    {
        try
        {
            ReadOnlySpan<byte> code = codeInfo.CodeSpan;
            AnalyzedCode analyzed = BytecodeAnalyzer.Analyze(code, spec);
            if (analyzed.BlockCount == 0)
                return s_nothingToCompile;

            IlCompiledSegment?[] segments = new IlCompiledSegment?[analyzed.BlockCount];
            int segmentCount = 0;
            for (int i = 0; i < analyzed.BlockCount; i++)
            {
                if (analyzed.Blocks[i].IsCompilable
                    && IlSegmentCompiler.TryCompile(code, analyzed.Blocks[i], out IlCompiledSegment? segment))
                {
                    segments[i] = segment;
                    segmentCount++;
                }
            }

            if (segmentCount == 0)
                return s_nothingToCompile;

            Interlocked.Increment(ref ContractsCompiled);
            Interlocked.Add(ref SegmentsCompiled, segmentCount);
            return new IlCompiledCode(analyzed, segments, spec, segmentCount);
        }
        catch (Exception)
        {
            // A compiler defect must never take down execution — the interpreter is always
            // correct; mark the code as not compilable and move on. The failure counter makes
            // a systematically broken compiler visible on dashboards (this static class has no
            // logger; a non-zero ContractCompilationFailures is the observable signal).
            Interlocked.Increment(ref ContractCompilationFailures);
            return s_nothingToCompile;
        }
    }

    private static int ParseThreshold() =>
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_THRESHOLD"), out int threshold) && threshold > 0
            ? threshold
            : 16;
}
