// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Nethermind.IlEvm;

/// <summary>Counters of the compiled path, collected only when <c>NETHERMIND_ILEVM_DIAG=1</c>.</summary>
public static class IlEvmDiagnostics
{
    public static readonly bool Enabled = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_DIAG") == "1";

    public static long Finds;
    public static long DirectHits;
    public static long SharedLookups;
    public static long SharedHits;
    public static long ContentHashes;
    public static long ContentHashBytes;
    public static long ContentHashTicks;
    public static long Requests;
    public static long Runs;
    public static long RunTicks;
    public static long ChunkCalls;
    public static long EntryDeopts;
    public static long Deopts;
    public static long DeoptTicks;
    public static long FailedSegments;
    public static long InterpretedFrames;
    public static long InterpretedTicks;
    /// <summary>Frames run on the interpreter with the compiler off.</summary>
    public static long PlainFrames;
    public static long PlainTicks;

    /// <summary>Per-code totals of frames, opcodes and time, on the interpreter and on compiled code.</summary>
    public sealed class ContractStats
    {
        public int CodeLength;
        public long PlainFrames, PlainOps, PlainTicks;
        public long CompiledFrames, CompiledOps, CompiledTicks;
    }

    public static readonly System.Collections.Concurrent.ConcurrentDictionary<object, ContractStats> Contracts = new(ReferenceEqualityComparer.Instance);

    /// <summary>IL instructions the compiler emitted, by what they implement.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> IlByCategory = new();

    /// <summary>With <c>NETHERMIND_ILEVM_MARKERS=1</c>, compiled code stores the id of what it runs next in <see cref="Marker"/>, for a sampling profiler.</summary>
    /// <remarks>The runtime keeps no IL-to-native map of dynamic methods, so this tells samples apart by opcode; each marker costs a store, and unlike the other diagnostics it leaves the rules as set.</remarks>
    public static readonly bool Markers = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MARKERS") == "1";

    /// <summary>With <see cref="Markers"/>: the id of what compiled code runs, 0 outside it.</summary>
    public static int Marker;

    /// <summary>With <see cref="Markers"/>: each marker's method, what it marks and the code offset of its opcode.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (string Method, string Category, int Pc)> MarkerNames = new();

    private static int _lastMarker;

    internal static int NextMarker(string method, string category, int pc)
    {
        int id = Interlocked.Increment(ref _lastMarker);
        MarkerNames[id] = (method, category, pc);
        return id;
    }

    public static IEnumerable<string> DescribeIl(int top)
    {
        long total = 0;
        foreach (KeyValuePair<string, long> entry in IlByCategory) total += entry.Value;
        foreach (KeyValuePair<string, long> entry in IlByCategory.OrderByDescending(static e => e.Value).Take(top))
            yield return $"{entry.Value,9} {100.0 * entry.Value / Math.Max(1, total),5:F1}% {entry.Key}";
        yield return $"{total,9} total";
    }

    internal static ContractStats For(object codeInfo, int length) =>
        Contracts.GetOrAdd(codeInfo, static (_, l) => new ContractStats { CodeLength = l }, length);

    /// <summary>One line per code, sorted by interpreter time: size, frames, opcodes per frame and microseconds per frame on each path.</summary>
    public static IEnumerable<string> DescribeContracts(int top)
    {
        foreach (ContractStats c in Contracts.Select(static kv => kv.Value).OrderByDescending(static c => c.PlainTicks + c.CompiledTicks).Take(top))
        {
            double plainUs = c.PlainFrames > 0 ? Us(c.PlainTicks) / c.PlainFrames : 0;
            double compiledUs = c.CompiledFrames > 0 ? Us(c.CompiledTicks) / c.CompiledFrames : 0;
            double opsPerFrame = c.PlainFrames > 0 ? (double)c.PlainOps / c.PlainFrames : 0;
            yield return $"{c.CodeLength,6} B | frames {c.PlainFrames,7}/{c.CompiledFrames,7} | ops/frame {opsPerFrame,8:F0} | us/frame {plainUs,8:F2} / {compiledUs,8:F2} | speedup {(compiledUs > 0 ? plainUs / compiledUs : 0),5:F2}x | total ms {Us(c.PlainTicks) / 1000,8:F1} / {Us(c.CompiledTicks) / 1000,8:F1}";
        }
    }

    public static void Reset()
    {
        Finds = DirectHits = SharedLookups = SharedHits = ContentHashes = ContentHashBytes = ContentHashTicks = 0;
        Requests = Runs = RunTicks = ChunkCalls = EntryDeopts = Deopts = DeoptTicks = FailedSegments = 0;
        InterpretedFrames = InterpretedTicks = PlainFrames = PlainTicks = 0;
    }

    private static double Us(long ticks) => ticks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;

    public static string Describe() =>
        $"finds {Finds}, direct hits {DirectHits}, shared lookups {SharedLookups} (hits {SharedHits}), " +
        $"content hashes {ContentHashes} ({ContentHashBytes / Math.Max(1, ContentHashes)} B avg, {Us(ContentHashTicks) / Math.Max(1, ContentHashes):F2} us avg, {Us(ContentHashTicks) / 1000:F1} ms total), " +
        $"requests {Requests}, runs {Runs} ({Us(RunTicks) / 1000:F1} ms), chunk calls {ChunkCalls}, entry deopts {EntryDeopts}, " +
        $"deopts {Deopts} ({Us(DeoptTicks) / 1000:F1} ms after), failed segments {FailedSegments}, " +
        $"interpreted frames {InterpretedFrames} ({Us(InterpretedTicks) / 1000:F1} ms), plain frames {PlainFrames} ({Us(PlainTicks) / 1000:F1} ms)";
}
