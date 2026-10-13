// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.IlEvm;

/// <summary>Switches for the EVM bytecode-to-IL compiler, set from <see cref="IIlEvmConfig"/> when the plugin loads.</summary>
/// <remarks>
/// A contract is compiled once it has run <see cref="Threshold"/> frames, on a background thread unless
/// <see cref="CompileSynchronously"/> is set; until then, and for any frame the compiled code hands back, the
/// interpreter runs it. The environment variables give defaults for tests and the replay harness.
/// </remarks>
public static class IlEvmSettings
{
    /// <summary>The kill switch: while off, the runner declines every frame.</summary>
    public static bool Enabled { get; set; } =
        RuntimeFeature.IsDynamicCodeSupported && Environment.GetEnvironmentVariable("NETHERMIND_ILEVM") != "0";

    /// <summary>The opcodes a contract runs on the interpreter, over all its frames, after which it is compiled.</summary>
    /// <remarks>Catches a contract whose few frames run long loops, which <see cref="Threshold"/> alone would miss.</remarks>
    public static int OpcodeThreshold { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_OPCODES"), out int opcodes) && opcodes > 0 ? opcodes : 20_000;

    /// <summary>The most compiled contracts kept for reuse by code content, across the code infos that carry them.</summary>
    public static int MaxCachedContracts { get; set; } = 8_192;

    /// <summary>The most IL the contracts kept for reuse by content may hold together; the oldest go first past it.</summary>
    /// <remarks>
    /// A contract the cache lets go stays compiled for the code infos that hold it. Code sent with a call's state
    /// overrides can be made hot at will, so without a bound in bytes the cache would keep whatever it was sent.
    /// </remarks>
    public static long MaxCachedIlBytes { get; set; } =
        long.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MAX_CACHED_IL"), out long maxCachedIl) && maxCachedIl > 0 ? maxCachedIl : 128L << 20;

    /// <summary>The most IL one contract may compile to; one past it stays on the interpreter.</summary>
    /// <remarks>The JIT takes about a millisecond per KiB of IL, so this bounds a contract's compile time to a second or two.</remarks>
    public static int MaxContractIlBytes { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MAX_CONTRACT_IL"), out int maxContractIl) && maxContractIl > 0 ? maxContractIl : 2 << 20;

    /// <summary>The most IL the compiled contracts alive at once may hold; past it nothing more is compiled until some is collected.</summary>
    /// <remarks>Native code takes roughly as much again as the IL.</remarks>
    public static long MaxLiveIlBytes { get; set; } =
        long.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MAX_LIVE_IL"), out long maxLiveIl) && maxLiveIl > 0 ? maxLiveIl : 256L << 20;

    /// <summary>The most compiles waiting for the background compiler; code that turns hot past it tries again later.</summary>
    public static int MaxPendingCompiles { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MAX_PENDING"), out int maxPending) && maxPending > 0 ? maxPending : 64;

    private static long _liveIl;

    /// <summary>The IL of the compiled contracts not yet collected.</summary>
    public static long LiveIlBytes => Interlocked.Read(ref _liveIl);

    internal static void AddLiveIl(long bytes) => Interlocked.Add(ref _liveIl, bytes);

    /// <summary>The number of frames a contract runs on the interpreter before it is compiled.</summary>
    public static int Threshold { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_THRESHOLD"), out int threshold) ? threshold : 32;

    /// <summary>EVM instructions per compiled chunk to start from; a chunk that compiles too large is halved.</summary>
    /// <remarks>Large contracts are compiled as several methods, each small enough for the JIT to optimize.</remarks>
    public static int ChunkInstructions { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_CHUNK"), out int chunk) && chunk > 0 ? chunk : 1_400;

    /// <summary>Estimated JIT locals that inlining may add to one chunk, kept under RyuJIT's limit of 1024 locals.</summary>
    /// <remarks>
    /// Past that limit the JIT stops inlining for the rest of the method, so the helpers of every later instruction
    /// become calls; chunks are cut to stay under it.
    /// </remarks>
    public static int ChunkInlineBudget { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_INLINE_BUDGET"), out int budget) && budget > 0 ? budget : 600;

    /// <summary>Cut a contract into chunks along its control flow, a function with the helpers it calls, rather than by code order.</summary>
    public static bool Regions { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_REGIONS") != "0";

    /// <summary>How many copies of units already compiled in another chunk the chunks may hold, relative to the units.</summary>
    /// <remarks>A helper called from several functions is compiled into each caller's chunk, so the call stays inside one method.</remarks>
    public static double ChunkDuplication { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_DUPLICATION"), System.Globalization.CultureInfo.InvariantCulture, out double duplication) && duplication >= 0 ? duplication : 1.0;

    /// <summary>Inline the memory and calldata fast paths into compiled code rather than call them.</summary>
    /// <remarks>
    /// Calling them is the default: inlined, each site brings tens of instructions and its temporaries into the chunk,
    /// so chunks are cut smaller and run colder, which costs more than the calls save.
    /// </remarks>
    public static bool InlineMemory { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_INLINE_MEMORY") == "1";

    /// <summary>Inline the 256-bit arithmetic, comparison and shift helpers into compiled code rather than call them.</summary>
    public static bool InlineArithmetic { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_INLINE_ARITH") == "1";

    /// <summary>Compile the common forms with a constant operand - comparisons, masks, shifts, small additions - straight to limb operations.</summary>
    public static bool DirectArithmetic { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_DIRECT") != "0";

    /// <summary>Let the JIT inline the bodies compiled code runs directly (CALLDATALOAD and the like) rather than call them out of line.</summary>
    public static bool InlineBodies { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_INLINE_BODIES") != "0";

    /// <summary>The most body call sites of one contract the JIT may inline; the rest call the body.</summary>
    /// <remarks>
    /// Each inlined body costs the JIT about as much as a few hundred IL instructions of the chunk's own, so a large
    /// contract made mostly of body opcodes would take seconds to compile; past the budget its sites stay calls, which
    /// also keeps its code small enough to stay in the CPU caches.
    /// </remarks>
    public static int InlineBodyBudget { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_INLINE_BODY_BUDGET"), out int inlineBodyBudget) && inlineBodyBudget >= 0 ? inlineBodyBudget : 256;

    /// <summary>Entries a chunk of a laid-out contract waits for before it is compiled; the interpreter runs it until then.</summary>
    /// <remarks>Only the parts of a contract that run get compiled, so the JIT's time and the code's memory follow use.</remarks>
    public static int ChunkThreshold { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_CHUNK_THRESHOLD"), out int chunkThreshold) && chunkThreshold > 0 ? chunkThreshold : 2;

    /// <summary>Compile every chunk once a contract is laid out, rather than each when frames reach it.</summary>
    public static bool EagerChunks { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_EAGER_CHUNKS") == "1";

    /// <summary>Interpreted opcodes per second a contract must also run before it is compiled; 0 for no rate condition.</summary>
    /// <remarks>
    /// Compiled code pays off only while it stays in the CPU caches: a contract that runs rarely meets its code cold
    /// on every frame, where the interpreter's few hot handlers win. Applies with a <see cref="Threshold"/> above 1.
    /// </remarks>
    public static int HotOpcodesPerSecond { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_HOT_RATE"), out int hotRate) && hotRate >= 0 ? hotRate : 50_000;

    /// <summary>Compile on the thread that crosses the threshold instead of in the background.</summary>
    public static bool CompileSynchronously { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SYNC") == "1";

    /// <summary>Time each compiled contract against the interpreter on its own frames, and give back to the interpreter one compiled code does not speed up.</summary>
    /// <remarks>See <see cref="IlSelfCheck"/>.</remarks>
    public static bool SelfCheck { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK") != "0";

    /// <summary>How long after a contract is compiled its self-check starts; until then compiled code runs every frame, untimed.</summary>
    /// <remarks>
    /// The helpers compiled code calls are tiered like any method: their first calls run tier-0 code, and the
    /// optimized code takes over once calls have been counted and the background JIT has caught up. A check started
    /// before that would time compiled code by code it no longer runs.
    /// </remarks>
    public static int SelfCheckDelayMs { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_DELAY_MS"), out int checkDelayMs) && checkDelayMs >= 0 ? checkDelayMs : 5_000;

    /// <summary>How long a contract the self-check first finds slower runs on the interpreter before it is checked once more.</summary>
    /// <remarks>
    /// Only a second verdict against compiled code releases it. The first may come early in the process's life, while
    /// the helpers compiled code calls still run tier-0 code, or during a burst of load that met one path more than the other.
    /// </remarks>
    public static int SelfCheckRecheckMs { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_RECHECK_MS"), out int recheckMs) && recheckMs >= 0 ? recheckMs : 30_000;

    /// <summary>Compiled code must run a contract's opcodes in at most this share of the interpreter's time to keep it; 1 keeps whatever measures faster.</summary>
    public static double SelfCheckKeepRatio { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_RATIO"), System.Globalization.CultureInfo.InvariantCulture, out double keepRatio) && keepRatio > 0 ? keepRatio : 1.0;

    private static long _compiled;
    private static long _failed;

    public static long CompiledContracts => Interlocked.Read(ref _compiled);
    public static long FailedContracts => Interlocked.Read(ref _failed);

    /// <summary>Contracts the compiler declined, by reason with its numbers left out.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> FailuresByReason = new();

    internal static void CountCompiled() => Interlocked.Increment(ref _compiled);

    private static long _deferred;

    /// <summary>Compiles put off because compiled code already took all the IL it may.</summary>
    public static long DeferredCompiles => Interlocked.Read(ref _deferred);

    internal static void CountDeferred() => Interlocked.Increment(ref _deferred);

    internal static void CountFailed(string? reason)
    {
        Interlocked.Increment(ref _failed);
        string kind = reason is null ? "unknown" : System.Text.RegularExpressions.Regex.Replace(reason.Split('\n')[0], "[0-9]+", "#");
        FailuresByReason.AddOrUpdate(kind.Length > 160 ? kind[..160] : kind, 1, static (_, v) => v + 1);
    }
}

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

/// <summary>The EVM bytecode-to-IL compiler: compiled contracts, their runtime helpers and the code generator.</summary>
public static unsafe partial class IlEvm
{
    /// <summary>Returned by compiled code that hands the frame back to the interpreter at <c>programCounter</c>.</summary>
    /// <remarks>
    /// Gas, stack and memory are then exactly the interpreter's before that instruction, so it can run on unchanged;
    /// the value is <see cref="FrameCodeRunner{EthereumGasPolicy}.Declined"/>.
    /// </remarks>
    private const EvmExceptionType IlDeopt = FrameCodeRunner<EthereumGasPolicy>.Declined;

    private const int IlPollInterval = 1024;

    /// <summary>Returned by a chunk of a compiled contract to continue at <c>programCounter</c> in another chunk.</summary>
    private const EvmExceptionType IlContinue = (EvmExceptionType)(int.MinValue + 1);

    /// <summary>The dispatch loop's per-chain counters, carried from chunk to chunk of a compiled contract.</summary>
    internal struct IlRunState
    {
        /// <summary>Opcodes run since the frame was entered or resumed.</summary>
#pragma warning disable CS0649 // Written by the compiled chunks.
        public int OpCodes;
#pragma warning restore CS0649
        /// <summary>The opcode count from which a taken jump polls for cancellation; <see cref="int.MaxValue"/> when the run is not cancelable.</summary>
        public int PollAt;
    }

    internal delegate EvmExceptionType IlRunner(
        VirtualMachine<EthereumGasPolicy> vm,
        scoped ref EvmStack stack,
        scoped ref EthereumGasPolicy gas,
        scoped ref nint programCounter,
        scoped ref IlRunState state);

    /// <summary>A contract compiled for one fork's opcode table.</summary>
    internal sealed class IlContract(
        IlFork fork, ReadOnlyMemory<byte> code, int[] entryChunk, int[] entryIndex, int[] jumpOrdinal, short[][] jumpCases, int[] jumpBases,
        ulong[] costs, int[] inputs, int[] growths, bool[] dynamic, bool[] opaque, int chunks, string name)
    {
        private const int ChunkCold = 0;
        private const int ChunkQueued = 1;
        private const int ChunkCompiled = 2;
        private const int ChunkFailed = 3;

        public readonly IlFork Fork = fork;
        /// <summary>The code compiled, to verify a content-hash hit byte for byte.</summary>
        public readonly ReadOnlyMemory<byte> Code = code;
        /// <summary>Per code offset, the chunk of the compiled unit that starts there, or -1.</summary>
        public readonly int[] EntryChunk = entryChunk;
        /// <summary>Per code offset, the index of the compiled unit that starts there within its chunk.</summary>
        public readonly int[] EntryIndex = entryIndex;
        /// <summary>Per code offset, the ordinal of the JUMPDEST there among the code's, or -1 where a jump may not land.</summary>
        public readonly int[] JumpOrdinal = jumpOrdinal;
        /// <summary>Per chunk and JUMPDEST ordinal less the chunk's base, the jump case of that JUMPDEST in the chunk, or -1 where the chunk does not compile it.</summary>
        public readonly short[][] JumpCases = jumpCases;
        /// <summary>Per chunk, the ordinal its jump-case table starts at.</summary>
        public readonly int[] JumpBases = jumpBases;
        /// <summary>The bytes of the contract's tables, which the memory budgets count with its IL.</summary>
        public long TableBytes;
        /// <summary>Each chunk's runner once the JIT has compiled it; null while frames that reach the chunk are left to the interpreter.</summary>
        public readonly IlRunner?[] Runners = new IlRunner?[chunks];
        /// <summary>Each chunk's emitted method, bound to its <see cref="IlChunk"/> but not JIT-compiled until frames reach it.</summary>
        public readonly IlRunner[] Prepared = new IlRunner[chunks];
        private readonly int[] _entries = new int[chunks];
        private readonly int[] _states = new int[chunks];
        /// <summary>The prefix of the compiled methods' names.</summary>
        public readonly string Name = name;
        public int ChunkCount => Prepared.Length;
        /// <summary>Why the last chunk that failed to compile did, for diagnostics.</summary>
        public string? ChunkFailure;
        public int CompiledChunks;
        /// <summary>Per segment instruction, its static gas, stack inputs and stack growth.</summary>
        private readonly ulong[] _costs = costs;
        private readonly int[] _inputs = inputs;
        private readonly int[] _growths = growths;
        private readonly bool[] _dynamic = dynamic;
        private readonly bool[] _opaque = opaque;
        /// <summary>The size of the compiled methods, and how long the JIT took over them, for diagnostics.</summary>
        public int IlBytes;
        public TimeSpan JitTime;
        /// <summary>Whether compiled code has proved faster than the interpreter on this contract's frames.</summary>
        public readonly IlSelfCheck Check = new();
        /// <summary>Chunks queued or compiled so far: a frame during which this moved waited on the JIT, and its time says nothing of the code.</summary>
        public int ChunkEvents;
        /// <summary>When the contract was laid out, in <see cref="Environment.TickCount64"/> milliseconds; its self-check waits <see cref="IlEvmSettings.SelfCheckDelayMs"/> from then.</summary>
        public readonly long CompiledAt = Environment.TickCount64;
        /// <summary>Whether <see cref="IlBytes"/> counts toward <see cref="IlEvmSettings.LiveIlBytes"/>; cleared once by release or collection.</summary>
        private int _live;

        /// <summary>Counts the contract's IL as alive, once it is laid out.</summary>
        /// <summary>What the memory budgets count for the contract: its IL and its tables.</summary>
        public long MemoryBytes => IlBytes + TableBytes;

        public void MarkLive()
        {
            if (Interlocked.Exchange(ref _live, 1) == 0)
                IlEvmSettings.AddLiveIl(MemoryBytes);
        }

        /// <summary>Drops the compiled code, for a contract the interpreter runs from now on; its frames are left to the interpreter.</summary>
        public void Release()
        {
            lock (Prepared)
            {
                for (int chunk = 0; chunk < Prepared.Length; chunk++)
                {
                    Volatile.Write(ref _states[chunk], ChunkFailed);
                    Volatile.Write(ref Runners[chunk], null);
                    Prepared[chunk] = null!;
                }
            }

            if (Interlocked.Exchange(ref _live, 0) == 1)
                IlEvmSettings.AddLiveIl(-MemoryBytes);
        }

        ~IlContract()
        {
            if (Interlocked.Exchange(ref _live, 0) == 1)
                IlEvmSettings.AddLiveIl(-MemoryBytes);
        }

        /// <summary>Set on this thread when a run handed back because it reached a chunk not compiled yet, which the self-check does not time.</summary>
        [ThreadStatic] public static bool HitColdChunk;

        /// <summary>Runs the frame from <paramref name="programCounter"/> with the contract of <c>RunDispatchLoop</c>.</summary>
        /// <returns>The halt reason, or <see cref="IlDeopt"/> to have the interpreter go on from <paramref name="programCounter"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, scoped ref nint programCounter, bool cancelable)
        {
            nint pc = programCounter;
            if ((nuint)pc >= (nuint)EntryChunk.Length || EntryChunk[pc] < 0)
            {
                if (IlEvmDiagnostics.Enabled)
                    Interlocked.Increment(ref IlEvmDiagnostics.EntryDeopts);
                return IlDeopt;
            }

            // The machine polled before offering the frame, as the dispatch loop's entry poll.
            IlRunState state = new() { PollAt = cancelable ? IlPollInterval : int.MaxValue };

            EvmExceptionType result;
            do
            {
                pc = programCounter;
                int chunk = (nuint)pc < (nuint)EntryChunk.Length ? EntryChunk[pc] : -1;
                if (chunk < 0)
                    return IlDeopt;
                IlRunner? runner = Runners[chunk] ?? Cold(chunk);
                // A chunk not compiled yet: the interpreter goes on from here, with the gas, stack and counter the last
                // chunk left.
                if (runner is null)
                    return IlDeopt;
                if (IlEvmDiagnostics.Enabled)
                    Interlocked.Increment(ref IlEvmDiagnostics.ChunkCalls);
                result = runner(vm, ref stack, ref gas, ref programCounter, ref state);
            } while (result == IlContinue);

            return result;
        }

        /// <summary>A chunk frames reach before it is compiled: compiled now when compiling synchronously, else queued once entered enough.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private IlRunner? Cold(int chunk)
        {
            // A chunk that failed to compile is a cost of compiled code the self-check times; one waiting for the JIT is not.
            if (Volatile.Read(ref _states[chunk]) == ChunkFailed)
                return null;
            if (IlEvmSettings.CompileSynchronously)
                return CompileChunk(chunk);
            HitColdChunk = true;
            if (Interlocked.Increment(ref _entries[chunk]) >= IlEvmSettings.ChunkThreshold &&
                Interlocked.CompareExchange(ref _states[chunk], ChunkQueued, ChunkCold) == ChunkCold)
            {
                Interlocked.Increment(ref ChunkEvents);
                if (!IlCompileQueue.Enqueue(() => CompileChunk(chunk)))
                {
                    _entries[chunk] = 0;
                    Volatile.Write(ref _states[chunk], ChunkCold);
                }
            }

            return null;
        }

        /// <summary>JIT-compiles <paramref name="chunk"/> and publishes its runner; one contract compiles one chunk at a time.</summary>
        public IlRunner? CompileChunk(int chunk)
        {
            lock (Prepared)
            {
                if (Runners[chunk] is { } compiled)
                    return compiled;
                if (_states[chunk] == ChunkFailed || Prepared[chunk] is null)
                    return null;

                IlRunner runner = Prepared[chunk];
                try
                {
                    // An offset past the code returns at once: the call only has the JIT compile the method.
                    long started = System.Diagnostics.Stopwatch.GetTimestamp();
                    EvmStack probeStack = default;
                    EthereumGasPolicy probeGas = default;
                    nint probePc = -1;
                    IlRunState probeState = default;
                    runner(null!, ref probeStack, ref probeGas, ref probePc, ref probeState);
                    JitTime += System.Diagnostics.Stopwatch.GetElapsedTime(started);
                }
                catch (Exception exception)
                {
                    ChunkFailure = exception.ToString();
                    Volatile.Write(ref _states[chunk], ChunkFailed);
                    return null;
                }

                CompiledChunks++;
                Interlocked.Increment(ref ChunkEvents);
                Volatile.Write(ref Runners[chunk], runner);
                Volatile.Write(ref _states[chunk], ChunkCompiled);
                return runner;
            }
        }

        /// <summary>Compiles every chunk now, for <see cref="IlEvmSettings.EagerChunks"/>.</summary>
        public void CompileAllChunks()
        {
            for (int chunk = 0; chunk < Prepared.Length; chunk++)
                CompileChunk(chunk);
        }

        /// <summary>Finds how a segment whose precondition failed fails, checking each opcode as the dispatch loop does.</summary>
        /// <remarks>
        /// Every opcode of a segment charges fixed gas and has fixed stack bounds, checked gas first and then the stack,
        /// so walking them from the segment's gas and head reproduces the dispatch loop's fault, its gas left and its
        /// opcode count, which counts the faulting opcode. A memory opcode charges expansion the walk cannot know, so
        /// a walk that passes one's fixed checks returns <see cref="IlDeopt"/> and the interpreter runs from the start.
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public EvmExceptionType FailSegment(int first, int count, ulong gas, nint head, int opCodes, out ulong gasLeft, out int opCodesRun)
        {
            gasLeft = gas;
            opCodesRun = opCodes;
            if (IlEvmDiagnostics.Enabled)
                Interlocked.Increment(ref IlEvmDiagnostics.FailedSegments);
            EvmExceptionType fault = FailSegment(first, count, ref gasLeft, head, ref opCodesRun);
            return fault;
        }

        private EvmExceptionType FailSegment(int first, int count, ref ulong gas, nint head, ref int opCodes)
        {
            for (int i = first; i < first + count; i++)
            {
                if (_opaque[i]) return IlDeopt;
                opCodes++;
                ulong cost = _costs[i];
                if (gas < cost) return EvmExceptionType.OutOfGas;
                gas -= cost;
                if (head < _inputs[i]) return EvmExceptionType.StackUnderflow;
                int growth = _growths[i];
                if (growth > 0 && head + growth >= EvmStack.MaxStackSize) return EvmExceptionType.StackOverflow;
                if (_dynamic[i]) return IlDeopt;
                head += growth;
            }

            throw new InvalidOperationException("A segment failed its precondition with every opcode fitting.");
        }
    }

    /// <summary>One compiled chunk: what its dynamic method is bound to, with the tables its code reads.</summary>
    internal sealed class IlChunk(IlContract contract, short[] jumpCases, int jumpBase, IlSegmentInfo[] segments, IlSite[] sites)
    {
        public readonly IlContract Contract = contract;
        /// <summary>The contract's entry table, read by the chunk's entry switch.</summary>
        public readonly int[] EntryIndex = contract.EntryIndex;
        /// <summary>The contract's JUMPDEST ordinals, and this chunk's jump case for each, read by its dynamic jumps.</summary>
        public readonly int[] JumpOrdinal = contract.JumpOrdinal;
        public readonly short[] JumpCases = jumpCases;
        /// <summary>The ordinal <see cref="JumpCases"/> starts at.</summary>
        public readonly int JumpBase = jumpBase;
        /// <summary>Per segment with a precondition, where its instructions sit in the contract's tables and where it starts.</summary>
        public readonly IlSegmentInfo[] Segments = segments;
        /// <summary>Per call of an embedded body, what <see cref="IlEmbedded{TOpcode}"/> needs to finish or leave its segment.</summary>
        public readonly IlSite[] Sites = sites;

        /// <summary>
        /// The chunk's shared fault path: finds how <paramref name="segment"/>, whose precondition failed, fails, and
        /// points <paramref name="programCounter"/> at its start for a hand-over.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public EvmExceptionType FailAt(int segment, ulong gas, nint head, int opCodes, out ulong gasLeft, out int opCodesRun, ref nint programCounter)
        {
            IlSegmentInfo info = Segments[segment];
            programCounter = info.Pc;
            return Contract.FailSegment(info.First, info.Count, gas, head, opCodes, out gasLeft, out opCodesRun);
        }
    }

    /// <summary>A compiled segment: its first instruction in the contract's tables, its instruction count and its code offset.</summary>
    internal readonly record struct IlSegmentInfo(int First, int Count, int Pc);

    /// <summary>An embedded body inside a segment.</summary>
    /// <param name="Pc">The body's opcode offset.</param>
    /// <param name="NextPc">The offset past it, where the segment goes on.</param>
    /// <param name="GasAfter">The static gas of the instructions after it, charged again once it has run.</param>
    /// <param name="FirstAfter">The table index of the instruction after it.</param>
    /// <param name="CountedAfter">The instructions after it, counted with the segment.</param>
    /// <param name="Execute">The body's <c>Execute</c> entry point.</param>
    internal readonly record struct IlSite(int Pc, int NextPc, ulong GasAfter, int FirstAfter, int CountedAfter, nint Execute, int HeadDelta, ulong GasBack);

    /// <summary>The compiler's state for one <see cref="CodeInfo"/>, held in the frame code runner's per-code slot.</summary>
    internal sealed class IlCodeState
    {
        public int Executions;
        public int OpCodesRun;
        /// <summary>Opcodes the interpreter ran for this code in the current one-second window, and when the window began.</summary>
        public int WindowOpCodes;
        public long WindowStart;
        public int Pending;
        /// <summary>Until when a hot frame does not ask for a compile again, after one was turned away by a budget.</summary>
        public long RetryAfter;
        public volatile IlContract? Contract;
        public volatile IlFork? FailedFor;
        public string? FailureReason;
        /// <summary>A fast 64-bit hash of the code, taken once the code runs a second frame, keying the shared cache.</summary>
        public long ContentHash;
        public bool HasContentHash;
    }

    /// <summary>A fork's opcode semantics as the compiler sees them, one per interned <see cref="OpcodeBodySet"/>.</summary>
    /// <remarks>
    /// The core interns body sets, so forks and specs whose every opcode runs the same body share one instance, and so
    /// share compiled contracts: specs rebuilt per call, and forks that change nothing the compiler sees.
    /// </remarks>
    internal sealed class IlFork(IlOp[] ops, OpcodeBodySet bodies)
    {
        public readonly IlOp[] Ops = ops;
        public readonly OpcodeBodySet Bodies = bodies;
        public bool UsesCallDestinations => Bodies.UsesCallDestinations;
    }

    private static readonly ConditionalWeakTable<OpcodeBodySet, IlFork> _ilForks = [];

    [ThreadStatic] private static OpcodeBodySet? _lastBodies;
    [ThreadStatic] private static IlFork? _lastFork;

    private static IlFork IlGetFork(OpcodeBodySet bodies)
    {
        if (ReferenceEquals(bodies, _lastBodies))
            return _lastFork!;
        IlFork fork = _ilForks.GetValue(bodies, static b => new IlFork(IlDescribeOps(b), b));
        _lastBodies = bodies;
        _lastFork = fork;
        return fork;
    }

    internal readonly record struct IlContractKey(long Code, int Length, IlFork Fork);

    /// <summary>Compiled contracts by code content and fork, so a new <see cref="CodeInfo"/> of known code needs no compile.</summary>
    private static readonly ConcurrentDictionary<IlContractKey, IlContract> _ilContracts = new();
    /// <summary>The cache's keys in the order they were added, and the IL their contracts hold together.</summary>
    private static readonly ConcurrentQueue<IlContractKey> _ilCacheOrder = new();
    private static long _ilCachedBytes;

    /// <summary>Keeps <paramref name="contract"/> for reuse by content, letting the oldest go past the cache's bounds.</summary>
    private static void IlCache(IlContractKey key, IlContract contract)
    {
        if (!_ilContracts.TryAdd(key, contract))
            return;
        _ilCacheOrder.Enqueue(key);
        long cached = Interlocked.Add(ref _ilCachedBytes, contract.MemoryBytes);
        while ((cached > IlEvmSettings.MaxCachedIlBytes || _ilContracts.Count > IlEvmSettings.MaxCachedContracts) &&
               _ilCacheOrder.TryDequeue(out IlContractKey oldest))
        {
            if (_ilContracts.TryRemove(oldest, out IlContract? evicted))
                cached = Interlocked.Add(ref _ilCachedBytes, -evicted.MemoryBytes);
        }
    }

    /// <remarks>Two AES-based hashes, of the whole code and of its second half; a collision only costs a cache miss.</remarks>
    private static long IlContentHash(CodeInfo codeInfo)
    {
        ReadOnlySpan<byte> code = codeInfo.CodeSpan;
        return ((long)code.FastHash() << 32) | (uint)code[(code.Length / 2)..].FastHash();
    }

    /// <summary>The compiled contract to run a frame of <paramref name="codeInfo"/> on, or null for the interpreter.</summary>
    /// <remarks>Counts the frame towards the code's hotness and requests its compile once it is hot.</remarks>
    internal static IlContract? Find(CodeInfo codeInfo, OpcodeBodySet bodies, out IlCodeState state)
    {
        ref object? slot = ref IlEvmFrameRunner.CodeSlot(codeInfo);
        if (slot is not IlCodeState existing)
        {
            existing = new IlCodeState();
            existing = Interlocked.CompareExchange(ref slot, existing, null) as IlCodeState ?? existing;
        }

        state = existing;
        IlFork fork = IlGetFork(bodies);
        IlContract? contract = state.Contract;
        if (IlEvmDiagnostics.Enabled)
        {
            Interlocked.Increment(ref IlEvmDiagnostics.Finds);
            if (contract is not null && ReferenceEquals(contract.Fork, fork))
                Interlocked.Increment(ref IlEvmDiagnostics.DirectHits);
        }

        if (contract is not null && ReferenceEquals(contract.Fork, fork))
            return contract;

        if (ReferenceEquals(state.FailedFor, fork))
            return null;

        int executions = ++state.Executions;
        bool hot = (executions >= IlEvmSettings.Threshold || state.OpCodesRun >= IlEvmSettings.OpcodeThreshold) &&
            (IlEvmSettings.Threshold <= 1 || state.WindowOpCodes >= IlEvmSettings.HotOpcodesPerSecond);
        // A second frame, or the first hot one, looks the code up by content: it may be compiled under another code info.
        if ((executions == 2 || hot) && IlShared(codeInfo, state, fork) is { } shared)
            return shared;

        return hot && (state.RetryAfter == 0 || Environment.TickCount64 >= state.RetryAfter) ? IlRequest(codeInfo, state, fork) : null;
    }

    /// <summary>How long code that a budget turned away waits before asking again.</summary>
    private const int RetryDelayMs = 5_000;

    /// <summary>The interpreter ran <paramref name="ran"/> opcodes of a frame of <paramref name="codeInfo"/>; what makes it hot.</summary>
    internal static void OnInterpreted(CodeInfo codeInfo, int ran)
    {
        if (IlEvmFrameRunner.CodeSlot(codeInfo) is not IlCodeState state)
            return;
        state.OpCodesRun += ran;
        long now = Environment.TickCount64;
        if (now - state.WindowStart >= 1000)
        {
            state.WindowStart = now;
            state.WindowOpCodes = 0;
        }

        state.WindowOpCodes += ran;
    }

    private static IlContract? IlShared(CodeInfo codeInfo, IlCodeState state, IlFork fork)
    {
        if (!state.HasContentHash)
        {
            long start = IlEvmDiagnostics.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            state.ContentHash = IlContentHash(codeInfo);
            state.HasContentHash = true;
            if (IlEvmDiagnostics.Enabled)
            {
                Interlocked.Increment(ref IlEvmDiagnostics.ContentHashes);
                Interlocked.Add(ref IlEvmDiagnostics.ContentHashBytes, codeInfo.CodeSpan.Length);
                Interlocked.Add(ref IlEvmDiagnostics.ContentHashTicks, System.Diagnostics.Stopwatch.GetTimestamp() - start);
            }
        }

        if (IlEvmDiagnostics.Enabled)
            Interlocked.Increment(ref IlEvmDiagnostics.SharedLookups);
        if (_ilContracts.TryGetValue(new IlContractKey(state.ContentHash, codeInfo.CodeSpan.Length, fork), out IlContract? contract) &&
            codeInfo.CodeSpan.SequenceEqual(contract.Code.Span))
        {
            if (IlEvmDiagnostics.Enabled)
                Interlocked.Increment(ref IlEvmDiagnostics.SharedHits);
            state.Contract = contract;
            return contract;
        }

        return null;
    }

    private static IlContract? IlRequest(CodeInfo codeInfo, IlCodeState state, IlFork fork)
    {
        if (IlEvmDiagnostics.Enabled)
            Interlocked.Increment(ref IlEvmDiagnostics.Requests);
        if (IlEvmSettings.CompileSynchronously)
            return IlCompileInto(codeInfo, state, fork);

        if (Interlocked.Exchange(ref state.Pending, 1) == 1)
            return null;

        if (!IlCompileQueue.Enqueue(() =>
            {
                IlCompileInto(codeInfo, state, fork);
                state.Pending = 0;
            }))
        {
            state.RetryAfter = Environment.TickCount64 + RetryDelayMs;
            state.Pending = 0;
        }

        return null;
    }

    private static IlContract? IlCompileInto(CodeInfo codeInfo, IlCodeState state, IlFork fork)
    {
        try
        {
            if (IlShared(codeInfo, state, fork) is { } shared)
                return shared;

            if (IlEvmSettings.LiveIlBytes >= IlEvmSettings.MaxLiveIlBytes)
            {
                // Compiled code already takes all it may; the code asks again once some may have been collected.
                state.RetryAfter = Environment.TickCount64 + RetryDelayMs;
                IlEvmSettings.CountDeferred();
                return null;
            }

            IlContract? contract = IlCompiler.Compile(codeInfo, fork, out string? reason);
            if (contract is null)
            {
                state.FailureReason = reason;
                state.FailedFor = fork;
                IlEvmSettings.CountFailed(reason);
                return null;
            }

            contract.MarkLive();
            // A contract the self-check gives back keeps its verdict, not its code.
            contract.Check.OnGivenBack = contract.Release;
            contract.Check.Name = contract.Name;
            state.Contract = contract;
            IlCache(new IlContractKey(state.ContentHash, codeInfo.CodeSpan.Length, fork), contract);
            IlEvmSettings.CountCompiled();
            return contract;
        }
        catch (Exception exception)
        {
            state.FailureReason = exception.ToString();
            state.FailedFor = fork;
            IlEvmSettings.CountFailed(state.FailureReason);
            return null;
        }
    }

    /// <summary>Why the compiler last declined <paramref name="codeInfo"/>, for tests and diagnostics.</summary>
    public static string? FailureReason(CodeInfo codeInfo) =>
        IlEvmFrameRunner.CodeSlot(codeInfo) is IlCodeState state ? state.FailureReason ?? state.Contract?.ChunkFailure : null;

    /// <summary>The compiled methods' IL size, JIT time and chunk count for <paramref name="codeInfo"/>, for benchmarks and diagnostics.</summary>
    public static (int IlBytes, TimeSpan JitTime, int Chunks)? CompileStats(CodeInfo codeInfo) =>
        (IlEvmFrameRunner.CodeSlot(codeInfo) as IlCodeState)?.Contract is { } contract ? (contract.IlBytes, contract.JitTime, contract.CompiledChunks) : null;

    /// <summary>Whether <paramref name="codeInfo"/> has a compiled contract, for tests and diagnostics.</summary>
    public static bool IsCompiled(CodeInfo codeInfo) => (IlEvmFrameRunner.CodeSlot(codeInfo) as IlCodeState)?.Contract is not null;

    /// <summary>The self-check of <paramref name="codeInfo"/>'s compiled contract: its verdict, and compiled time per opcode over the interpreter's.</summary>
    /// <summary>The self-check's verdicts so far in the process: kept compiled, put on probation, given back.</summary>
    public static (int Kept, int Probation, int GivenBack) SelfCheckVerdicts => IlSelfCheck.Verdicts;

    /// <summary>The IL of the contracts the shared cache holds, for tests and diagnostics.</summary>
    public static long CachedIlBytes => Interlocked.Read(ref _ilCachedBytes);

    /// <summary>The self-check's samples for <paramref name="codeInfo"/>, for diagnostics.</summary>
    public static string? DescribeSelfCheck(CodeInfo codeInfo) => (IlEvmFrameRunner.CodeSlot(codeInfo) as IlCodeState)?.Contract?.Check.Describe();

    public static (string Verdict, double Ratio, int Samples)? SelfCheckOf(CodeInfo codeInfo) =>
        (IlEvmFrameRunner.CodeSlot(codeInfo) as IlCodeState)?.Contract?.Check is { } check
            ? (check.Verdict switch { IlSelfCheck.Compiled => "compiled", IlSelfCheck.Interpreted => "interpreted", IlSelfCheck.Probation => "probation", _ => "undecided" }, check.Ratio, check.InterpretedSamples)
            : null;

    /// <summary>How the compiler runs an opcode.</summary>
    internal enum IlKind : byte
    {
        /// <summary>Handed back to the interpreter at the instruction.</summary>
        Unsupported,
        Push,
        Dup,
        Swap,
        Pop,
        JumpDest,
        Jump,
        JumpIf,
        ProgramCounter,
        Gas,
        Stop,
        /// <summary>A body whose gas and stack checks the dispatch performs; the compiler hoists them into the segment.</summary>
        CheckedBody,
        /// <summary>A checked opcode the compiler runs itself, through its operation on the top stack slot.</summary>
        Native,
        /// <summary>MLOAD: a word already in memory read in place, anything else through its body.</summary>
        MLoad,
        /// <summary>MSTORE: a word that needs no clearing and no new backing written in place, anything else through its body.</summary>
        MStore,
        /// <summary>A memory opcode run through its body inside the segment, at the gas and stack the interpreter would hold.</summary>
        EmbeddedBody,
        /// <summary>A body that charges and checks for itself and may halt or suspend the frame.</summary>
        Body,
        /// <summary>A body after which the frame always ends.</summary>
        TerminatingBody,
    }

    internal sealed class IlOp
    {
        public IlOp Clone() => (IlOp)MemberwiseClone();

        public IlKind Kind;
        public ulong StaticGas;
        public int StackInputs;
        public int StackGrowth;
        public int Count;
        public bool UsesVm;
        /// <summary>Whether the opcode charges gas beyond <see cref="StaticGas"/>, which a segment cannot precompute.</summary>
        public bool Dynamic;
        /// <summary>Whether the order of the opcode's own gas and stack checks is not known, so a failed segment precondition cannot be walked past it.</summary>
        public bool Opaque;
        public bool IsCallDataLoad;
        public MethodInfo? Execute;
        /// <summary>The body's <c>Execute</c> entry point, called through <c>calli</c> so the JIT never inlines it.</summary>
        public nint ExecutePointer;
        /// <summary>The helper a <see cref="IlKind.Native"/> opcode runs on the top slot.</summary>
        public MethodInfo? Native;
        /// <summary>The helper a <see cref="IlKind.Native"/> binary opcode runs when one operand is a constant.</summary>
        public MethodInfo? NativeConst;
        /// <summary>For a comparison, the helper that answers it as a branchable flag instead of a word.</summary>
        public MethodInfo? Predicate;
        public MethodInfo? PredicateConst;
        /// <summary>The opcode's <c>Operation</c>, run at compile time to fold constant operands.</summary>
        public MethodInfo? Operation;
        public IlFamily Family;
        public bool IsPredicate;
        public Type? BodyType;
        public string? Name;
    }

    /// <summary>The operation shapes of <see cref="IlKind.Native"/> opcodes.</summary>
    internal enum IlFamily : byte
    {
        None,
        /// <summary><see cref="EvmInstructions.IOpMath2Param"/>: two words to one, by reference.</summary>
        Math2,
        /// <summary><see cref="EvmInstructions.IOpBitwise"/>: two words to one, by value.</summary>
        Bitwise,
        /// <summary><see cref="EvmInstructions.IOpMath1Param"/>: one word to one.</summary>
        Math1,
        /// <summary><see cref="EvmInstructions.IOpShift"/>: an amount and a word to one.</summary>
        Shift,
    }


    /// <summary>How the compiler runs each opcode of a fork, from what the core reports about the bodies of its dispatch table.</summary>
    /// <remarks>
    /// Bodies are recognized by name only to pick a faster path; whatever is not recognized runs through its own Execute
    /// with the core's stack and gas declarations, or hands back to the interpreter.
    /// </remarks>
    private static IlOp[] IlDescribeOps(OpcodeBodySet bodies)
    {
        IlOp[] ops = new IlOp[256];
        for (int opcode = 0; opcode < 256; opcode++)
        {
            IlOp op = new() { Kind = IlKind.Unsupported };
            ops[opcode] = op;
            OpcodeBody body = bodies.Bodies[opcode];
            if (body.Flow == OpcodeFlow.Unrecorded)
            {
                op.Name = "unrecorded";
                continue;
            }

            Instruction instruction = (Instruction)opcode;
            if (body.Flow == OpcodeFlow.ConditionalJump)
            {
                op.Kind = IlKind.JumpIf;
                op.StaticGas = body.FixedGas;
                op.StackInputs = body.StackInputs;
                op.StackGrowth = body.StackGrowth;
                op.Name = "JUMPI";
                continue;
            }

            Type bodyType = body.BodyType!;
            op.Name = bodyType.Name;
            op.BodyType = bodyType;
            MethodInfo? execute = bodyType.GetMethod("Execute", BindingFlags.Public | BindingFlags.Static);
            op.Execute = execute;
            op.ExecutePointer = execute?.MethodHandle.GetFunctionPointer() ?? 0;
            bool fixedGas = body.HasCheckedBody && body.FixedGas != ulong.MaxValue;

            bool IsBody(string name) => bodyType.Name == name || bodyType.Name.StartsWith(name + "`", StringComparison.Ordinal);

            if (!body.IsDefined)
            {
                op.Kind = IlKind.TerminatingBody;
                continue;
            }

            switch (instruction)
            {
                case Instruction.STOP when IsBody("StopOpcode"):
                    op.Kind = IlKind.Stop;
                    continue;
                case Instruction.PUSH0 when IsBody("Push0Opcode") && fixedGas:
                    op.Kind = IlKind.Push;
                    op.StaticGas = body.FixedGas;
                    op.StackGrowth = 1;
                    continue;
                case Instruction.PUSH2 when IsBody("Push2Opcode") && !bodies.UsesCallDestinations:
                    op.Kind = IlKind.Push;
                    op.StaticGas = VeryLowGasCost.GasCost;
                    op.StackGrowth = 1;
                    op.Count = 2;
                    continue;
                case >= Instruction.PUSH1 and <= Instruction.PUSH32 when IsBody("PushOpcode") && fixedGas:
                    op.Kind = IlKind.Push;
                    op.StaticGas = body.FixedGas;
                    op.StackGrowth = 1;
                    op.Count = opcode - (int)Instruction.PUSH1 + 1;
                    continue;
                case >= Instruction.DUP1 and <= Instruction.DUP16 when IsBody("DupOpcode") && fixedGas:
                    op.Kind = IlKind.Dup;
                    op.StaticGas = body.FixedGas;
                    op.Count = opcode - (int)Instruction.DUP1 + 1;
                    op.StackInputs = op.Count;
                    op.StackGrowth = 1;
                    continue;
                case >= Instruction.SWAP1 and <= Instruction.SWAP16 when IsBody("SwapOpcode") && fixedGas:
                    op.Kind = IlKind.Swap;
                    op.StaticGas = body.FixedGas;
                    op.Count = opcode - (int)Instruction.SWAP1 + 1;
                    op.StackInputs = op.Count + 1;
                    continue;
                case Instruction.POP when IsBody("PopOpcode") && fixedGas:
                    op.Kind = IlKind.Pop;
                    op.StaticGas = body.FixedGas;
                    op.StackInputs = 1;
                    op.StackGrowth = -1;
                    continue;
                case Instruction.JUMPDEST when IsBody("JumpDestOpcode") && fixedGas:
                    op.Kind = IlKind.JumpDest;
                    op.StaticGas = body.FixedGas;
                    continue;
                case Instruction.JUMP when IsBody("JumpOpcode"):
                    op.Kind = IlKind.Jump;
                    op.StaticGas = JumpGasCost.GasCost;
                    op.StackInputs = 1;
                    op.StackGrowth = -1;
                    continue;
                case Instruction.PC when IsBody("ProgramCounterOpcode") && fixedGas:
                    op.Kind = IlKind.ProgramCounter;
                    op.StaticGas = body.FixedGas;
                    op.StackGrowth = 1;
                    continue;
                case Instruction.GAS when IsBody("GasOpcode") && fixedGas:
                    op.Kind = IlKind.Gas;
                    op.StaticGas = body.FixedGas;
                    op.StackGrowth = 1;
                    continue;
            }

            if (execute is null || op.ExecutePointer == 0 || IsBody("DupNOpcode") || IsBody("SwapNOpcode") || IsBody("ExchangeOpcode") || body.MayJump)
            {
                op.Name += " (unsupported)";
                continue;
            }

            if (IlTryNative(body, bodyType, op))
                continue;

            if (IsBody("MLoadOpcode") || IsBody("MStoreOpcode") || IsBody("MStore8Opcode"))
            {
                // Memory words: VeryLow gas and the stack checked before expansion, as the bodies do.
                op.Kind = IsBody("MLoadOpcode") ? IlKind.MLoad : IsBody("MStoreOpcode") ? IlKind.MStore : IlKind.EmbeddedBody;
                op.StaticGas = VeryLowGasCost.GasCost;
                op.StackInputs = op.Kind == IlKind.MLoad ? 1 : 2;
                op.StackGrowth = op.Kind == IlKind.MLoad ? 0 : -2;
                op.Dynamic = true;
                continue;
            }

            if (fixedGas)
            {
                op.IsCallDataLoad = IsBody("CallDataLoadOpcode");
                op.Kind = IlKind.CheckedBody;
                op.StaticGas = body.FixedGas;
                op.StackInputs = body.StackInputs;
                op.StackGrowth = body.StackGrowth;
                op.UsesVm = body.UsesVm;
                continue;
            }

            op.Kind = body.Flow == OpcodeFlow.Terminates ? IlKind.TerminatingBody : IlKind.Body;
            if (op.Kind == IlKind.Body && !IsBody("CallOpcode") && !IsBody("CreateOpcode") && IlEmbeddableStackEffect(instruction) is var (inputs, outputs))
            {
                // Charges and checks for itself, in an order the compiler does not model; inside a segment it runs at the
                // interpreter's exact gas and stack, and a segment that fails before it is handed back whole.
                op.Kind = IlKind.EmbeddedBody;
                op.StackInputs = inputs;
                op.StackGrowth = outputs - inputs;
                op.Dynamic = true;
                op.Opaque = true;
            }
        }

        return ops;
    }

    /// <summary>Describes a checked arithmetic, comparison, bitwise or shift body as an operation the compiler runs itself.</summary>
    /// <remarks>
    /// Each runs the opcode's own <c>Operation</c>, the one the interpreter's body runs, on the slots at the top of the stack
    /// rather than through <see cref="EvmStack.Head"/>.
    /// </remarks>
    private static bool IlTryNative(OpcodeBody body, Type bodyType, IlOp op)
    {
        if (!body.HasCheckedBody || body.FixedGas == ulong.MaxValue || !bodyType.IsGenericType)
            return false;

        string name = bodyType.Name;
        Type[] arguments = bodyType.GetGenericArguments();
        Type? Find(Type contract) => Array.Find(arguments, contract.IsAssignableFrom);
        // The shift operations are internal to the core, so they are found by name; they run as direct forms or bodies.
        Type? FindShift() => Array.Find(arguments, static a => a.Name is "OpShl" or "OpShr");

        string? helper = null;
        Type? operation = null;
        if (name.StartsWith("Math2Opcode`", StringComparison.Ordinal) && (operation = Find(typeof(EvmInstructions.IOpMath2Param))) is not null)
            // ADD and SUB are alias-safe, as the interpreter's vector path relies on; MUL, DIV and MOD are not.
            helper = operation == typeof(EvmInstructions.OpAdd) || operation == typeof(EvmInstructions.OpSub) ? nameof(IlMath2InPlace) : nameof(IlMath2);
        else if (name.StartsWith("BitwiseOpcode`", StringComparison.Ordinal) && (operation = Find(typeof(EvmInstructions.IOpBitwise))) is not null)
            helper = nameof(IlBitwise);
        else if (name.StartsWith("Math1Opcode`", StringComparison.Ordinal) && Vector256.IsHardwareAccelerated && (operation = Find(typeof(EvmInstructions.IOpMath1Param))) is not null)
            // ISZERO's operation is only used where a 256-bit register exists.
            helper = nameof(IlMath1);
        else if (name.StartsWith("ShiftOpcode`", StringComparison.Ordinal) && (operation = FindShift()) is not null)
            helper = "Shift";
        else if (name.StartsWith("CountLeadingZerosOpcode`", StringComparison.Ordinal) && Vector256.IsHardwareAccelerated)
            (operation, helper) = (typeof(IlOpClz), nameof(IlMath1));

        if (helper is null)
            return false;

        op.Kind = IlKind.Native;
        op.StaticGas = body.FixedGas;
        op.StackInputs = body.StackInputs;
        op.StackGrowth = body.StackGrowth;
        op.UsesVm = body.UsesVm;
        op.Native = helper == "Shift" ? null : IlHelper(helper, operation!);
        op.Family = helper switch
        {
            nameof(IlBitwise) => IlFamily.Bitwise,
            nameof(IlMath1) => IlFamily.Math1,
            "Shift" => IlFamily.Shift,
            _ => IlFamily.Math2,
        };
        op.Operation = operation!.GetMethod("Operation", BindingFlags.Public | BindingFlags.Static);
        op.IsPredicate = operation == typeof(EvmInstructions.OpLt) || operation == typeof(EvmInstructions.OpGt) ||
            operation == typeof(EvmInstructions.OpSLt) || operation == typeof(EvmInstructions.OpSGt) ||
            operation == typeof(EvmInstructions.OpBitwiseEq) || operation == typeof(EvmInstructions.OpIsZero);
        op.NativeConst = op.Family switch
        {
            IlFamily.Math2 => IlHelper(nameof(IlMath2Const), operation),
            IlFamily.Bitwise => IlHelper(nameof(IlBitwiseConst), operation),
            _ => null,
        };
        if (op.IsPredicate)
        {
            op.Predicate = op.Family switch
            {
                IlFamily.Math2 => IlHelper(nameof(IlPredicate2), operation),
                IlFamily.Bitwise => IlHelper(nameof(IlPredicateBitwise), operation),
                _ => IlHelper(nameof(IlPredicate1), operation),
            };
            op.PredicateConst = op.Family switch
            {
                IlFamily.Math2 => IlHelper(nameof(IlPredicate2Const), operation),
                IlFamily.Bitwise => IlHelper(nameof(IlPredicateBitwiseConst), operation),
                _ => null,
            };
        }

        return op.Operation is not null;
    }

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Opcode bodies are rooted by the dispatch table; the compiler is off without dynamic code.")]
    private static MethodInfo IlHelper(string name, Type operation) =>
        typeof(IlEvm).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.MakeGenericMethod(operation);

    // Constant-operand forms: the constant arrives as four literal limbs, so the JIT folds it into the operation.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlMath2Const<TOpMath>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param
    {
        UInt256 constant = new(c0, c1, c2, c3);
        ref UInt256 value = ref Unsafe.As<byte, UInt256>(ref operand);
        UInt256 result;
        if (constantOnTop)
            TOpMath.Operation(in constant, in value, out result);
        else
            TOpMath.Operation(in value, in constant, out result);
        Unsafe.As<byte, UInt256>(ref destination) = result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlBitwiseConst<TOp>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise
    {
        EvmWord constant = Vector256.Create(c0, c1, c2, c3).AsByte();
        ref EvmWord value = ref Unsafe.As<byte, EvmWord>(ref operand);
        Unsafe.As<byte, EvmWord>(ref destination) = constantOnTop ? TOp.Operation(in constant, in value) : TOp.Operation(in value, in constant);
    }

    // Comparisons answered as a flag, through the opcode's own operation, for a JUMPI or ISZERO that consumes them.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlPredicate2<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param
    {
        ref UInt256 a = ref Unsafe.As<byte, UInt256>(ref top);
        TOpMath.Operation(in a, in Unsafe.Subtract(ref a, 1), out UInt256 result);
        return !result.IsZero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlPredicate2Const<TOpMath>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param
    {
        UInt256 constant = new(c0, c1, c2, c3);
        ref UInt256 value = ref Unsafe.As<byte, UInt256>(ref operand);
        UInt256 result;
        if (constantOnTop)
            TOpMath.Operation(in constant, in value, out result);
        else
            TOpMath.Operation(in value, in constant, out result);
        return !result.IsZero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlPredicateBitwise<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise
    {
        ref EvmWord a = ref Unsafe.As<byte, EvmWord>(ref top);
        return TOp.Operation(in a, in Unsafe.Subtract(ref a, 1)) != default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlPredicateBitwiseConst<TOp>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise
    {
        EvmWord constant = Vector256.Create(c0, c1, c2, c3).AsByte();
        ref EvmWord value = ref Unsafe.As<byte, EvmWord>(ref operand);
        return (constantOnTop ? TOp.Operation(in constant, in value) : TOp.Operation(in value, in constant)) != default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlPredicate1<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param =>
        TOp.Operation(Unsafe.As<byte, EvmWord>(ref top)) != default;

    /// <summary>MLOAD at a constant offset below 2^32 of a word inside the active, initialized memory; false for anything else.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlTryMLoadAt(ref EvmPooledMemory memory, ulong offset, ref byte destination)
    {
        ref byte source = ref GetActiveInitializedWord(ref memory, offset);
        if (Unsafe.IsNullRef(ref source))
            return false;

        Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<EvmWord>(ref source).ByteSwap());
        return true;
    }

    /// <summary>MSTORE at a constant offset below 2^32 of the word in <paramref name="value"/>; <see cref="ulong.MaxValue"/> where it needs the body.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong IlTryMStoreAt(ref EvmPooledMemory memory, ulong offset, ref byte value, ulong gas)
    {
        ulong expansion = GetWordOverwriteCost(ref memory, offset);
        if (expansion > gas)
            return ulong.MaxValue;

        ref byte destination = ref CommitWordOverwrite(ref memory, offset);
        Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<EvmWord>(ref value).ByteSwap());
        return gas - expansion;
    }

    /// <summary>MSTORE of a constant word, given in memory byte order, at a constant offset below 2^32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong IlTryMStoreWordAt(ref EvmPooledMemory memory, ulong offset, ulong m0, ulong m1, ulong m2, ulong m3, ulong gas)
    {
        ulong expansion = GetWordOverwriteCost(ref memory, offset);
        if (expansion > gas)
            return ulong.MaxValue;

        ref byte destination = ref CommitWordOverwrite(ref memory, offset);
        Unsafe.WriteUnaligned(ref destination, Vector256.Create(m0, m1, m2, m3));
        return gas - expansion;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlMath2<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param
    {
        ref UInt256 a = ref Unsafe.As<byte, UInt256>(ref top);
        ref UInt256 b = ref Unsafe.Subtract(ref a, 1);
        TOpMath.Operation(in a, in b, out UInt256 result);
        b = result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlMath2InPlace<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param
    {
        ref UInt256 a = ref Unsafe.As<byte, UInt256>(ref top);
        ref UInt256 b = ref Unsafe.Subtract(ref a, 1);
        TOpMath.Operation(in a, in b, out b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlBitwise<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise
    {
        ref EvmWord a = ref Unsafe.As<byte, EvmWord>(ref top);
        ref EvmWord b = ref Unsafe.Subtract(ref a, 1);
        b = TOp.Operation(in a, in b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlMath1<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param
    {
        ref EvmWord value = ref Unsafe.As<byte, EvmWord>(ref top);
        value = TOp.Operation(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref EvmPooledMemory IlMemoryOf(VirtualMachine<EthereumGasPolicy> vm) => ref vm.VmState.Memory;

    /// <summary>CLZ as a one-word operation, so it compiles as NOT does; the core runs it through a body of its own.</summary>
    internal struct IlOpClz : EvmInstructions.IOpMath1Param
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmWord Operation(EvmWord value)
        {
            Vector256<ulong> limbs = value.AsUInt64();
            ulong u3 = limbs.GetElement(3), u2 = limbs.GetElement(2), u1 = limbs.GetElement(1), u0 = limbs.GetElement(0);
            // A zero word counts 192 + 64.
            int count = u3 != 0 ? System.Numerics.BitOperations.LeadingZeroCount(u3)
                : u2 != 0 ? 64 + System.Numerics.BitOperations.LeadingZeroCount(u2)
                : u1 != 0 ? 128 + System.Numerics.BitOperations.LeadingZeroCount(u1)
                : 192 + System.Numerics.BitOperations.LeadingZeroCount(u0);
            return Vector256.CreateScalar((ulong)count).AsByte();
        }
    }

    // The memory's word fast paths are internal to the core; the accessors bind them by name and inline like direct calls.

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetActiveInitializedWord")]
    private static extern ref byte GetActiveInitializedWord(ref EvmPooledMemory memory, ulong offset);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "GetWordOverwriteCost")]
    private static extern ulong GetWordOverwriteCost(ref EvmPooledMemory memory, ulong offset);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "CommitWordOverwrite")]
    private static extern ref byte CommitWordOverwrite(ref EvmPooledMemory memory, ulong offset);

    /// <summary>Carries one into limb 1 and up of the word at <paramref name="slot"/>, for an addition whose low limb wrapped.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlCarry(ref byte slot)
    {
        ref ulong limbs = ref Unsafe.As<byte, ulong>(ref slot);
        if (++Unsafe.Add(ref limbs, 1) != 0) return;
        if (++Unsafe.Add(ref limbs, 2) != 0) return;
        ++Unsafe.Add(ref limbs, 3);
    }

    /// <summary>Borrows one from limb 1 and up of the word at <paramref name="slot"/>, for a subtraction whose low limb wrapped.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlBorrow(ref byte slot)
    {
        ref ulong limbs = ref Unsafe.As<byte, ulong>(ref slot);
        if (Unsafe.Add(ref limbs, 1)-- != 0) return;
        if (Unsafe.Add(ref limbs, 2)-- != 0) return;
        Unsafe.Add(ref limbs, 3)--;
    }

    // Called twins of the helpers above: compiled code calls these instead when the helpers are not to be inlined,
    // trading a call for much smaller code. Each is found by its helper's name with the "Called" suffix.

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlMath2Called<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlMath2<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlMath2InPlaceCalled<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlMath2InPlace<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlMath2ConstCalled<TOpMath>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param =>
        IlMath2Const<TOpMath>(ref operand, ref destination, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlBitwiseCalled<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise => IlBitwise<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlBitwiseConstCalled<TOp>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise =>
        IlBitwiseConst<TOp>(ref operand, ref destination, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlMath1Called<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param => IlMath1<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlPredicate2Called<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlPredicate2<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlPredicate2ConstCalled<TOpMath>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param =>
        IlPredicate2Const<TOpMath>(ref operand, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlPredicateBitwiseCalled<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise => IlPredicateBitwise<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlPredicateBitwiseConstCalled<TOp>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise =>
        IlPredicateBitwiseConst<TOp>(ref operand, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlPredicate1Called<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param => IlPredicate1<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlTryMLoadCalled(ref EvmPooledMemory memory, ref byte slot) => IlTryMLoad(ref memory, ref slot);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong IlTryMStoreCalled(ref EvmPooledMemory memory, ref byte offsetSlot, ulong gas) => IlTryMStore(ref memory, ref offsetSlot, gas);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlTryMLoadAtCalled(ref EvmPooledMemory memory, ulong offset, ref byte destination) => IlTryMLoadAt(ref memory, offset, ref destination);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong IlTryMStoreAtCalled(ref EvmPooledMemory memory, ulong offset, ref byte value, ulong gas) => IlTryMStoreAt(ref memory, offset, ref value, gas);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong IlTryMStoreWordAtCalled(ref EvmPooledMemory memory, ulong offset, ulong m0, ulong m1, ulong m2, ulong m3, ulong gas) =>
        IlTryMStoreWordAt(ref memory, offset, m0, m1, m2, m3, gas);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlCallDataLoadAtCalled(ref byte input, nint length, ulong offset, ref byte destination) => IlCallDataLoadAt(ref input, length, offset, ref destination);

    private static readonly ConcurrentDictionary<MethodInfo, MethodInfo> _ilCalledTwins = new();

    /// <summary>The called twin of an inlined helper, found by name.</summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Opcode bodies are rooted by the dispatch table; the compiler is off without dynamic code.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The twins are private static methods of this type, referenced here.")]
    private static MethodInfo IlCalledTwin(MethodInfo helper) =>
        _ilCalledTwins.GetOrAdd(helper, static h =>
        {
            MethodInfo twin = typeof(IlEvm).GetMethod(h.Name + "Called", BindingFlags.NonPublic | BindingFlags.Static)!;
            return h.IsGenericMethod ? twin.MakeGenericMethod(h.GetGenericArguments()) : twin;
        });

    /// <summary>MLOAD of a word inside the active, initialized memory, which needs no gas beyond the opcode's; false for anything else.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlTryMLoad(ref EvmPooledMemory memory, ref byte slot)
    {
        ref ulong offset = ref Unsafe.As<byte, ulong>(ref slot);
        if ((Unsafe.Add(ref offset, 1) | Unsafe.Add(ref offset, 2) | Unsafe.Add(ref offset, 3) | (offset >> 32)) != 0)
            return false;

        ref byte source = ref GetActiveInitializedWord(ref memory, offset);
        if (Unsafe.IsNullRef(ref source))
            return false;

        // Memory holds the word big-endian; the slot takes it in limb layout, over the offset it held.
        Unsafe.WriteUnaligned(ref slot, Unsafe.ReadUnaligned<EvmWord>(ref source).ByteSwap());
        return true;
    }

    /// <summary>
    /// MSTORE of a word that needs no clearing and no new backing, paying its expansion out of <paramref name="gas"/>;
    /// <see cref="ulong.MaxValue"/> for anything else.
    /// </summary>
    /// <returns>The gas left after the expansion.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong IlTryMStore(ref EvmPooledMemory memory, ref byte offsetSlot, ulong gas)
    {
        ref ulong offset = ref Unsafe.As<byte, ulong>(ref offsetSlot);
        if ((Unsafe.Add(ref offset, 1) | Unsafe.Add(ref offset, 2) | Unsafe.Add(ref offset, 3) | (offset >> 32)) != 0)
            return ulong.MaxValue;

        ulong expansion = GetWordOverwriteCost(ref memory, offset);
        if (expansion > gas)
            return ulong.MaxValue;

        ref byte destination = ref CommitWordOverwrite(ref memory, offset);
        // The value sits below the offset in limb layout; memory holds it big-endian.
        Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<EvmWord>(ref Unsafe.Subtract(ref offsetSlot, EvmStack.WordSize)).ByteSwap());
        return gas - expansion;
    }

    /// <summary>Runs an embedded body for compiled code and finishes its segment's charge, or says how the frame leaves.</summary>
    /// <remarks>
    /// The caller has returned the segment's charge for this opcode and the ones after it, so the body sees the
    /// interpreter's gas. Once it has run, the rest of the segment is charged again; gas too short for that means the
    /// interpreter faults in the rest, which the walk finds, or hands over after this opcode where the walk cannot
    /// tell. A body that halts, or goes on somewhere else than past itself, ends the compiled run there.
    /// </remarks>
    /// <param name="opsAdjust">What the caller adds to its opcode count when the frame leaves.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> to go on with the segment; <see cref="IlDeopt"/> for the interpreter to
    /// go on from <paramref name="programCounter"/> once the values still virtual are written out; any other value
    /// ends the frame with that result. Gas, head and counter are then the frame's.
    /// </returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static EvmExceptionType IlEmbedded(IlChunk chunk, int site, VirtualMachine<EthereumGasPolicy> vm, ref EvmStack stack, ref EthereumGasPolicy gas, ref nint programCounter, out int opsAdjust, nint head, ulong gasNow)
    {
        ref readonly IlSite info = ref chunk.Sites[site];
        // The interpreter's head and gas before the opcode: the segment's charge for it and the ones after it comes back.
        stack.Head = head + info.HeadDelta;
        gas.Value = gasNow + info.GasBack;
        // Bodies see the counter past their opcode byte, as the dispatch loop passes it.
        nint pc = info.Pc + 1;
        EvmExceptionType result = ((delegate*<ref EvmStack, ref EthereumGasPolicy, VirtualMachine<EthereumGasPolicy>, ref nint, EvmExceptionType>)info.Execute)(ref stack, ref gas, vm, ref pc);
        if (result == EvmExceptionType.None && pc == info.NextPc)
        {
            ref ulong left = ref gas.Value;
            if (left >= info.GasAfter)
            {
                left -= info.GasAfter;
                opsAdjust = 0;
                return EvmExceptionType.None;
            }

            programCounter = info.NextPc;
            result = chunk.Contract.FailSegment(info.FirstAfter, info.CountedAfter, left, stack.Head, 0, out ulong gasLeft, out int walked);
            if (result == IlDeopt)
            {
                opsAdjust = -info.CountedAfter;
                return IlDeopt;
            }

            left = gasLeft;
            opsAdjust = walked - info.CountedAfter;
            return result;
        }

        programCounter = pc;
        opsAdjust = -info.CountedAfter;
        return result == EvmExceptionType.None ? IlDeopt : result;
    }

    /// <summary>The stack inputs and outputs of the opcodes that may run inside a segment through their bodies.</summary>
    /// <remarks>None of them suspends the frame, jumps, or reads the program counter.</remarks>
    private static (int Inputs, int Outputs)? IlEmbeddableStackEffect(Instruction instruction) => instruction switch
    {
        Instruction.EXP => (2, 1),
        Instruction.KECCAK256 => (2, 1),
        Instruction.BALANCE => (1, 1),
        Instruction.CALLDATACOPY => (3, 0),
        Instruction.CODECOPY => (3, 0),
        Instruction.EXTCODESIZE => (1, 1),
        Instruction.EXTCODECOPY => (4, 0),
        Instruction.RETURNDATACOPY => (3, 0),
        Instruction.EXTCODEHASH => (1, 1),
        Instruction.BLOCKHASH => (1, 1),
        Instruction.SELFBALANCE => (0, 1),
        Instruction.BLOBHASH => (1, 1),
        Instruction.SLOAD => (1, 1),
        Instruction.SSTORE => (2, 0),
        Instruction.TLOAD => (1, 1),
        Instruction.TSTORE => (2, 0),
        Instruction.MCOPY => (3, 0),
        >= Instruction.LOG0 and <= Instruction.LOG4 => (2 + (instruction - Instruction.LOG0), 0),
        _ => null,
    };

    /// <summary>CALLDATALOAD at a constant offset below 2^64, written straight into <paramref name="destination"/>, as the body writes it.</summary>
    /// <param name="input">The frame's input data, which the chunk reads from the stack.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlCallDataLoadAt(ref byte input, nint length, ulong offset, ref byte destination)
    {
        if (offset >= (ulong)length)
        {
            Unsafe.InitBlockUnaligned(ref destination, 0, EvmStack.WordSize);
            return;
        }

        uint available = (uint)length - (uint)offset;
        ref byte source = ref Unsafe.Add(ref input, (nint)offset);
        if (available >= EvmStack.WordSize)
        {
            Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<EvmWord>(ref source).ByteSwap());
            return;
        }

        // Past the end the word reads as zeros.
        EvmWord word = default;
        Unsafe.CopyBlockUnaligned(ref Unsafe.As<EvmWord, byte>(ref word), ref source, available);
        Unsafe.WriteUnaligned(ref destination, word.ByteSwap());
    }

    /// <summary>Compiles one queued contract at a time on a background thread.</summary>
    internal static class IlCompileQueue
    {
        private static readonly BlockingCollection<Action> _work = [];
        private static int _started;

        /// <summary>Queues <paramref name="work"/> for the background compiler, unless <see cref="IlEvmSettings.MaxPendingCompiles"/> wait already.</summary>
        public static bool Enqueue(Action work)
        {
            if (_work.Count >= IlEvmSettings.MaxPendingCompiles)
                return false;
            _work.Add(work);
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                Thread worker = new(Run) { IsBackground = true, Name = "EVM IL compiler", Priority = ThreadPriority.BelowNormal };
                worker.Start();
            }

            return true;
        }

        private static void Run()
        {
            foreach (Action work in _work.GetConsumingEnumerable())
            {
                try
                {
                    work();
                }
                catch
                {
                    // IlCompileInto records its own failures; nothing else may take the worker down.
                }
            }
        }
    }

    internal sealed class IlInstruction
    {
        public int Pc;
        public byte Opcode;
        public int Size;
        public IlOp Op = null!;
        public ulong U0, U1, U2, U3;
        /// <summary>Static gas of the instructions after this one in its segment, which the segment has already charged.</summary>
        public ulong GasAfter;
        /// <summary>The instruction ends its segment, so the one after it starts a unit a body may continue at.</summary>
        public bool EndsSegment;
        /// <summary>A body that may run the next instruction itself, and then continues past it.</summary>
        public bool MayFuseNext;
    }

    internal sealed class IlUnit
    {
        public int StartPc;
        public bool IsSegment;
        public bool IsJumpDest;
        public readonly List<IlInstruction> Instructions = [];
        public ulong Gas;
        public int MinHeight;
        public int MaxPeak;
        public int EndDelta;
        public int FirstTableIndex;
        /// <summary>For a segment, roughly the IL instructions it compiles to; see <see cref="IlCompiler.SegmentEstimate"/>.</summary>
        public int Il;
    }

    /// <summary>Translates EVM bytecode into a dynamic method with the contract of <see cref="RunDispatchLoop{TTracingInst, TCancelable}"/>.</summary>
    /// <remarks>
    /// <para>
    /// The code is cut into units. A segment is a straight run of opcodes the compiler runs itself or through
    /// checked bodies; it charges its static gas and checks the stack bounds of all its opcodes once, at its start,
    /// and addresses the stack at offsets fixed when it is compiled. Every other opcode is a unit of its own that
    /// calls the interpreter's body with the exact gas and stack, as the dispatch loop does.
    /// </para>
    /// <para>
    /// Whatever the compiled code does not handle itself - a segment it cannot afford or whose stack bounds fail,
    /// an invalid jump destination, an unsupported opcode, an entry offset it has no unit for - it hands back at an
    /// instruction boundary, with gas, stack and memory exactly as the interpreter would have them before that
    /// instruction, and the interpreter runs the rest of the frame. That keeps every failure, and its error, the
    /// interpreter's.
    /// </para>
    /// </remarks>
    internal static class IlCompiler
    {
        private const int MaxCodeLength = 64 * 1024;
        /// <summary>RyuJIT compiles a method of more IL than this without optimizations (<c>DEFAULT_MIN_OPTS_CODE_AMOUNT</c> is 60000).</summary>
        private const int MaxIlBytes = 56_000;
        /// <summary>Kept under RyuJIT's <c>DEFAULT_MIN_OPTS_BB_COUNT</c> of 2000 basic blocks, with headroom for those the JIT adds.</summary>
        private const int MaxLabels = 1_700;
        /// <summary>Kept under RyuJIT's <c>DEFAULT_MIN_OPTS_INSTR_COUNT</c> of 20000 IL instructions.</summary>
        private const int MaxIlInstructions = 18_000;
        /// <summary>Kept under RyuJIT's <c>DEFAULT_MIN_OPTS_LV_REF_COUNT</c> of 8000 references to locals and arguments.</summary>
        private const int MaxLocalReferences = 7_000;
        /// <summary>Kept under RyuJIT's <c>DEFAULT_MIN_OPTS_LV_NUM_COUNT</c> of 2000 locals.</summary>
        private const int MaxLocals = 1_500;
        private const int MinChunkInstructions = 40;
        /// <summary>A segment is cut once its estimate reaches this fraction of a chunk's, so chunks hold several and a long run spans chunks.</summary>
        private const int SegmentShare = 4;
        /// <summary>The smallest segment estimate the recut goes down to; a few instructions with their checks.</summary>
        private const int MinSegmentIl = 120;

        private static readonly FieldInfo GasValue = typeof(EthereumGasPolicy).GetField(nameof(EthereumGasPolicy.Value))!;
        private static readonly FieldInfo StackHead = typeof(EvmStack).GetField(nameof(EvmStack.Head))!;
        /// <summary>The stack's word storage, a private ref field the chunks address slots from.</summary>
        private static readonly FieldInfo StackBase = typeof(EvmStack).GetField("_stack", BindingFlags.NonPublic | BindingFlags.Instance)!;
        /// <summary>The frame's input data and its length, internal fields of the stack the CALLDATALOAD fast path reads.</summary>
        private static readonly FieldInfo StackInputData = typeof(EvmStack).GetField("InputData", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly FieldInfo StackInputDataLength = typeof(EvmStack).GetField("InputDataLength", BindingFlags.NonPublic | BindingFlags.Instance)!;
        private static readonly MethodInfo EmbeddedMethod = typeof(IlEvm).GetMethod(nameof(IlEmbedded), BindingFlags.NonPublic | BindingFlags.Static)!;
        /// <summary>The signature of a body's Execute, for <c>calli</c>.</summary>
        private static readonly Type[] BodySignature = [typeof(EvmStack).MakeByRefType(), typeof(EthereumGasPolicy).MakeByRefType(), typeof(VirtualMachine<EthereumGasPolicy>), typeof(nint).MakeByRefType()];

        /// <summary>Whether the core members the generated code binds by name are all there, and if not which are missing.</summary>
        public static string? MissingBindings()
        {
            List<string> missing = [];
            if (StackBase is null || StackBase.FieldType != typeof(byte).MakeByRefType()) missing.Add("EvmStack._stack");
            if (StackInputData is null) missing.Add("EvmStack.InputData");
            if (StackInputDataLength is null || StackInputDataLength.FieldType != typeof(nint)) missing.Add("EvmStack.InputDataLength");
            return missing.Count == 0 ? null : string.Join(", ", missing);
        }
        private static readonly FieldInfo EntryIndexField = typeof(IlChunk).GetField(nameof(IlChunk.EntryIndex))!;
        private static readonly FieldInfo JumpOrdinalField = typeof(IlChunk).GetField(nameof(IlChunk.JumpOrdinal))!;
        private static readonly FieldInfo JumpCasesField = typeof(IlChunk).GetField(nameof(IlChunk.JumpCases))!;
        private static readonly FieldInfo JumpBaseField = typeof(IlChunk).GetField(nameof(IlChunk.JumpBase))!;
        /// <summary>The opcode count's backing field, which compiled code updates directly; the property where it is not found.</summary>
        private static readonly FieldInfo? OpCodeCountField = typeof(VirtualMachine<EthereumGasPolicy>).GetField("<OpCodeCount>k__BackingField", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo StateOpCodes = typeof(IlRunState).GetField(nameof(IlRunState.OpCodes))!;
        private static readonly FieldInfo StatePollAt = typeof(IlRunState).GetField(nameof(IlRunState.PollAt))!;
        private static readonly MethodInfo FailAtMethod = typeof(IlChunk).GetMethod(nameof(IlChunk.FailAt), BindingFlags.Public | BindingFlags.Instance)!;
        private static readonly MethodInfo GetOpCodeCount = typeof(VirtualMachine<EthereumGasPolicy>).GetProperty(nameof(VirtualMachine<EthereumGasPolicy>.OpCodeCount))!.GetGetMethod()!;
        private static readonly MethodInfo SetOpCodeCount = typeof(VirtualMachine<EthereumGasPolicy>).GetProperty(nameof(VirtualMachine<EthereumGasPolicy>.OpCodeCount))!.GetSetMethod()!;
        private static readonly MethodInfo PollCancellation = typeof(IlEvmFrameRunner).GetMethod(nameof(IlEvmFrameRunner.Poll), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo MemoryOf = typeof(IlEvm).GetMethod(nameof(IlMemoryOf), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo TryMLoad = typeof(IlEvm).GetMethod(nameof(IlTryMLoad), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo TryMStore = typeof(IlEvm).GetMethod(nameof(IlTryMStore), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo TryMLoadAt = typeof(IlEvm).GetMethod(nameof(IlTryMLoadAt), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo CallDataLoadAt = typeof(IlEvm).GetMethod(nameof(IlCallDataLoadAt), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo TryMStoreAt = typeof(IlEvm).GetMethod(nameof(IlTryMStoreAt), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo TryMStoreWordAt = typeof(IlEvm).GetMethod(nameof(IlTryMStoreWordAt), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo CarryMethod = typeof(IlEvm).GetMethod(nameof(IlCarry), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo VectorCreate = typeof(Vector256).GetMethod(nameof(Vector256.Create), [typeof(ulong), typeof(ulong), typeof(ulong), typeof(ulong)])!;
        private static readonly MethodInfo VectorCreateScalar = typeof(Vector256).GetMethod(nameof(Vector256.CreateScalar), [typeof(ulong)])!;
        private static readonly MethodInfo VectorAsByte = typeof(Vector256).GetMethod(nameof(Vector256.AsByte))!.MakeGenericMethod(typeof(ulong));
        private static readonly MethodInfo VectorOnesComplement = typeof(Vector256).GetMethod(nameof(Vector256.OnesComplement))!.MakeGenericMethod(typeof(byte));
        private static readonly MethodInfo BorrowMethod = typeof(IlEvm).GetMethod(nameof(IlBorrow), BindingFlags.NonPublic | BindingFlags.Static)!;

        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
        public static IlContract? Compile(CodeInfo codeInfo, IlFork fork, out string? reason)
        {
            if (fork.UsesCallDestinations)
            {
                reason = "EIP-7979 subroutines";
                return null;
            }

            ReadOnlySpan<byte> code = codeInfo.CodeSpan;
            if (code.Length == 0 || code.Length > MaxCodeLength)
            {
                reason = "code length";
                return null;
            }

            IlOp[] ops = fork.Ops;
            List<IlInstruction> instructions = Decode(code, ops);
            FusePeepholes(instructions);

            // Each chunk is cut small enough for the JIT to optimize: by instructions, estimated inline locals and
            // estimated IL, and no straight run of code is one unit past a share of that, however long it is. Every
            // chunk is emitted now, which is cheap next to the JIT; one that comes out too large has the whole contract
            // cut again, smaller, its long runs included.
            int instructionsPerChunk = IlEvmSettings.ChunkInstructions;
            int inlineBudget = IlEvmSettings.ChunkInlineBudget;
            int ilBudget = MaxIlEstimate;
            for (int attempt = 0; ; attempt++)
            {
                int[] jumpIndex = new int[code.Length];
                Array.Fill(jumpIndex, -1);
                List<IlUnit> units = Cut(instructions, ilBudget / SegmentShare);

                // The interpreter's bitmap decides which JUMPDESTs a jump may land on; it may mark fewer than the code holds,
                // as for code that starts with STOP, but never a byte that is not a JUMPDEST instruction.
                int jumpTargets = 0;
                foreach (IlUnit unit in units)
                {
                    if (unit.IsJumpDest && !codeInfo.ValidateJump(unit.StartPc))
                        unit.IsJumpDest = false;
                    if (unit.IsJumpDest)
                        jumpIndex[unit.StartPc] = jumpTargets++;
                }

                for (int pc = 0; pc < code.Length; pc++)
                {
                    if (codeInfo.ValidateJump(pc) && jumpIndex[pc] < 0)
                    {
                        reason = $"jump destination analysis disagrees at {pc}";
                        return null;
                    }
                }

                units = Reachable(units);

                List<ulong> costs = [];
                List<int> inputs = [];
                List<int> growths = [];
                List<bool> dynamic = [];
                List<bool> opaque = [];
                foreach (IlUnit unit in units)
                {
                    if (!unit.IsSegment) continue;
                    unit.FirstTableIndex = costs.Count;
                    foreach (IlInstruction instruction in unit.Instructions)
                    {
                        costs.Add(instruction.Op.StaticGas);
                        inputs.Add(instruction.Op.StackInputs);
                        growths.Add(instruction.Op.StackGrowth);
                        dynamic.Add(instruction.Op.Dynamic);
                        opaque.Add(instruction.Op.Opaque);
                    }
                }

                Dictionary<int, IlUnit> unitsByPc = new(units.Count);
                foreach (IlUnit unit in units)
                    unitsByPc[unit.StartPc] = unit;

                List<List<IlUnit>> chunks = IlEvmSettings.Regions
                    ? Regions(units, unitsByPc, code.Length, instructionsPerChunk, inlineBudget, ilBudget, IlEvmSettings.ChunkDuplication)
                    : Partition(units, instructionsPerChunk, inlineBudget, ilBudget);
                // A unit's home is the first chunk that compiles it: frames enter and resume there, and other chunks
                // that do not compile it continue there. A chunk's jump cases are its JUMPDEST units in order.
                int[] entryChunk = new int[code.Length];
                int[] entryIndex = new int[code.Length];
                Array.Fill(entryChunk, -1);
                // A chunk's table covers only the ordinals from its first JUMPDEST to its last, so the tables of a contract
                // grow with its code rather than with its chunks times its JUMPDESTs.
                short[][] jumpCases = new short[chunks.Count][];
                int[] jumpBases = new int[chunks.Count];
                long tableBytes = 12L * code.Length;
                int copies = 0;
                for (int c = 0; c < chunks.Count; c++)
                {
                    int lowest = int.MaxValue, highest = -1;
                    foreach (IlUnit unit in chunks[c])
                    {
                        if (!unit.IsJumpDest) continue;
                        lowest = Math.Min(lowest, jumpIndex[unit.StartPc]);
                        highest = Math.Max(highest, jumpIndex[unit.StartPc]);
                    }

                    short[] cases = jumpCases[c] = highest < 0 ? [] : new short[highest - lowest + 1];
                    jumpBases[c] = highest < 0 ? 0 : lowest;
                    tableBytes += 2L * cases.Length;
                    Array.Fill(cases, (short)-1);
                    short jumpCase = 0;
                    for (int u = 0; u < chunks[c].Count; u++)
                    {
                        IlUnit unit = chunks[c][u];
                        if (entryChunk[unit.StartPc] < 0)
                        {
                            entryChunk[unit.StartPc] = c;
                            entryIndex[unit.StartPc] = u;
                        }
                        else
                        {
                            copies++;
                        }

                        if (unit.IsJumpDest)
                            cases[jumpIndex[unit.StartPc] - jumpBases[c]] = jumpCase++;
                    }
                }

                string name = $"evm_{codeInfo.CodeSpan.FastHash():x8}";
                IlContract contract = new(fork, codeInfo.Code.ToArray(), entryChunk, entryIndex, jumpIndex, jumpCases, jumpBases, costs.ToArray(),
                    inputs.ToArray(), growths.ToArray(), dynamic.ToArray(), opaque.ToArray(), chunks.Count, name) { TableBytes = tableBytes };
                string? tooLarge = null;
                InlineBudget bodyBudget = new();
                for (int c = 0; c < chunks.Count && tooLarge is null; c++)
                    contract.Prepared[c] = EmitChunk(contract, c, chunks[c], unitsByPc, bodyBudget, out tooLarge)!;

                if (tooLarge is not null)
                {
                    if (ilBudget / SegmentShare <= MinSegmentIl || attempt > 12)
                    {
                        reason = tooLarge;
                        return null;
                    }

                    if (IlEvmDiagnostics.Enabled)
                        Console.WriteLine($"il-evm {name}: {tooLarge}; cutting again");
                    instructionsPerChunk = Math.Max(MinChunkInstructions, instructionsPerChunk * 3 / 4);
                    inlineBudget = Math.Max(16, inlineBudget * 3 / 4);
                    ilBudget = ilBudget * 3 / 4;
                    continue;
                }

                if (IlEvmDiagnostics.Enabled)
                    Console.WriteLine($"il-evm laid out {name}: {code.Length} B of code, {units.Count} units, {copies} copies, {chunks.Count} chunks, {contract.IlBytes / 1024} KiB IL");
                if (contract.IlBytes > IlEvmSettings.MaxContractIlBytes)
                {
                    // Nothing was JIT-compiled yet; the emitted methods are dropped with the contract.
                    reason = $"compiles to {contract.IlBytes} bytes of IL, past {IlEvmSettings.MaxContractIlBytes}";
                    return null;
                }

                if (IlEvmSettings.EagerChunks)
                    contract.CompileAllChunks();
                reason = null;
                return contract;
            }
        }

        /// <summary>Emits one chunk of a laid-out contract and binds it, leaving the JIT to the first frames that reach it.</summary>
        /// <returns>The chunk's runner, or null with <paramref name="failure"/> when the chunk is too large for the JIT to optimize.</returns>
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
        private static IlRunner? EmitChunk(IlContract contract, int index, List<IlUnit> units, Dictionary<int, IlUnit> unitsByPc, InlineBudget inlineBudget, out string? failure)
        {
            DynamicMethod method = new(
                $"{contract.Name}_{index}",
                typeof(EvmExceptionType),
                [typeof(IlChunk), typeof(VirtualMachine<EthereumGasPolicy>), typeof(EvmStack).MakeByRefType(), typeof(EthereumGasPolicy).MakeByRefType(), typeof(nint).MakeByRefType(), typeof(IlRunState).MakeByRefType()],
                typeof(IlEvm).Module,
                skipVisibility: true)
            {
                InitLocals = false
            };

            IlWriter generator = new(method.GetILGenerator());
            List<IlSegmentInfo> segments = [];
            List<IlSite> sites = [];
            Emitter emitter = new(generator, units, unitsByPc, contract.Code.Length, segments, sites, inlineBudget);
            emitter.Emit();
            // Past these the JIT compiles the method without optimizations, or takes very long to optimize it.
            if (generator.ILOffset > MaxIlBytes || emitter.Labels > MaxLabels || generator.Instructions > MaxIlInstructions ||
                generator.LocalReferences > MaxLocalReferences || generator.Locals > MaxLocals)
            {
                failure = $"chunk {index} too large to optimize: {units.Count} units, {generator.ILOffset}B/{generator.Instructions}i/{generator.LocalReferences}r/{generator.Locals}l/{emitter.Labels}b/{generator.InlineSites}s";
                return null;
            }

            IlChunk chunk = new(contract, contract.JumpCases[index], contract.JumpBases[index], segments.ToArray(), sites.ToArray());
            IlRunner runner = (IlRunner)method.CreateDelegate(typeof(IlRunner), chunk);
            contract.IlBytes += generator.ILOffset;
            failure = null;
            return runner;
        }

        /// <summary>The units execution can reach: the entry, whatever a reached unit falls through to, and every JUMPDEST a jump may land on.</summary>
        /// <remarks>
        /// The rest - padding, data and metadata after the code, code after a halt that nothing jumps to - is never run, so
        /// it is not compiled. A unit handed back to the interpreter counts as falling through, since the interpreter runs on
        /// past it and a CALL there resumes in compiled code.
        /// </remarks>
        private static List<IlUnit> Reachable(List<IlUnit> units)
        {
            List<IlUnit> reachable = new(units.Count);
            bool fallsInto = true;
            foreach (IlUnit unit in units)
            {
                if (fallsInto || unit.IsJumpDest)
                {
                    reachable.Add(unit);
                    fallsInto = FallsThrough(unit);
                }
                else
                {
                    fallsInto = false;
                }
            }

            return reachable;
        }

        private static bool FallsThrough(IlUnit unit) =>
            unit.Instructions[^1].Op.Kind switch
            {
                IlKind.Stop or IlKind.Jump or IlKind.TerminatingBody => false,
                _ => true,
            };

        /// <summary>Where a unit leads, for region forming.</summary>
        private sealed class IlEdges
        {
            /// <summary>The JUMPDEST a constant destination jumps to.</summary>
            public IlUnit? Jump;
            /// <summary>The jump is a dispatcher's selector match, so its target starts an external function.</summary>
            public bool JumpStartsFunction;
            /// <summary>The unit the last instruction falls into.</summary>
            public IlUnit? Fall;
            /// <summary>JUMPDESTs whose offsets the unit pushes for later, the return addresses of internal calls.</summary>
            public readonly List<IlUnit> Continuations = [];
        }

        private static IlEdges Edges(IlUnit unit, Dictionary<int, IlUnit> unitsByPc, int codeLength)
        {
            IlEdges edges = new();
            IlInstruction last = unit.Instructions[^1];
            if (FallsThrough(unit) && unitsByPc.TryGetValue(last.Pc + last.Size, out IlUnit? fall))
                edges.Fall = fall;
            if (!unit.IsSegment)
                return edges;

            // The segment's stack, from its start: small constants, whether a value is a 3- or 4-byte constant,
            // and whether it is an EQ against one, the dispatcher's selector match.
            List<(long Value, bool Selector, bool Match)> stack = [];
            (long Value, bool Selector, bool Match) unknown = (-1, false, false);
            List<long> pushed = [];

            (long Value, bool Selector, bool Match) Pop()
            {
                if (stack.Count == 0) return unknown;
                (long Value, bool Selector, bool Match) top = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                return top;
            }

            (long Value, bool Selector, bool Match) Peek(int depth) => depth <= stack.Count ? stack[^depth] : unknown;

            bool IsTarget(long value, [NotNullWhen(true)] out IlUnit? target)
            {
                target = null;
                return value >= 0 && unitsByPc.TryGetValue((int)value, out target) && target.IsJumpDest;
            }

            foreach (IlInstruction instruction in unit.Instructions)
            {
                IlOp op = instruction.Op;
                switch (op.Kind)
                {
                    case IlKind.Push:
                    {
                        bool small = (instruction.U1 | instruction.U2 | instruction.U3) == 0 && instruction.U0 < (ulong)codeLength;
                        long value = small ? (long)instruction.U0 : -1;
                        stack.Add((value, op.Count is 3 or 4, false));
                        if (value >= 0)
                            pushed.Add(value);
                        break;
                    }
                    case IlKind.Dup:
                        stack.Add(Peek(op.Count));
                        break;
                    case IlKind.Swap:
                        if (op.Count + 1 <= stack.Count)
                            (stack[^1], stack[^(op.Count + 1)]) = (stack[^(op.Count + 1)], stack[^1]);
                        else if (stack.Count > 0)
                            stack[^1] = unknown;
                        break;
                    case IlKind.Pop:
                        Pop();
                        break;
                    case IlKind.Jump:
                        if (IsTarget(Pop().Value, out IlUnit? jumpTarget))
                            edges.Jump = jumpTarget;
                        break;
                    case IlKind.JumpIf:
                    {
                        (long destination, _, _) = Pop();
                        (_, _, bool match) = Pop();
                        if (IsTarget(destination, out IlUnit? target))
                        {
                            edges.Jump = target;
                            edges.JumpStartsFunction = match;
                        }

                        break;
                    }
                    default:
                    {
                        bool match = instruction.Opcode == (byte)Instruction.EQ && (Peek(1).Selector || Peek(2).Selector);
                        for (int k = 0; k < op.StackInputs; k++)
                            Pop();
                        for (int k = 0; k < op.StackInputs + op.StackGrowth; k++)
                            stack.Add((-1, false, match));
                        break;
                    }
                }
            }

            foreach (long value in pushed)
            {
                if (IsTarget(value, out IlUnit? continuation) && continuation != edges.Jump && !edges.Continuations.Contains(continuation))
                    edges.Continuations.Add(continuation);
            }

            return edges;
        }

        /// <summary>Cuts the units into chunks along control flow, each grown depth first from a root as execution would go.</summary>
        /// <remarks>
        /// <para>
        /// A chunk follows jumps into callees before the return addresses they were given, so an internal call and its
        /// return stay in one method. The dispatcher's selector matches start new roots: each external function grows
        /// a chunk of its own, entered from the dispatcher once per frame. A unit another chunk already compiles is
        /// compiled again where it is reached, within <paramref name="duplication"/> copies per unit overall, so a helper
        /// shared by several functions runs inside each of them; past that it stays foreign and is continued at.
        /// </para>
        /// <para>
        /// Whatever the walk does not reach - destinations computed at run time, units past a full chunk - starts chunks
        /// of its own, so every unit has a home where frames enter it.
        /// </para>
        /// </remarks>
        private static List<List<IlUnit>> Regions(List<IlUnit> units, Dictionary<int, IlUnit> unitsByPc, int codeLength, int instructions, int inlineBudget, int ilBudget, double duplication)
        {
            Dictionary<IlUnit, IlEdges> edges = new(units.Count);
            foreach (IlUnit unit in units)
                edges[unit] = Edges(unit, unitsByPc, codeLength);

            HashSet<IlUnit> homed = new(units.Count);
            int copies = 0;
            int maxCopies = (int)Math.Min(int.MaxValue, units.Count * duplication);
            List<List<IlUnit>> regions = [];
            Queue<IlUnit> roots = new();
            roots.Enqueue(units[0]);
            int nextUnhomed = 0;
            while (true)
            {
                IlUnit? root = null;
                while (roots.Count > 0)
                {
                    IlUnit candidate = roots.Dequeue();
                    if (!homed.Contains(candidate))
                    {
                        root = candidate;
                        break;
                    }
                }

                if (root is null)
                {
                    while (nextUnhomed < units.Count && homed.Contains(units[nextUnhomed]))
                        nextUnhomed++;
                    if (nextUnhomed == units.Count)
                        break;
                    root = units[nextUnhomed];
                }

                List<IlUnit> region = [];
                HashSet<IlUnit> members = [];
                int count = 0;
                int weight = 0;
                int il = 0;
                Stack<IlUnit> walk = new();
                walk.Push(root);
                while (walk.Count > 0)
                {
                    IlUnit unit = walk.Pop();
                    if (members.Contains(unit))
                        continue;

                    bool copy = homed.Contains(unit);
                    if (copy && copies >= maxCopies)
                        continue;

                    int unitWeight = 0;
                    foreach (IlInstruction instruction in unit.Instructions)
                        unitWeight += InlineWeight(instruction);
                    int unitIl = IlEstimate(unit);
                    if (region.Count > 0 && (count + unit.Instructions.Count > instructions || weight + unitWeight > inlineBudget || il + unitIl > ilBudget))
                    {
                        if (!copy)
                            roots.Enqueue(unit);
                        continue;
                    }

                    region.Add(unit);
                    members.Add(unit);
                    count += unit.Instructions.Count;
                    weight += unitWeight;
                    il += unitIl;
                    if (copy)
                        copies++;
                    else
                        homed.Add(unit);

                    // Popped in reverse: the jump first, then what the unit falls into, then the return addresses.
                    IlEdges next = edges[unit];
                    for (int i = next.Continuations.Count - 1; i >= 0; i--)
                        walk.Push(next.Continuations[i]);
                    if (next.Fall is not null)
                        walk.Push(next.Fall);
                    if (next.Jump is not null)
                    {
                        if (next.JumpStartsFunction)
                            roots.Enqueue(next.Jump);
                        else
                            walk.Push(next.Jump);
                    }
                }

                // In code order, so a unit and the one it falls into are adjacent wherever both are here.
                region.Sort(static (a, b) => a.StartPc.CompareTo(b.StartPc));
                regions.Add(region);
            }

            return regions;
        }

        /// <summary>Groups consecutive units into chunks of at most <paramref name="instructions"/> instructions, units kept whole.</summary>
        private static List<List<IlUnit>> Partition(List<IlUnit> units, int instructions, int inlineBudget, int ilBudget)
        {
            List<List<IlUnit>> chunks = [];
            List<IlUnit> current = [];
            int count = 0;
            int weight = 0;
            int il = 0;
            foreach (IlUnit unit in units)
            {
                int unitWeight = 0;
                foreach (IlInstruction instruction in unit.Instructions)
                    unitWeight += InlineWeight(instruction);

                int unitIl = IlEstimate(unit);
                if (count > 0 && (count + unit.Instructions.Count > instructions || weight + unitWeight > inlineBudget || il + unitIl > ilBudget))
                {
                    chunks.Add(current);
                    current = [];
                    count = 0;
                    weight = 0;
                    il = 0;
                }

                current.Add(unit);
                count += unit.Instructions.Count;
                weight += unitWeight;
                il += unitIl;
            }

            if (current.Count > 0)
                chunks.Add(current);
            return chunks;
        }

        /// <summary>The IL instructions a chunk is cut at, with headroom under <see cref="MaxIlInstructions"/> for the estimate's error.</summary>
        private const int MaxIlEstimate = 14_000;

        /// <summary>Roughly the IL instructions a unit compiles to, its cold paths included.</summary>
        private static int IlEstimate(IlUnit unit) => unit.IsSegment ? unit.Il : 60;

        /// <summary>Roughly the IL instructions of a segment, followed instruction by instruction as the emitter goes.</summary>
        /// <remarks>
        /// A segment keeps the constants it pushes, and the flags its comparisons produce, out of their stack slots until an
        /// instruction needs them there or the segment ends. Each costs a 32-byte write when it is written out, and an
        /// embedded body's hand-back path writes out every one below it again, so a run of pushes that nothing consumes - a
        /// stack filled to its limit, arguments laid out for a call - compiles to far more than one an operation consumes.
        /// </remarks>
        internal sealed class SegmentEstimate
        {
            /// <summary>IL instructions to write one virtual value to its slot: one vector store, or four limb stores without 256-bit vectors.</summary>
            private static readonly int Write = Vector256.IsHardwareAccelerated ? 12 : 26;

            private enum Held : byte { Memory, Constant, Flag }

            /// <summary>What the stack positions hold relative to the segment's start; any not listed are in memory.</summary>
            /// <remarks>A SWAP can move a virtual value below the start, so positions there are tracked too.</remarks>
            private readonly Dictionary<int, Held> _held = [];
            private int _height;
            private int _virtual;
            private int _il = 30;

            /// <summary>The segment so far, with the writes of whatever it still holds virtually at its end.</summary>
            public int Total => _il + _virtual * Write;

            private Held At(int position) => _held.TryGetValue(position, out Held held) ? held : Held.Memory;

            private void Set(int position, Held value)
            {
                Held old = At(position);
                if (value == Held.Memory)
                    _held.Remove(position);
                else
                    _held[position] = value;
                _virtual += (value != Held.Memory ? 1 : 0) - (old != Held.Memory ? 1 : 0);
            }

            private void WriteOut(int position)
            {
                if (At(position) == Held.Memory)
                    return;
                Set(position, Held.Memory);
                _il += Write;
            }

            private int VirtualBelow(int limit)
            {
                int count = 0;
                foreach (int position in _held.Keys)
                {
                    if (position < limit)
                        count++;
                }

                return count;
            }

            /// <summary>Clears what an instruction consumed and produced in memory, from <paramref name="from"/> up.</summary>
            private void Produce(int from, int to)
            {
                for (int position = from; position < Math.Max(to, _height); position++)
                    Set(position, Held.Memory);
                _height = to;
            }

            public void Add(IlInstruction instruction)
            {
                IlOp op = instruction.Op;
                int h = _height;
                switch (op.Kind)
                {
                    case IlKind.Push:
                    case IlKind.ProgramCounter:
                        Set(h, Held.Constant);
                        _height = h + 1;
                        _il += 2;
                        break;
                    case IlKind.Dup:
                    {
                        Held source = At(h - op.Count);
                        Set(h, source);
                        _height = h + 1;
                        _il += source != Held.Memory ? 2 : 9;
                        break;
                    }
                    case IlKind.Swap:
                    {
                        Held a = At(h - 1), b = At(h - 1 - op.Count);
                        if (a != Held.Memory && b != Held.Memory)
                        {
                            _il += 1;
                        }
                        else if (a != Held.Memory || b != Held.Memory)
                        {
                            _il += 9;
                        }
                        else
                        {
                            _il += 18;
                        }

                        Set(h - 1, b);
                        Set(h - 1 - op.Count, a);
                        break;
                    }
                    case IlKind.Pop:
                        Set(h - 1, Held.Memory);
                        _height = h - 1;
                        _il += 2;
                        break;
                    case IlKind.JumpDest:
                        _il += 2;
                        break;
                    case IlKind.Gas:
                        Set(h, Held.Memory);
                        _height = h + 1;
                        _il += 22;
                        break;
                    case IlKind.CheckedBody when op.IsCallDataLoad && At(h - 1) == Held.Constant:
                        Set(h - 1, Held.Memory);
                        _il += 12;
                        break;
                    case IlKind.Native when op.StackInputs == 1:
                    {
                        Held x = At(h - 1);
                        if (x == Held.Constant)
                            break;
                        if (op.IsPredicate)
                        {
                            if (x == Held.Memory)
                                _il += 14;
                            Set(h - 1, Held.Flag);
                            break;
                        }

                        WriteOut(h - 1);
                        _il += 14;
                        break;
                    }
                    case IlKind.Native:
                    {
                        if (At(h - 1) == Held.Flag) WriteOut(h - 1);
                        if (At(h - 2) == Held.Flag) WriteOut(h - 2);
                        bool folds = At(h - 1) == Held.Constant && At(h - 2) == Held.Constant;
                        // A shift by a constant amount is a direct form over the limbs, several times a helper call.
                        bool constantShift = op.Family == IlFamily.Shift && At(h - 1) == Held.Constant;
                        Set(h - 1, Held.Memory);
                        _height = h - 1;
                        if (folds)
                        {
                            Set(h - 2, Held.Constant);
                            break;
                        }

                        Set(h - 2, op.IsPredicate ? Held.Flag : Held.Memory);
                        _il += constantShift ? 72 : op.IsPredicate || IlEvmSettings.DirectArithmetic ? 24 : 14;
                        break;
                    }
                    case IlKind.MLoad:
                        // The fast path, and the body call on its cold path, which writes out a constant offset first.
                        _il += 40 + (At(h - 1) == Held.Constant ? Write : 0) + Write * VirtualBelow(h - 1);
                        Set(h - 1, Held.Memory);
                        break;
                    case IlKind.MStore:
                        _il += 50 + (At(h - 1) == Held.Constant ? Write : 0) + (At(h - 2) == Held.Constant ? Write : 0) + Write * VirtualBelow(h - 2);
                        Produce(h - 2, h - 2);
                        break;
                    case IlKind.EmbeddedBody:
                    {
                        for (int position = h - op.StackInputs; position < h; position++)
                            WriteOut(position);
                        int after = h + op.StackGrowth;
                        // The hand-back path writes out everything still virtual under the body's results.
                        _il += 40 + Write * VirtualBelow(after);
                        Produce(h - op.StackInputs, after);
                        break;
                    }
                    case IlKind.Stop:
                        // The frame ends; nothing virtual is written out.
                        _held.Clear();
                        _virtual = 0;
                        _height = 0;
                        _il += 10;
                        break;
                    case IlKind.Jump:
                    case IlKind.JumpIf:
                    {
                        // A destination known at compile time is a branch; one computed at run time is looked up in the chunk's
                        // table, with its fault, foreign and poll paths.
                        bool constant = At(h - 1) == Held.Constant;
                        Produce(h - op.StackInputs, h - op.StackInputs);
                        _il += (constant ? 30 : 150) + Write * _virtual;
                        _held.Clear();
                        _virtual = 0;
                        _height = 0;
                        break;
                    }
                    default:
                        // A checked body, or an operation without a helper: its operands are written out, its results are in memory.
                        for (int position = h - op.StackInputs; position < h; position++)
                            WriteOut(position);
                        Produce(h - op.StackInputs, h + op.StackGrowth);
                        _il += 20;
                        break;
                }
            }
        }

        /// <summary>Roughly the JIT locals the inlined helpers of <paramref name="instruction"/> add to its chunk.</summary>
        /// <remarks>
        /// A 256-bit operation inlines its helper, the opcode's operation and the integer routines under it, each with
        /// temporaries; memory accesses inline the memory's cost and commit paths. Stack moves, jumps and bodies run
        /// behind a call add none.
        /// </remarks>
        private static int InlineWeight(IlInstruction instruction) => instruction.Op.Kind switch
        {
            IlKind.Native when !IlEvmSettings.InlineArithmetic => 1,
            IlKind.Native => instruction.Op.Family is IlFamily.Bitwise or IlFamily.Math1 ? 4 : 8,
            IlKind.MLoad => IlEvmSettings.InlineMemory ? 8 : 1,
            IlKind.MStore => IlEvmSettings.InlineMemory ? 10 : 1,
            IlKind.CheckedBody => IlEvmSettings.InlineBodies ? 8 : 1,
            IlKind.JumpIf => 2,
            IlKind.Gas or IlKind.ProgramCounter => 1,
            _ => 0,
        };

        private static List<IlInstruction> Decode(ReadOnlySpan<byte> code, IlOp[] ops)
        {
            List<IlInstruction> instructions = new(code.Length / 2);
            Span<byte> word = stackalloc byte[32];
            for (int pc = 0; pc < code.Length;)
            {
                byte opcode = code[pc];
                IlOp op = ops[opcode];
                IlInstruction instruction = new() { Pc = pc, Opcode = opcode, Op = op, Size = 1 };
                if (opcode is >= (byte)Instruction.PUSH1 and <= (byte)Instruction.PUSH32)
                {
                    int count = opcode - (byte)Instruction.PUSH1 + 1;
                    instruction.Size = 1 + count;
                    // Big-endian immediate; bytes past the end of the code read as zero, as from the execution padding.
                    word.Clear();
                    for (int i = 0; i < count; i++)
                    {
                        int source = pc + 1 + i;
                        word[32 - count + i] = source < code.Length ? code[source] : (byte)0;
                    }

                    instruction.U3 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(word);
                    instruction.U2 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(word[8..]);
                    instruction.U1 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(word[16..]);
                    instruction.U0 = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(word[24..]);
                }

                instructions.Add(instruction);
                pc += instruction.Size;
            }

            return instructions;
        }

        /// <summary>Models the untraced EXTCODESIZE body's peephole, which runs a following ISZERO, GT or EQ itself.</summary>
        /// <remarks>
        /// Before ISZERO the body always runs it, charging its gas and counting it, so the pair compiles as one instruction.
        /// Before GT or EQ it does so only when the word under the address is zero, which it then pops; that EXTCODESIZE
        /// runs as a unit of its own over a stack fully in memory and goes on past whichever instruction the body reached.
        /// </remarks>
        private static void FusePeepholes(List<IlInstruction> instructions)
        {
            for (int i = 0; i + 1 < instructions.Count; i++)
            {
                IlInstruction instruction = instructions[i];
                if (instruction.Opcode != (byte)Instruction.EXTCODESIZE || instruction.Op.Kind != IlKind.EmbeddedBody)
                    continue;

                IlInstruction next = instructions[i + 1];
                if (next.Opcode == (byte)Instruction.ISZERO)
                {
                    instruction.Size += next.Size;
                    instructions.RemoveAt(i + 1);
                }
                else if (next.Opcode is (byte)Instruction.GT or (byte)Instruction.EQ)
                {
                    IlOp standalone = instruction.Op.Clone();
                    standalone.Kind = IlKind.Body;
                    instruction.Op = standalone;
                    instruction.MayFuseNext = true;
                    next.EndsSegment = true;
                }
            }
        }

        private static bool IsLinear(IlKind kind) => kind is IlKind.Push or IlKind.Dup or IlKind.Swap or IlKind.Pop or
            IlKind.JumpDest or IlKind.ProgramCounter or IlKind.Gas or IlKind.CheckedBody or IlKind.Native or
            IlKind.MLoad or IlKind.MStore or IlKind.EmbeddedBody;

        private static bool EndsSegment(IlKind kind) => kind is IlKind.Jump or IlKind.JumpIf or IlKind.Stop;

        /// <summary>Cuts the instructions into units, no segment past <paramref name="maxSegmentIl"/> estimated IL instructions.</summary>
        /// <remarks>
        /// A straight run of code longer than that - a stack filled with pushes, an unrolled loop, a verifier's arithmetic -
        /// becomes several segments, each falling into the next, so it spans as many chunks as it needs instead of
        /// compiling as one method the JIT will not optimize.
        /// </remarks>
        private static List<IlUnit> Cut(List<IlInstruction> instructions, int maxSegmentIl)
        {
            List<IlUnit> units = [];
            IlUnit? segment = null;
            SegmentEstimate estimate = new();
            int delta = 0;

            void Close()
            {
                if (segment is null) return;
                segment.EndDelta = delta;
                segment.Il = estimate.Total;
                ulong remaining = segment.Gas;
                foreach (IlInstruction instruction in segment.Instructions)
                {
                    remaining -= instruction.Op.StaticGas;
                    instruction.GasAfter = remaining;
                }

                segment = null;
            }

            foreach (IlInstruction instruction in instructions)
            {
                IlKind kind = instruction.Op.Kind;
                if (IsLinear(kind) || EndsSegment(kind))
                {
                    if (kind == IlKind.JumpDest)
                        Close();

                    if (segment is null)
                    {
                        segment = new IlUnit { StartPc = instruction.Pc, IsSegment = true, IsJumpDest = kind == IlKind.JumpDest };
                        units.Add(segment);
                        estimate = new SegmentEstimate();
                        delta = 0;
                    }

                    IlOp op = instruction.Op;
                    segment.MinHeight = Math.Max(segment.MinHeight, op.StackInputs - delta);
                    if (op.StackGrowth > 0)
                        segment.MaxPeak = Math.Max(segment.MaxPeak, delta + op.StackGrowth);
                    segment.Gas += op.StaticGas;
                    segment.Instructions.Add(instruction);
                    estimate.Add(instruction);
                    delta += op.StackGrowth;

                    if (EndsSegment(kind) || instruction.EndsSegment || estimate.Total >= maxSegmentIl)
                        Close();
                    continue;
                }

                Close();
                IlUnit unit = new() { StartPc = instruction.Pc };
                unit.Instructions.Add(instruction);
                units.Add(unit);
            }

            Close();
            return units;
        }

        /// <summary>An <see cref="ILGenerator"/> that counts what RyuJIT weighs when it decides whether to optimize a method.</summary>
        private sealed class IlWriter(ILGenerator generator)
        {
            /// <summary>IL instructions emitted.</summary>
            public int Instructions { get; private set; }

            /// <summary>Instructions that load, store or take the address of a local or an argument.</summary>
            public int LocalReferences { get; private set; }

            public int Locals { get; private set; }

            /// <summary>Calls to helpers marked for aggressive inlining.</summary>
            public int InlineSites { get; private set; }

            /// <summary>What the emitter is writing, for <see cref="IlEvmDiagnostics"/>; IL instructions are tallied under it.</summary>
            public string Category = "entry";

            public int ILOffset => generator.ILOffset;

            public LocalBuilder DeclareLocal(Type type)
            {
                Locals++;
                return generator.DeclareLocal(type);
            }

            public Label DefineLabel() => generator.DefineLabel();

            public void MarkLabel(Label label) => generator.MarkLabel(label);

            private void Count(OpCode opcode)
            {
                Instructions++;
                if (IlEvmDiagnostics.Enabled)
                    IlEvmDiagnostics.IlByCategory.AddOrUpdate(Category, 1, static (_, v) => v + 1);
                if (opcode.OperandType is OperandType.InlineVar or OperandType.ShortInlineVar ||
                    opcode == OpCodes.Ldloc_0 || opcode == OpCodes.Ldloc_1 || opcode == OpCodes.Ldloc_2 || opcode == OpCodes.Ldloc_3 ||
                    opcode == OpCodes.Stloc_0 || opcode == OpCodes.Stloc_1 || opcode == OpCodes.Stloc_2 || opcode == OpCodes.Stloc_3 ||
                    opcode == OpCodes.Ldarg_0 || opcode == OpCodes.Ldarg_1 || opcode == OpCodes.Ldarg_2 || opcode == OpCodes.Ldarg_3)
                {
                    LocalReferences++;
                }
            }

            public void Emit(OpCode opcode)
            {
                Count(opcode);
                generator.Emit(opcode);
            }

            public void Emit(OpCode opcode, byte arg)
            {
                Count(opcode);
                generator.Emit(opcode, arg);
            }

            public void Emit(OpCode opcode, int arg)
            {
                Count(opcode);
                generator.Emit(opcode, arg);
            }

            public void Emit(OpCode opcode, long arg)
            {
                Count(opcode);
                generator.Emit(opcode, arg);
            }

            public void Emit(OpCode opcode, Label label)
            {
                Count(opcode);
                generator.Emit(opcode, label);
            }

            public void Emit(OpCode opcode, Label[] labels)
            {
                Count(opcode);
                generator.Emit(opcode, labels);
            }

            public void Emit(OpCode opcode, LocalBuilder local)
            {
                Count(opcode);
                generator.Emit(opcode, local);
            }

            public void Emit(OpCode opcode, FieldInfo field)
            {
                Count(opcode);
                generator.Emit(opcode, field);
            }

            public void Emit(OpCode opcode, MethodInfo method)
            {
                Count(opcode);
                if ((method.MethodImplementationFlags & MethodImplAttributes.AggressiveInlining) != 0)
                    InlineSites++;
                generator.Emit(opcode, method);
            }

            public void Emit(OpCode opcode, Type type)
            {
                Count(opcode);
                generator.Emit(opcode, type);
            }

            /// <summary>A managed <c>calli</c> through the function pointer on the evaluation stack.</summary>
            public void EmitCalli(Type returnType, Type[] parameterTypes)
            {
                Count(OpCodes.Calli);
                generator.EmitCalli(OpCodes.Calli, CallingConventions.Standard, returnType, parameterTypes, null);
            }
        }

        /// <summary>The body call sites of a contract the JIT may still inline, shared by its chunks.</summary>
        internal sealed class InlineBudget
        {
            public int Remaining = IlEvmSettings.InlineBodyBudget;
        }

        private sealed class Emitter(IlWriter il, List<IlUnit> units, Dictionary<int, IlUnit> allUnits, int codeLength, List<IlSegmentInfo> segments, List<IlSite> sites, InlineBudget inlineBudget)
        {
            /// <summary>The units this chunk compiles; a jump to any other unit leaves the chunk for that unit's home.</summary>
            private readonly HashSet<IlUnit> _members = [.. units];
            /// <summary>Each unit's label in this chunk; a unit compiled into several chunks has one in each.</summary>
            private readonly Dictionary<IlUnit, Label> _unitLabels = new(units.Count);

            private Label LabelOf(IlUnit unit) => _unitLabels[unit];
            /// <summary>The <see cref="IlChunk"/> the chunk's method is bound to.</summary>
            private const byte ArgChunk = 0;
            private const byte ArgVm = 1;
            private const byte ArgStack = 2;
            private const byte ArgGas = 3;
            private const byte ArgProgramCounter = 4;
            private const byte ArgState = 5;

            private readonly List<Action> _outOfLine = [];

            private void AddOutOfLine(Action block)
            {
                string category = "cold " + il.Category;
                _outOfLine.Add(() =>
                {
                    il.Category = category;
                    block();
                });
            }
            private readonly List<Label> _jumpTargets = [];

            private LocalBuilder _gas = null!;
            private LocalBuilder _head = null!;
            private LocalBuilder _base = null!;
            private LocalBuilder _frame = null!;
            private LocalBuilder _pc = null!;
            private LocalBuilder _ops = null!;
            private LocalBuilder _opsAtEntry = null!;
            private LocalBuilder _poll = null!;
            private LocalBuilder _result = null!;
            private LocalBuilder _target = null!;
            private LocalBuilder _limb = null!;
            private LocalBuilder _word = null!;
            // Address-taken by the cold calls that write through them, so the hot locals they feed are not.
            private LocalBuilder _failGas = null!;
            private LocalBuilder _failOps = null!;
            private LocalBuilder? _memory;
            // The segment whose precondition failed, and the opcode count change of a body that left, for the shared paths.
            private LocalBuilder _failSegment = null!;
            private LocalBuilder _opsAdjust = null!;
            private Label _sharedFail;
            private Label _sharedContinue;
            private Label _leaveSite;
            private bool _usesSharedFail;
            private bool _usesSharedContinue;
            private bool _usesLeaveSite;
            private Label _exit;
            private Label _dynamicJump;
            private int _labels;

            /// <summary>Labels defined, a proxy for the basic blocks the JIT will see.</summary>
            public int Labels => _labels;

            private Label NewLabel()
            {
                _labels++;
                return il.DefineLabel();
            }

            public void Emit()
            {
                _gas = il.DeclareLocal(typeof(ulong));
                _head = il.DeclareLocal(typeof(nint));
                _base = il.DeclareLocal(typeof(byte).MakeByRefType());
                _frame = il.DeclareLocal(typeof(byte).MakeByRefType());
                _pc = il.DeclareLocal(typeof(nint));
                _ops = il.DeclareLocal(typeof(int));
                _opsAtEntry = il.DeclareLocal(typeof(int));
                _poll = il.DeclareLocal(typeof(int));
                _result = il.DeclareLocal(typeof(EvmExceptionType));
                _target = il.DeclareLocal(typeof(int));
                _limb = il.DeclareLocal(typeof(ulong));
                _failGas = il.DeclareLocal(typeof(ulong));
                _failOps = il.DeclareLocal(typeof(int));
                _failSegment = il.DeclareLocal(typeof(int));
                _opsAdjust = il.DeclareLocal(typeof(int));
                foreach (IlUnit unit in units)
                {
                    foreach (IlInstruction instruction in unit.Instructions)
                    {
                        if (instruction.Op.Kind is IlKind.MLoad or IlKind.MStore)
                            _memory ??= il.DeclareLocal(typeof(EvmPooledMemory).MakeByRefType());
                    }
                }
                _word = il.DeclareLocal(typeof(EvmWord));
                _exit = NewLabel();
                _dynamicJump = NewLabel();
                _sharedFail = NewLabel();
                _sharedContinue = NewLabel();
                _leaveSite = NewLabel();

                foreach (IlUnit unit in units)
                {
                    Label label = _unitLabels[unit] = NewLabel();
                    if (unit.IsJumpDest)
                        _jumpTargets.Add(label);
                }

                EmitEntry();
                for (int i = 0; i < units.Count; i++)
                {
                    IlUnit unit = units[i];
                    if (unit.IsSegment)
                        EmitSegment(unit);
                    else
                        EmitSingle(unit);

                    // A unit that falls into one not emitted right after it goes there; running off the end of the code
                    // halts as on an implicit STOP.
                    if (!FallsThrough(unit))
                        continue;
                    IlInstruction last = unit.Instructions[^1];
                    int fallPc = last.Pc + last.Size;
                    if (i + 1 < units.Count && units[i + 1].StartPc == fallPc)
                        continue;
                    il.Category = "fall through";
                    if (allUnits.TryGetValue(fallPc, out IlUnit? fall))
                        GoTo(fall);
                    else
                        ExitWith(EvmExceptionType.None, codeLength, 0, 0);
                }

                // Out-of-line blocks may add more of their own, so the list is walked by index.
                for (int block = 0; block < _outOfLine.Count; block++)
                    _outOfLine[block]();

                il.Category = "shared paths";
                if (_usesSharedFail)
                    EmitSharedFail();
                if (_usesSharedContinue)
                    EmitSharedContinue();
                if (_usesLeaveSite)
                    EmitLeaveSite();
                il.Category = "dynamic jump + exit";
                EmitDynamicJump();
                EmitExit();
            }

            /// <summary>Where a segment whose precondition failed goes: the walk finds the fault, or the interpreter runs the segment.</summary>
            private void EmitSharedFail()
            {
                il.MarkLabel(_sharedFail);
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldloc, _failSegment);
                il.Emit(OpCodes.Ldloc, _gas);
                il.Emit(OpCodes.Ldloc, _head);
                il.Emit(OpCodes.Ldloc, _ops);
                il.Emit(OpCodes.Ldloca, _failGas);
                il.Emit(OpCodes.Ldloca, _failOps);
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Call, FailAtMethod);
                il.Emit(OpCodes.Stloc, _result);
                Label fromStart = NewLabel();
                il.Emit(OpCodes.Ldloc, _result);
                il.Emit(OpCodes.Ldc_I4, (int)IlDeopt);
                il.Emit(OpCodes.Beq, fromStart);
                il.Emit(OpCodes.Ldloc, _failGas);
                il.Emit(OpCodes.Stloc, _gas);
                il.Emit(OpCodes.Ldloc, _failOps);
                il.Emit(OpCodes.Stloc, _ops);
                // fromStart: nothing of the segment ran, so its starting gas and head are the interpreter's.
                il.MarkLabel(fromStart);
                SyncHead(0);
                SyncGas(0);
                il.Emit(OpCodes.Br, _exit);
            }

            /// <summary>Where the chunk leaves for a unit of another chunk, whose offset is in the target local; the head is already moved.</summary>
            private void EmitSharedContinue()
            {
                il.MarkLabel(_sharedContinue);
                SyncHead(0);
                SyncGas(0);
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stind_I);
                il.Emit(OpCodes.Ldc_I4, (int)IlContinue);
                il.Emit(OpCodes.Stloc, _result);
                il.Emit(OpCodes.Br, _exit);
            }

            /// <summary>Where an embedded body that ended the compiled run goes; gas, head and counter are already the frame's.</summary>
            private void EmitLeaveSite()
            {
                il.MarkLabel(_leaveSite);
                il.Emit(OpCodes.Ldloc, _ops);
                il.Emit(OpCodes.Ldloc, _opsAdjust);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, _ops);
                il.Emit(OpCodes.Br, _exit);
            }

            private void EmitEntry()
            {
                Label inRange = NewLabel();
                // pc = programCounter; outside the code the interpreter answers. A local of its own, since the bodies
                // take the address of the counter local.
                LocalBuilder entryPc = il.DeclareLocal(typeof(nint));
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Ldind_I);
                il.Emit(OpCodes.Stloc, entryPc);
                il.Emit(OpCodes.Ldloc, entryPc);
                il.Emit(OpCodes.Ldc_I4, codeLength);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Blt_Un, inRange);
                il.Emit(OpCodes.Ldc_I4, (int)IlDeopt);
                il.Emit(OpCodes.Ret);

                il.MarkLabel(inRange);
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                il.Emit(OpCodes.Ldfld, GasValue);
                il.Emit(OpCodes.Stloc, _gas);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldfld, StackHead);
                il.Emit(OpCodes.Stloc, _head);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldfld, StackBase);
                il.Emit(OpCodes.Stloc, _base);
                if (_memory is not null)
                {
                    il.Emit(OpCodes.Ldarg_S, ArgVm);
                    il.Emit(OpCodes.Call, MemoryOf);
                    il.Emit(OpCodes.Stloc, _memory);
                }
                // The chain's opcode count and poll mark come in with the frame's run and leave with it.
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Stloc, _ops);
                il.Emit(OpCodes.Stloc, _opsAtEntry);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StatePollAt);
                il.Emit(OpCodes.Stloc, _poll);

                Label[] entries = new Label[units.Count];
                for (int i = 0; i < units.Count; i++)
                    entries[i] = LabelOf(units[i]);

                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldfld, EntryIndexField);
                il.Emit(OpCodes.Ldloc, entryPc);
                il.Emit(OpCodes.Ldelem_I4);
                il.Emit(OpCodes.Switch, entries);
                // No unit starts here (the frame resumed inside one): the interpreter takes it from this offset.
                il.Emit(OpCodes.Ldc_I4, (int)IlDeopt);
                il.Emit(OpCodes.Ret);
            }

            /// <summary>An arithmetic, comparison or shift helper, inlined or called as <see cref="IlEvmSettings.InlineArithmetic"/> says.</summary>
            private static MethodInfo Arithmetic(MethodInfo helper) => IlEvmSettings.InlineArithmetic ? helper : IlCalledTwin(helper);

            /// <summary>A memory or calldata helper, inlined or called as <see cref="IlEvmSettings.InlineMemory"/> says.</summary>
            private static MethodInfo Memory(MethodInfo helper) => IlEvmSettings.InlineMemory ? helper : IlCalledTwin(helper);

            private void EmitSegment(IlUnit segment)
            {
                Label deopt = NewLabel();
                il.MarkLabel(LabelOf(segment));
                il.Category = "segment checks";

                bool checks = segment.Gas > 0 || segment.MinHeight > 0 || segment.MaxPeak > 0;
                if (segment.Gas > 0)
                {
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Ldc_I8, (long)segment.Gas);
                    il.Emit(OpCodes.Blt_Un, deopt);
                }

                if (segment.MinHeight > 0 && segment.MaxPeak > 0 && EvmStack.MaxStackSize - 1 - segment.MaxPeak < segment.MinHeight)
                {
                    // No head satisfies both bounds: the segment always faults, which the walk below reproduces.
                    il.Emit(OpCodes.Br, deopt);
                }
                else if (segment.MinHeight > 0 && segment.MaxPeak > 0)
                {
                    // Both bounds in one unsigned compare: head - min must lie in [0, max - min], where max is the highest
                    // head from which every push of the segment still fits below EvmStack.MaxStackSize.
                    il.Emit(OpCodes.Ldloc, _head);
                    il.Emit(OpCodes.Ldc_I4, segment.MinHeight);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Sub);
                    il.Emit(OpCodes.Ldc_I4, EvmStack.MaxStackSize - 1 - segment.MaxPeak - segment.MinHeight);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Bgt_Un, deopt);
                }
                else if (segment.MinHeight > 0)
                {
                    il.Emit(OpCodes.Ldloc, _head);
                    il.Emit(OpCodes.Ldc_I4, segment.MinHeight);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Blt, deopt);
                }
                else if (segment.MaxPeak > 0)
                {
                    // A push needs head + growth below EvmStack.MaxStackSize.
                    il.Emit(OpCodes.Ldloc, _head);
                    il.Emit(OpCodes.Ldc_I4, EvmStack.MaxStackSize - 1 - segment.MaxPeak);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Bgt, deopt);
                }

                if (segment.Gas > 0)
                {
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Ldc_I8, (long)segment.Gas);
                    il.Emit(OpCodes.Sub);
                    il.Emit(OpCodes.Stloc, _gas);
                }

                AddOps(segment.Instructions.Count);

                // frame = base + head * 32
                il.Emit(OpCodes.Ldloc, _base);
                il.Emit(OpCodes.Ldloc, _head);
                il.Emit(OpCodes.Ldc_I4_5);
                il.Emit(OpCodes.Shl);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, _frame);

                if (checks)
                {
                    int segmentId = segments.Count;
                    segments.Add(new IlSegmentInfo(segment.FirstTableIndex, segment.Instructions.Count, segment.StartPc));
                    _usesSharedFail = true;
                    AddOutOfLine(() =>
                    {
                        il.MarkLabel(deopt);
                        il.Emit(OpCodes.Ldc_I4, segmentId);
                        il.Emit(OpCodes.Stloc, _failSegment);
                        il.Emit(OpCodes.Br, _sharedFail);
                    });
                }

                int delta = 0;
                List<IlInstruction> instructions = segment.Instructions;
                for (int i = 0; i < instructions.Count; i++)
                {
                    IlInstruction instruction = instructions[i];
                    IlOp op = instruction.Op;
                    if (IlEvmDiagnostics.Enabled)
                        il.Category = op.Kind is IlKind.Native or IlKind.CheckedBody or IlKind.EmbeddedBody ? $"{op.Kind} {op.Name}" : op.Kind.ToString();
                    switch (op.Kind)
                    {
                        case IlKind.Push:
                            _virtual[delta] = VVal.Constant(instruction.U0, instruction.U1, instruction.U2, instruction.U3);
                            delta++;
                            break;
                        case IlKind.ProgramCounter:
                            _virtual[delta] = VVal.Constant((ulong)instruction.Pc, 0, 0, 0);
                            delta++;
                            break;
                        case IlKind.Dup:
                        {
                            int source = delta - op.Count;
                            if (Virtual(source) is { } value)
                            {
                                _virtual[delta] = value;
                            }
                            else
                            {
                                _virtual.Remove(delta);
                                CopySlot(delta, source);
                            }
                            delta++;
                            break;
                        }
                        case IlKind.Swap:
                        {
                            int a = delta - 1;
                            int b = delta - 1 - op.Count;
                            VVal? va = Virtual(a);
                            VVal? vb = Virtual(b);
                            if (va is not null && vb is not null)
                            {
                                _virtual[a] = vb;
                                _virtual[b] = va;
                            }
                            else if (va is not null)
                            {
                                CopySlot(a, b);
                                _virtual.Remove(a);
                                _virtual[b] = va;
                            }
                            else if (vb is not null)
                            {
                                CopySlot(b, a);
                                _virtual.Remove(b);
                                _virtual[a] = vb;
                            }
                            else
                            {
                                Swap(a, b);
                            }
                            break;
                        }
                        case IlKind.Pop:
                            _virtual.Remove(delta - 1);
                            delta--;
                            break;
                        case IlKind.JumpDest:
                            break;
                        case IlKind.Gas:
                            // GAS reads what the interpreter would hold after it: the segment charged the rest already.
                            _virtual.Remove(delta);
                            StoreSmallWord(delta, () =>
                            {
                                il.Emit(OpCodes.Ldloc, _gas);
                                if (instruction.GasAfter > 0)
                                {
                                    il.Emit(OpCodes.Ldc_I8, (long)instruction.GasAfter);
                                    il.Emit(OpCodes.Add);
                                }
                            });
                            delta++;
                            break;
                        case IlKind.CheckedBody when op.IsCallDataLoad && Virtual(delta - 1) is { Kind: VKind.Const } offset:
                            if (offset.U1 != 0 || offset.U2 != 0 || offset.U3 != 0)
                            {
                                // Past 2^64 the body reads nothing and pushes zero.
                                _virtual[delta - 1] = VVal.Constant(0, 0, 0, 0);
                            }
                            else
                            {
                                _virtual.Remove(delta - 1);
                                il.Emit(OpCodes.Ldarg_S, ArgStack);
                                il.Emit(OpCodes.Ldfld, StackInputData);
                                il.Emit(OpCodes.Ldarg_S, ArgStack);
                                il.Emit(OpCodes.Ldfld, StackInputDataLength);
                                il.Emit(OpCodes.Ldc_I8, (long)offset.U0);
                                Slot(delta - 1);
                                il.Emit(OpCodes.Call, Memory(CallDataLoadAt));
                            }
                            break;
                        case IlKind.CheckedBody:
                            for (int position = delta - op.StackInputs; position < delta; position++)
                                Materialize(position);
                            SyncHead(delta);
                            CallBody(instruction, op);
                            il.Emit(OpCodes.Pop);
                            delta += op.StackGrowth;
                            break;
                        case IlKind.Native when op.Native is null && !(IlEvmSettings.DirectArithmetic &&
                            Virtual(delta - 1) is { Kind: VKind.Const } && Virtual(delta - 2) is null or { Kind: VKind.Const }):
                            // An operation the plugin has no helper for, without the constant amount its direct form needs.
                            for (int position = delta - op.StackInputs; position < delta; position++)
                                Materialize(position);
                            SyncHead(delta);
                            CallBody(instruction, op);
                            il.Emit(OpCodes.Pop);
                            delta += op.StackGrowth;
                            break;
                        case IlKind.Native:
                            EmitNative(op, ref delta);
                            break;
                        case IlKind.MLoad:
                            EmitMLoad(segment, i, op, delta);
                            break;
                        case IlKind.MStore:
                            EmitMStore(segment, i, delta);
                            delta -= 2;
                            break;
                        case IlKind.EmbeddedBody:
                        {
                            for (int position = delta - op.StackInputs; position < delta; position++)
                                Materialize(position);
                            int after = delta + op.StackGrowth;
                            EmbeddedCall(segment, i, delta, after, Snapshot(after));
                            delta = after;
                            break;
                        }
                        case IlKind.Stop:
                            // The frame ends, so nothing still virtual needs writing out.
                            ExitWith(EvmExceptionType.Stop, instruction.Pc + 1, delta, 0);
                            EndSegment();
                            return;
                        case IlKind.Jump:
                            EmitJump(instruction, delta);
                            EndSegment();
                            return;
                        case IlKind.JumpIf:
                            EmitJumpIf(instruction, delta);
                            EndSegment();
                            return;
                        default:
                            throw new InvalidOperationException($"{op.Kind} in a segment");
                    }
                }

                il.Category = "segment end";
                MaterializeBelow(delta);
                EndSegment();
                MoveHead(delta);
            }

            /// <summary>A value the compiled segment knows without it being in its stack slot yet.</summary>
            private enum VKind : byte
            {
                /// <summary>A word known at compile time.</summary>
                Const,
                /// <summary>A comparison result, 0 or 1, held in an int local.</summary>
                Bool,
            }

            /// <summary>An immutable virtual stack value; positions that share one (after DUP) share the object.</summary>
            private sealed class VVal
            {
                public VKind Kind;
                public ulong U0, U1, U2, U3;
                public LocalBuilder? Flag;
                /// <summary>For <see cref="VKind.Bool"/>: the value is <see cref="Flag"/> inverted (ISZERO of it).</summary>
                public bool Negated;

                public static VVal Constant(ulong u0, ulong u1, ulong u2, ulong u3) => new() { Kind = VKind.Const, U0 = u0, U1 = u1, U2 = u2, U3 = u3 };
                public static VVal Boolean(LocalBuilder flag, bool negated) => new() { Kind = VKind.Bool, Flag = flag, Negated = negated };
                public bool IsZero => (U0 | U1 | U2 | U3) == 0;
                public bool FitsOffset => U1 == 0 && U2 == 0 && U3 == 0 && U0 <= uint.MaxValue;
            }

            /// <summary>Stack positions, relative to the segment's starting head, whose values have not been written to their slots.</summary>
            private readonly Dictionary<int, VVal> _virtual = [];
            private readonly Stack<LocalBuilder> _freeFlags = new();
            private readonly List<LocalBuilder> _segmentFlags = [];

            private VVal? Virtual(int position) => _virtual.TryGetValue(position, out VVal? value) ? value : null;

            private LocalBuilder NewFlag()
            {
                LocalBuilder flag = _freeFlags.Count > 0 ? _freeFlags.Pop() : il.DeclareLocal(typeof(int));
                _segmentFlags.Add(flag);
                return flag;
            }

            private void EndSegment()
            {
                _virtual.Clear();
                foreach (LocalBuilder flag in _segmentFlags)
                    _freeFlags.Push(flag);
                _segmentFlags.Clear();
            }

            /// <summary>Writes <paramref name="value"/> to the slot of <paramref name="position"/>.</summary>
            private void EmitStore(int position, VVal value)
            {
                if (value.Kind == VKind.Const)
                {
                    StoreConstant(position, value.U0, value.U1, value.U2, value.U3);
                    return;
                }

                StoreSmallWord(position, () =>
                {
                    il.Emit(OpCodes.Ldloc, value.Flag!);
                    if (value.Negated)
                    {
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Xor);
                    }
                    il.Emit(OpCodes.Conv_U8);
                });
            }

            private void Materialize(int position)
            {
                if (_virtual.Remove(position, out VVal? value))
                    EmitStore(position, value);
            }

            /// <summary>Writes out every virtual value below <paramref name="limit"/>, the positions that stay on the stack.</summary>
            private void MaterializeBelow(int limit)
            {
                foreach (int position in _virtual.Keys.Where(position => position < limit).OrderBy(position => position).ToArray())
                    Materialize(position);
            }

            /// <summary>The virtual values below <paramref name="limit"/>, for an out-of-line path that must write them out.</summary>
            private (int Position, VVal Value)[] Snapshot(int limit) =>
                _virtual.Where(entry => entry.Key < limit).OrderBy(entry => entry.Key).Select(entry => (entry.Key, entry.Value)).ToArray();

            private void EmitLimbs(VVal constant)
            {
                il.Emit(OpCodes.Ldc_I8, (long)constant.U0);
                il.Emit(OpCodes.Ldc_I8, (long)constant.U1);
                il.Emit(OpCodes.Ldc_I8, (long)constant.U2);
                il.Emit(OpCodes.Ldc_I8, (long)constant.U3);
            }

            private void CopySlot(int destination, int source)
            {
                Slot(destination);
                Slot(source);
                il.Emit(OpCodes.Ldc_I4, EvmStack.WordSize);
                il.Emit(OpCodes.Cpblk);
            }

            /// <summary>Runs the opcode's operation at compile time, exactly as the interpreter's body runs it.</summary>
            private static VVal Fold(IlOp op, VVal a, VVal? b)
            {
                switch (op.Family)
                {
                    case IlFamily.Math2:
                    case IlFamily.Shift:
                    {
                        object?[] arguments = [new UInt256(a.U0, a.U1, a.U2, a.U3), new UInt256(b!.U0, b.U1, b.U2, b.U3), null];
                        op.Operation!.Invoke(null, arguments);
                        UInt256 result = (UInt256)arguments[2]!;
                        return VVal.Constant(result.u0, result.u1, result.u2, result.u3);
                    }
                    case IlFamily.Bitwise:
                        return FromWord((EvmWord)op.Operation!.Invoke(null, [Word(a), Word(b!)])!);
                    case IlFamily.Math1:
                        return FromWord((EvmWord)op.Operation!.Invoke(null, [Word(a)])!);
                    default:
                        throw new InvalidOperationException($"{op.Name} does not fold");
                }
            }

            private static EvmWord Word(VVal constant) => Vector256.Create(constant.U0, constant.U1, constant.U2, constant.U3).AsByte();

            private static VVal FromWord(EvmWord word)
            {
                Vector256<ulong> limbs = word.AsUInt64();
                return VVal.Constant(limbs.GetElement(0), limbs.GetElement(1), limbs.GetElement(2), limbs.GetElement(3));
            }

            private void EmitNative(IlOp op, ref int delta)
            {
                if (op.StackInputs == 1)
                {
                    int x = delta - 1;
                    VVal? value = Virtual(x);
                    if (value is { Kind: VKind.Const })
                    {
                        _virtual[x] = Fold(op, value, null);
                        return;
                    }

                    if (op.IsPredicate)
                    {
                        // ISZERO: of a flag, the flag inverted; of a word, a new flag.
                        if (value is { Kind: VKind.Bool })
                        {
                            _virtual[x] = VVal.Boolean(value.Flag!, !value.Negated);
                            return;
                        }

                        LocalBuilder flag = NewFlag();
                        if (IlEvmSettings.DirectArithmetic)
                        {
                            SlotIsZero(x);
                        }
                        else
                        {
                            Slot(x);
                            il.Emit(OpCodes.Call, Arithmetic(op.Predicate!));
                        }

                        il.Emit(OpCodes.Stloc, flag);
                        _virtual[x] = VVal.Boolean(flag, negated: false);
                        return;
                    }

                    Materialize(x);
                    if (IlEvmSettings.DirectArithmetic && Vector256.IsHardwareAccelerated && op.Operation?.DeclaringType == typeof(EvmInstructions.OpNot))
                    {
                        // NOT complements the slot in place: a load, a vector xor and a store, no call.
                        Slot(x);
                        Slot(x);
                        il.Emit(OpCodes.Ldobj, typeof(EvmWord));
                        il.Emit(OpCodes.Call, VectorOnesComplement);
                        il.Emit(OpCodes.Stobj, typeof(EvmWord));
                        return;
                    }

                    Slot(x);
                    il.Emit(OpCodes.Call, Arithmetic(op.Native!));
                    return;
                }

                int top = delta - 1;
                int second = delta - 2;
                VVal? va = Virtual(top);
                VVal? vb = Virtual(second);
                if (va is { Kind: VKind.Bool })
                {
                    Materialize(top);
                    va = null;
                }
                if (vb is { Kind: VKind.Bool })
                {
                    Materialize(second);
                    vb = null;
                }

                delta--;
                _virtual.Remove(top);
                if (va is not null && vb is not null)
                {
                    _virtual[second] = Fold(op, va, vb);
                    return;
                }

                if (op.IsPredicate)
                {
                    LocalBuilder flag = NewFlag();
                    if (IlEvmSettings.DirectArithmetic && TryEmitComparison(op, va, vb, top, second))
                    {
                        // The flag is on the evaluation stack.
                    }
                    else if (va is not null)
                    {
                        Slot(second);
                        EmitLimbs(va);
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Call, Arithmetic(op.PredicateConst!));
                    }
                    else if (vb is not null)
                    {
                        Slot(top);
                        EmitLimbs(vb);
                        il.Emit(OpCodes.Ldc_I4_0);
                        il.Emit(OpCodes.Call, Arithmetic(op.PredicateConst!));
                    }
                    else
                    {
                        Slot(top);
                        il.Emit(OpCodes.Call, Arithmetic(op.Predicate!));
                    }
                    il.Emit(OpCodes.Stloc, flag);
                    _virtual[second] = VVal.Boolean(flag, negated: false);
                    return;
                }

                _virtual.Remove(second);
                if (IlEvmSettings.DirectArithmetic && TryEmitWithConstant(op, va, vb, top, second))
                    return;

                if (va is not null)
                {
                    // The constant was on top; the other operand and the result share the second slot.
                    Slot(second);
                    Slot(second);
                    EmitLimbs(va);
                    il.Emit(OpCodes.Ldc_I4_1);
                    il.Emit(OpCodes.Call, Arithmetic(op.NativeConst!));
                }
                else if (vb is not null)
                {
                    Slot(top);
                    Slot(second);
                    EmitLimbs(vb);
                    il.Emit(OpCodes.Ldc_I4_0);
                    il.Emit(OpCodes.Call, Arithmetic(op.NativeConst!));
                }
                else
                {
                    Slot(top);
                    il.Emit(OpCodes.Call, Arithmetic(op.Native!));
                }
            }

            /// <summary>The operation struct behind a native opcode, which tells the direct forms apart.</summary>
            private static Type? OperationOf(IlOp op) => op.Operation?.DeclaringType;

            /// <summary>
            /// EQ, LT or GT with one constant operand as a flag on the evaluation stack, from the other operand's limbs.
            /// LT and GT take constants below 2^64; EQ any constant.
            /// </summary>
            /// <remarks>The opcode compares its first operand, the top, with its second: a constant on top is the left side.</remarks>
            private bool TryEmitComparison(IlOp op, VVal? va, VVal? vb, int top, int second)
            {
                VVal? constant = va ?? vb;
                if (constant is not { Kind: VKind.Const })
                    return false;
                int x = va is not null ? second : top;
                Type? operation = OperationOf(op);
                bool small = constant.U1 == 0 && constant.U2 == 0 && constant.U3 == 0;
                if (operation == typeof(EvmInstructions.OpBitwiseEq))
                {
                    // x == c: every limb equal.
                    if (small)
                    {
                        EmitHighLimbs(x);
                        il.Emit(OpCodes.Ldc_I8, 0L);
                        il.Emit(OpCodes.Ceq);
                    }
                    else
                    {
                        EmitLimbEquals(x, 3, constant.U3);
                        EmitLimbEquals(x, 2, constant.U2);
                        il.Emit(OpCodes.And);
                        EmitLimbEquals(x, 1, constant.U1);
                        il.Emit(OpCodes.And);
                    }

                    EmitLimbEquals(x, 0, constant.U0);
                    il.Emit(OpCodes.And);
                    return true;
                }

                if (!small)
                    return false;

                // LT: top < second; GT: top > second. With the constant on top the comparison is c op x, else x op c.
                bool xBelowC;
                if (operation == typeof(EvmInstructions.OpLt))
                    xBelowC = va is null;
                else if (operation == typeof(EvmInstructions.OpGt))
                    xBelowC = va is not null;
                else
                    return false;

                if (xBelowC)
                {
                    // x < c: no high limbs, and the low limb below c.
                    EmitHighLimbs(x);
                    il.Emit(OpCodes.Ldc_I8, 0L);
                    il.Emit(OpCodes.Ceq);
                    SlotLimb(x, 0);
                    il.Emit(OpCodes.Ldc_I8, (long)constant.U0);
                    il.Emit(OpCodes.Clt_Un);
                    il.Emit(OpCodes.And);
                }
                else
                {
                    // x > c: any high limb, or the low limb above c.
                    EmitHighLimbs(x);
                    il.Emit(OpCodes.Ldc_I8, 0L);
                    il.Emit(OpCodes.Cgt_Un);
                    SlotLimb(x, 0);
                    il.Emit(OpCodes.Ldc_I8, (long)constant.U0);
                    il.Emit(OpCodes.Cgt_Un);
                    il.Emit(OpCodes.Or);
                }

                return true;
            }

            private void EmitHighLimbs(int slot)
            {
                SlotLimb(slot, 1);
                SlotLimb(slot, 2);
                il.Emit(OpCodes.Or);
                SlotLimb(slot, 3);
                il.Emit(OpCodes.Or);
            }

            private void EmitLimbEquals(int slot, int limb, ulong value)
            {
                SlotLimb(slot, limb);
                il.Emit(OpCodes.Ldc_I8, (long)value);
                il.Emit(OpCodes.Ceq);
            }

            /// <summary>Pushes the address of limb <paramref name="limb"/> of the slot, for a store whose value follows.</summary>
            private void LimbAddress(int slot, int limb)
            {
                il.Emit(OpCodes.Ldloc, _frame);
                int offset = slot * EvmStack.WordSize + limb * sizeof(ulong);
                if (offset != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, offset);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Add);
                }
            }

            /// <summary>
            /// AND and OR with a constant, SHL and SHR by a constant amount, ADD of a constant below 2^64 and SUB of one
            /// from the top, written into the second slot from the other operand's limbs.
            /// </summary>
            private bool TryEmitWithConstant(IlOp op, VVal? va, VVal? vb, int top, int second)
            {
                VVal? constant = va ?? vb;
                if (constant is not { Kind: VKind.Const })
                    return false;
                int x = va is not null ? second : top;
                Type? operation = OperationOf(op);
                ulong[] c = [constant.U0, constant.U1, constant.U2, constant.U3];
                if (operation == typeof(EvmInstructions.OpBitwiseAnd) || operation == typeof(EvmInstructions.OpBitwiseOr))
                {
                    bool and = operation == typeof(EvmInstructions.OpBitwiseAnd);
                    for (int limb = 0; limb < 4; limb++)
                    {
                        ulong keep = and ? ulong.MaxValue : 0;
                        ulong fixedValue = and ? 0 : ulong.MaxValue;
                        if (c[limb] == fixedValue)
                        {
                            StoreLimb(second, limb, fixedValue);
                        }
                        else if (c[limb] == keep)
                        {
                            if (x != second)
                            {
                                LimbAddress(second, limb);
                                SlotLimb(x, limb);
                                il.Emit(OpCodes.Stind_I8);
                            }
                        }
                        else
                        {
                            LimbAddress(second, limb);
                            SlotLimb(x, limb);
                            il.Emit(OpCodes.Ldc_I8, (long)c[limb]);
                            il.Emit(and ? OpCodes.And : OpCodes.Or);
                            il.Emit(OpCodes.Stind_I8);
                        }
                    }

                    return true;
                }

                // The core's shift operations are internal, so they are told apart by name.
                if (operation?.Name is "OpShr" or "OpShl")
                {
                    // Only a constant amount, on top, with the value in the second slot shifted in place.
                    if (va is null)
                        return false;
                    if (constant.U1 != 0 || constant.U2 != 0 || constant.U3 != 0 || constant.U0 >= 256)
                    {
                        StoreConstant(second, 0, 0, 0, 0);
                        return true;
                    }

                    int shift = (int)constant.U0;
                    int q = shift / 64;
                    int r = shift % 64;
                    bool right = operation.Name == "OpShr";
                    // Right shifts read limbs at or above the one written, so go up; left shifts go down.
                    for (int step = 0; step < 4; step++)
                    {
                        int limb = right ? step : 3 - step;
                        int source = right ? limb + q : limb - q;
                        if (source < 0 || source > 3)
                        {
                            StoreLimb(second, limb, 0);
                            continue;
                        }

                        if (q == 0 && r == 0)
                            continue;
                        LimbAddress(second, limb);
                        SlotLimb(second, source);
                        if (r != 0)
                        {
                            il.Emit(OpCodes.Ldc_I4, r);
                            il.Emit(right ? OpCodes.Shr_Un : OpCodes.Shl);
                            int carrySource = right ? source + 1 : source - 1;
                            if (carrySource is >= 0 and <= 3)
                            {
                                SlotLimb(second, carrySource);
                                il.Emit(OpCodes.Ldc_I4, 64 - r);
                                il.Emit(right ? OpCodes.Shl : OpCodes.Shr_Un);
                                il.Emit(OpCodes.Or);
                            }
                        }

                        il.Emit(OpCodes.Stind_I8);
                    }

                    return true;
                }

                bool add = operation == typeof(EvmInstructions.OpAdd);
                if ((add || (operation == typeof(EvmInstructions.OpSub) && vb is not null)) &&
                    constant.U1 == 0 && constant.U2 == 0 && constant.U3 == 0)
                {
                    // x + c or x - c in the low limb, carrying into the rest only when it wraps.
                    if (x != second)
                        CopySlot(second, x);
                    if (constant.U0 == 0)
                        return true;
                    Label wrapped = NewLabel();
                    Label back = NewLabel();
                    LimbAddress(second, 0);
                    SlotLimb(second, 0);
                    il.Emit(OpCodes.Dup);
                    il.Emit(OpCodes.Stloc, _limb);
                    il.Emit(OpCodes.Ldc_I8, (long)constant.U0);
                    il.Emit(add ? OpCodes.Add : OpCodes.Sub);
                    il.Emit(OpCodes.Stind_I8);
                    // Added: wrapped when the new low limb is below c. Subtracted: when the old one was below c.
                    if (add)
                        SlotLimb(second, 0);
                    else
                        il.Emit(OpCodes.Ldloc, _limb);
                    il.Emit(OpCodes.Ldc_I8, (long)constant.U0);
                    il.Emit(OpCodes.Blt_Un, wrapped);
                    il.MarkLabel(back);
                    AddOutOfLine(() =>
                    {
                        il.MarkLabel(wrapped);
                        Slot(second);
                        il.Emit(OpCodes.Call, add ? CarryMethod : BorrowMethod);
                        il.Emit(OpCodes.Br, back);
                    });
                    return true;
                }

                return false;
            }

            private void EmitMLoad(IlUnit segment, int index, IlOp op, int delta)
            {
                int offset = delta - 1;
                VVal? value = Virtual(offset);
                if (value is not null && (value.Kind == VKind.Bool || !value.FitsOffset))
                {
                    Materialize(offset);
                    value = null;
                }

                Label slow = NewLabel();
                Label back = NewLabel();
                il.Emit(OpCodes.Ldloc, _memory!);
                if (value is not null)
                {
                    il.Emit(OpCodes.Ldc_I8, (long)value.U0);
                    Slot(offset);
                    il.Emit(OpCodes.Call, Memory(TryMLoadAt));
                }
                else
                {
                    Slot(offset);
                    il.Emit(OpCodes.Call, Memory(TryMLoad));
                }
                il.Emit(OpCodes.Brfalse, slow);
                il.MarkLabel(back);

                // The loaded word is in the slot either way.
                _virtual.Remove(offset);
                (int Position, VVal Value)[] live = Snapshot(delta);
                VVal? constantOffset = value;
                AddOutOfLine(() =>
                {
                    il.MarkLabel(slow);
                    if (constantOffset is not null)
                        EmitStore(offset, constantOffset);
                    EmbeddedCall(segment, index, delta, delta + op.StackGrowth, live);
                    il.Emit(OpCodes.Br, back);
                });
            }

            private void EmitMStore(IlUnit segment, int index, int delta)
            {
                int offset = delta - 1;
                int word = delta - 2;
                VVal? vo = Virtual(offset);
                VVal? vw = Virtual(word);
                if (vw is { Kind: VKind.Bool })
                {
                    Materialize(word);
                    vw = null;
                }
                if (vo is not null && (vo.Kind == VKind.Bool || !vo.FitsOffset))
                {
                    Materialize(offset);
                    vo = null;
                }
                if (vo is null && vw is not null)
                {
                    Materialize(word);
                    vw = null;
                }

                Label slow = NewLabel();
                Label back = NewLabel();
                il.Emit(OpCodes.Ldloc, _memory!);
                if (vo is not null && vw is not null)
                {
                    // The word in memory byte order: big-endian limbs, most significant first.
                    il.Emit(OpCodes.Ldc_I8, (long)vo.U0);
                    il.Emit(OpCodes.Ldc_I8, (long)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(vw.U3));
                    il.Emit(OpCodes.Ldc_I8, (long)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(vw.U2));
                    il.Emit(OpCodes.Ldc_I8, (long)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(vw.U1));
                    il.Emit(OpCodes.Ldc_I8, (long)System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(vw.U0));
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Call, Memory(TryMStoreWordAt));
                }
                else if (vo is not null)
                {
                    il.Emit(OpCodes.Ldc_I8, (long)vo.U0);
                    Slot(word);
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Call, Memory(TryMStoreAt));
                }
                else
                {
                    Slot(offset);
                    il.Emit(OpCodes.Ldloc, _gas);
                    il.Emit(OpCodes.Call, Memory(TryMStore));
                }
                il.Emit(OpCodes.Stloc, _limb);
                il.Emit(OpCodes.Ldloc, _limb);
                il.Emit(OpCodes.Ldc_I8, -1L);
                il.Emit(OpCodes.Beq, slow);
                il.Emit(OpCodes.Ldloc, _limb);
                il.Emit(OpCodes.Stloc, _gas);
                il.MarkLabel(back);

                _virtual.Remove(offset);
                _virtual.Remove(word);
                (int Position, VVal Value)[] live = Snapshot(delta - 2);
                AddOutOfLine(() =>
                {
                    il.MarkLabel(slow);
                    if (vo is not null)
                        EmitStore(offset, vo);
                    if (vw is not null)
                        EmitStore(word, vw);
                    EmbeddedCall(segment, index, delta, delta - 2, live);
                    il.Emit(OpCodes.Br, back);
                });
            }

            /// <summary>Runs the body of the segment's <paramref name="index"/>th opcode exactly as the dispatch loop would.</summary>
            /// <remarks>
            /// The body sees the interpreter's gas: the segment's charge for this opcode and the ones after it is returned
            /// first, and charged again once the body has run. A body that halts ends the frame. Gas then too short for
            /// the rest of the segment means the interpreter would fault in it, so the fault is found by walking the rest;
            /// where the walk cannot tell, the interpreter takes over after this opcode, with <paramref name="live"/>,
            /// the values still virtual, written out first.
            /// </remarks>
            private void EmbeddedCall(IlUnit segment, int index, int deltaBefore, int deltaAfter, (int Position, VVal Value)[] live)
            {
                IlInstruction instruction = segment.Instructions[index];
                IlOp op = instruction.Op;
                int site = sites.Count;
                sites.Add(new IlSite(instruction.Pc, instruction.Pc + instruction.Size, instruction.GasAfter,
                    segment.FirstTableIndex + index + 1, segment.Instructions.Count - index - 1, op.ExecutePointer,
                    deltaBefore, instruction.GasAfter + op.StaticGas));

                // The head and gas go in as they are; the site's table entry adjusts them, which keeps each site small.
                Label leaves = NewLabel();
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldc_I4, site);
                il.Emit(OpCodes.Ldarg_S, ArgVm);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Ldloca, _opsAdjust);
                il.Emit(OpCodes.Ldloc, _head);
                il.Emit(OpCodes.Ldloc, _gas);
                il.Emit(OpCodes.Call, EmbeddedMethod);
                il.Emit(OpCodes.Stloc, _result);
                il.Emit(OpCodes.Ldloc, _result);
                il.Emit(OpCodes.Brtrue, leaves);
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                il.Emit(OpCodes.Ldfld, GasValue);
                il.Emit(OpCodes.Stloc, _gas);

                _usesLeaveSite = true;
                AddOutOfLine(() =>
                {
                    // The run ends at the body: a hand-over to the interpreter also needs the values still virtual.
                    il.MarkLabel(leaves);
                    if (live.Length > 0)
                    {
                        Label halts = NewLabel();
                        il.Emit(OpCodes.Ldloc, _result);
                        il.Emit(OpCodes.Ldc_I4, (int)IlDeopt);
                        il.Emit(OpCodes.Bne_Un, halts);
                        foreach ((int position, VVal value) in live)
                            EmitStore(position, value);
                        il.MarkLabel(halts);
                    }

                    il.Emit(OpCodes.Br, _leaveSite);
                });
            }

            private void EmitSingle(IlUnit unit)
            {
                il.Category = "single " + unit.Instructions[0].Op.Kind;
                IlInstruction instruction = unit.Instructions[0];
                IlOp op = instruction.Op;
                il.MarkLabel(LabelOf(unit));

                if (op.Kind == IlKind.Unsupported)
                {
                    Handback(instruction.Pc, 0, 0, uncount: 0);
                    return;
                }

                SyncHead(0);
                SyncGas(0);
                CallBody(instruction, op);
                il.Emit(OpCodes.Stloc, _result);
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                il.Emit(OpCodes.Ldfld, GasValue);
                il.Emit(OpCodes.Stloc, _gas);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldfld, StackHead);
                il.Emit(OpCodes.Stloc, _head);
                AddOps(1);

                if (op.Kind == IlKind.TerminatingBody)
                {
                    StoreProgramCounterFromLocal();
                    il.Emit(OpCodes.Br, _exit);
                    return;
                }

                Label halted = NewLabel();
                Label moved = NewLabel();
                il.Emit(OpCodes.Ldloc, _result);
                il.Emit(OpCodes.Brtrue, halted);
                if (instruction.MayFuseNext && allUnits.TryGetValue(instruction.Pc + instruction.Size, out IlUnit? fused))
                {
                    // The body ran the instruction after it: go on past that one.
                    int pastFused = fused.StartPc + fused.Instructions[0].Size;
                    Label notFused = NewLabel();
                    il.Emit(OpCodes.Ldloc, _pc);
                    il.Emit(OpCodes.Ldc_I4, pastFused);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Bne_Un, notFused);
                    if (allUnits.TryGetValue(pastFused, out IlUnit? after))
                        GoTo(after);
                    else
                        ExitWith(EvmExceptionType.None, codeLength, 0, 0);
                    il.MarkLabel(notFused);
                }
                // A body that moved the counter somewhere else than past itself leaves the frame to the interpreter.
                il.Emit(OpCodes.Ldloc, _pc);
                il.Emit(OpCodes.Ldc_I4, instruction.Pc + instruction.Size);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Bne_Un, moved);

                AddOutOfLine(() =>
                {
                    il.MarkLabel(halted);
                    StoreProgramCounterFromLocal();
                    il.Emit(OpCodes.Br, _exit);

                    il.MarkLabel(moved);
                    StoreProgramCounterFromLocal();
                    il.Emit(OpCodes.Ldc_I4, (int)IlDeopt);
                    il.Emit(OpCodes.Stloc, _result);
                    il.Emit(OpCodes.Br, _exit);
                });
            }

            /// <summary>Whether a constant names a JUMPDEST of the code, and which unit starts there.</summary>
            private bool TryTarget(VVal destination, [NotNullWhen(true)] out IlUnit? target)
            {
                target = null;
                return destination.Kind == VKind.Const && destination.U1 == 0 && destination.U2 == 0 && destination.U3 == 0 &&
                    destination.U0 < (ulong)codeLength && allUnits.TryGetValue((int)destination.U0, out target) && target.IsJumpDest;
            }

            private void EmitJump(IlInstruction jump, int delta)
            {
                int destination = delta - 1;
                VVal? vd = Virtual(destination);
                _virtual.Remove(destination);
                if (vd is { Kind: VKind.Const })
                {
                    if (TryTarget(vd, out IlUnit? target))
                    {
                        MaterializeBelow(destination);
                        MoveHead(delta - 1);
                        Poll(landedJumpDest: 1);
                        GoTo(target);
                    }
                    else
                    {
                        ExitWith(EvmExceptionType.InvalidJumpDestination, jump.Pc + 1, delta - 1, 0);
                    }

                    return;
                }

                if (vd is not null)
                    EmitStore(destination, vd);
                MaterializeBelow(destination);
                Label invalid = NewLabel();
                Label foreign = NewLabel();
                LoadTarget(destination, invalid, foreign);
                MoveHead(delta - 1);
                Poll(landedJumpDest: 1);
                il.Emit(OpCodes.Br, _dynamicJump);

                AddOutOfLine(() =>
                {
                    il.MarkLabel(invalid);
                    ExitWith(EvmExceptionType.InvalidJumpDestination, jump.Pc + 1, delta - 1, 0);

                    il.MarkLabel(foreign);
                    MoveHead(delta - 1);
                    Poll(landedJumpDest: 1);
                    ExitContinueAtLimb();
                });
            }

            private void EmitJumpIf(IlInstruction jumpIf, int delta)
            {
                int destination = delta - 1;
                int condition = delta - 2;
                VVal? vd = Virtual(destination);
                VVal? vc = Virtual(condition);
                _virtual.Remove(destination);
                _virtual.Remove(condition);
                if (vd is { Kind: VKind.Bool })
                {
                    EmitStore(destination, vd);
                    vd = null;
                }

                // Everything under the condition stays on the stack on both paths.
                MaterializeBelow(condition);

                if (vc is { Kind: VKind.Const })
                {
                    if (!vc.IsZero)
                        EmitTaken(jumpIf, vd, destination, delta);
                    else
                        MoveHead(delta - 2);
                    return;
                }

                Label notTaken = NewLabel();
                if (vc is not null)
                {
                    il.Emit(OpCodes.Ldloc, vc.Flag!);
                    il.Emit(vc.Negated ? OpCodes.Brtrue : OpCodes.Brfalse, notTaken);
                }
                else
                {
                    SlotIsZero(condition);
                    il.Emit(OpCodes.Brtrue, notTaken);
                }

                EmitTaken(jumpIf, vd, destination, delta);
                il.MarkLabel(notTaken);
                MoveHead(delta - 2);
            }

            /// <summary>The taken side of a JUMPI: to a constant JUMPDEST directly, otherwise through the jump table.</summary>
            private void EmitTaken(IlInstruction jumpIf, VVal? vd, int destination, int delta)
            {
                if (vd is not null)
                {
                    if (TryTarget(vd, out IlUnit? target))
                    {
                        MoveHead(delta - 2);
                        Poll(landedJumpDest: 0);
                        GoTo(target);
                    }
                    else
                    {
                        ExitWith(EvmExceptionType.InvalidJumpDestination, jumpIf.Pc + 1, delta - 2, 0);
                    }

                    return;
                }

                Label invalid = NewLabel();
                Label foreign = NewLabel();
                LoadTarget(destination, invalid, foreign);
                MoveHead(delta - 2);
                Poll(landedJumpDest: 0);
                il.Emit(OpCodes.Br, _dynamicJump);
                AddOutOfLine(() =>
                {
                    il.MarkLabel(invalid);
                    ExitWith(EvmExceptionType.InvalidJumpDestination, jumpIf.Pc + 1, delta - 2, 0);

                    il.MarkLabel(foreign);
                    MoveHead(delta - 2);
                    Poll(landedJumpDest: 0);
                    ExitContinueAtLimb();
                });
            }

            /// <summary>
            /// Loads the jump case of the destination in <paramref name="slot"/> into the target local, or branches to
            /// <paramref name="invalid"/>, or to <paramref name="foreign"/> with the destination in the limb local when
            /// another chunk compiles it.
            /// </summary>
            private void LoadTarget(int slot, Label invalid, Label foreign)
            {
                // Anything above the low limb, or a low limb past the code, is no destination.
                SlotLimb(slot, 1);
                SlotLimb(slot, 2);
                il.Emit(OpCodes.Or);
                SlotLimb(slot, 3);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Brtrue, invalid);
                SlotLimb(slot, 0);
                il.Emit(OpCodes.Stloc, _limb);
                il.Emit(OpCodes.Ldloc, _limb);
                il.Emit(OpCodes.Ldc_I8, (long)codeLength);
                il.Emit(OpCodes.Bge_Un, invalid);
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldfld, JumpOrdinalField);
                il.Emit(OpCodes.Ldloc, _limb);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Ldelem_I4);
                il.Emit(OpCodes.Stloc, _target);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Blt, invalid);
                // The destination's case in this chunk; a JUMPDEST this chunk does not compile, within its table's range or
                // outside it, is continued at in its home.
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldfld, JumpBaseField);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Stloc, _target);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldfld, JumpCasesField);
                il.Emit(OpCodes.Ldlen);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Bge_Un, foreign);
                il.Emit(OpCodes.Ldarg_S, ArgChunk);
                il.Emit(OpCodes.Ldfld, JumpCasesField);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Ldelem_I2);
                il.Emit(OpCodes.Stloc, _target);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Ldc_I4_0);
                il.Emit(OpCodes.Blt, foreign);
            }

            /// <summary><see cref="ExitContinue"/> to the destination held in the limb local.</summary>
            private void ExitContinueAtLimb()
            {
                il.Emit(OpCodes.Ldloc, _limb);
                il.Emit(OpCodes.Conv_I4);
                il.Emit(OpCodes.Stloc, _target);
                il.Emit(OpCodes.Br, _sharedContinue);
                _usesSharedContinue = true;
            }

            private void EmitDynamicJump()
            {
                il.MarkLabel(_dynamicJump);
                il.Emit(OpCodes.Ldloc, _target);
                il.Emit(OpCodes.Switch, _jumpTargets.ToArray());
                // Unreachable: the jump index only names JUMPDEST units.
                il.Emit(OpCodes.Ldc_I4, (int)EvmExceptionType.InvalidJumpDestination);
                il.Emit(OpCodes.Ret);
            }

            private void EmitExit()
            {
                il.MarkLabel(_exit);
                il.Emit(OpCodes.Ldarg_S, ArgVm);
                il.Emit(OpCodes.Ldarg_S, ArgVm);
                if (OpCodeCountField is not null)
                    il.Emit(OpCodes.Ldfld, OpCodeCountField);
                else
                    il.Emit(OpCodes.Call, GetOpCodeCount);
                il.Emit(OpCodes.Ldloc, _ops);
                il.Emit(OpCodes.Ldloc, _opsAtEntry);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Add);
                if (OpCodeCountField is not null)
                    il.Emit(OpCodes.Stfld, OpCodeCountField);
                else
                    il.Emit(OpCodes.Call, SetOpCodeCount);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldloc, _ops);
                il.Emit(OpCodes.Stfld, StateOpCodes);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldloc, _poll);
                il.Emit(OpCodes.Stfld, StatePollAt);
                il.Emit(OpCodes.Ldloc, _result);
                il.Emit(OpCodes.Ret);
            }

            /// <summary>Leaves the chunk to go on at <paramref name="pc"/>, a unit of another chunk; the head is already moved.</summary>
            private void ExitContinue(int pc)
            {
                il.Emit(OpCodes.Ldc_I4, pc);
                il.Emit(OpCodes.Stloc, _target);
                il.Emit(OpCodes.Br, _sharedContinue);
                _usesSharedContinue = true;
            }

            /// <summary>Branches to <paramref name="target"/> in this chunk, or leaves the chunk for it.</summary>
            private void GoTo(IlUnit target)
            {
                if (_members.Contains(target))
                    il.Emit(OpCodes.Br, LabelOf(target));
                else
                    ExitContinue(target.StartPc);
            }

            /// <summary>Hands the frame back to the interpreter at <paramref name="pc"/>.</summary>
            /// <param name="delta">The stack height at <paramref name="pc"/> relative to the head.</param>
            /// <param name="refund">Gas the segment charged for the instruction at <paramref name="pc"/>, which the interpreter charges again.</param>
            /// <param name="uncount">Opcodes counted for the instruction, which the interpreter counts again.</param>
            private void Handback(int pc, int delta, ulong refund, int uncount)
            {
                if (uncount != 0)
                    AddOps(-uncount);
                ExitWith(IlDeopt, pc, delta, refund);
            }

            private void ExitWith(EvmExceptionType result, int pc, int delta, ulong gasBack)
            {
                SyncHead(delta);
                SyncGas(gasBack);
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Ldc_I4, pc);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stind_I);
                il.Emit(OpCodes.Ldc_I4, (int)result);
                il.Emit(OpCodes.Stloc, _result);
                il.Emit(OpCodes.Br, _exit);
            }

            private void StoreProgramCounterFromLocal()
            {
                il.Emit(OpCodes.Ldarg_S, ArgProgramCounter);
                il.Emit(OpCodes.Ldloc, _pc);
                il.Emit(OpCodes.Stind_I);
            }

            private void CallBody(IlInstruction instruction, IlOp op)
            {
                // Bodies see the counter past their opcode byte, as the dispatch loop passes it.
                il.Emit(OpCodes.Ldc_I4, instruction.Pc + 1);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Stloc, _pc);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                if (op.Kind != IlKind.CheckedBody || op.UsesVm)
                    il.Emit(OpCodes.Ldarg_S, ArgVm);
                else
                    il.Emit(OpCodes.Ldnull);
                il.Emit(OpCodes.Ldloca, _pc);
                if (op.Kind is IlKind.CheckedBody or IlKind.Native && IlEvmSettings.InlineBodies && inlineBudget.Remaining > 0)
                {
                    inlineBudget.Remaining--;
                    il.Emit(OpCodes.Call, op.Execute!);
                }
                else
                {
                    // Through the entry point, which the JIT does not inline: a CALL or RETURN body would bring its locals
                    // into the compiled frame, which then saves and zeroes them on every frame entry.
                    il.Emit(OpCodes.Ldc_I8, (long)op.ExecutePointer);
                    il.Emit(OpCodes.Conv_I);
                    il.EmitCalli(typeof(EvmExceptionType), BodySignature);
                }
            }

            /// <summary>Polls for cancellation at a taken jump once the opcode count reaches the poll mark, as the dispatch loop does.</summary>
            /// <param name="landedJumpDest">1 where the dispatch loop has counted the landed JUMPDEST before its check (JUMP), 0 where not (JUMPI).</param>
            private void Poll(int landedJumpDest)
            {
                Label slow = NewLabel();
                Label back = NewLabel();
                il.Emit(OpCodes.Ldloc, _ops);
                if (landedJumpDest != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, landedJumpDest);
                    il.Emit(OpCodes.Add);
                }
                il.Emit(OpCodes.Ldloc, _poll);
                il.Emit(OpCodes.Bge, slow);
                il.MarkLabel(back);

                AddOutOfLine(() =>
                {
                    il.MarkLabel(slow);
                    il.Emit(OpCodes.Ldloc, _ops);
                    il.Emit(OpCodes.Ldc_I4, landedJumpDest + IlPollInterval);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Stloc, _poll);
                    il.Emit(OpCodes.Ldarg_S, ArgVm);
                    il.Emit(OpCodes.Call, PollCancellation);
                    il.Emit(OpCodes.Br, back);
                });
            }

            private void AddOps(int count)
            {
                if (count == 0) return;
                il.Emit(OpCodes.Ldloc, _ops);
                il.Emit(OpCodes.Ldc_I4, count);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, _ops);
            }

            private void MoveHead(int delta)
            {
                if (delta == 0) return;
                il.Emit(OpCodes.Ldloc, _head);
                il.Emit(OpCodes.Ldc_I4, delta);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stloc, _head);
            }

            private void SyncHead(int delta)
            {
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldloc, _head);
                if (delta != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, delta);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Add);
                }
                il.Emit(OpCodes.Stfld, StackHead);
            }

            private void SyncGas(ulong gasBack)
            {
                il.Emit(OpCodes.Ldarg_S, ArgGas);
                il.Emit(OpCodes.Ldloc, _gas);
                if (gasBack != 0)
                {
                    il.Emit(OpCodes.Ldc_I8, (long)gasBack);
                    il.Emit(OpCodes.Add);
                }
                il.Emit(OpCodes.Stfld, GasValue);
            }

            /// <summary>Pushes the address of the slot <paramref name="slot"/> words above the segment's starting head.</summary>
            private void Slot(int slot)
            {
                il.Emit(OpCodes.Ldloc, _frame);
                if (slot != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, slot * EvmStack.WordSize);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Add);
                }
            }

            private void SlotLimb(int slot, int limb)
            {
                il.Emit(OpCodes.Ldloc, _frame);
                int offset = slot * EvmStack.WordSize + limb * sizeof(ulong);
                if (offset != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, offset);
                    il.Emit(OpCodes.Conv_I);
                    il.Emit(OpCodes.Add);
                }
                il.Emit(OpCodes.Ldind_I8);
            }

            private void SlotIsZero(int slot)
            {
                SlotLimb(slot, 0);
                SlotLimb(slot, 1);
                il.Emit(OpCodes.Or);
                SlotLimb(slot, 2);
                il.Emit(OpCodes.Or);
                SlotLimb(slot, 3);
                il.Emit(OpCodes.Or);
                il.Emit(OpCodes.Ldc_I8, 0L);
                il.Emit(OpCodes.Ceq);
            }

            private void StoreLimb(int slot, int limb, ulong value)
            {
                il.Emit(OpCodes.Ldloc, _frame);
                il.Emit(OpCodes.Ldc_I4, slot * EvmStack.WordSize + limb * sizeof(ulong));
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldc_I8, (long)value);
                il.Emit(OpCodes.Stind_I8);
            }

            /// <summary>Writes a constant word to a slot.</summary>
            /// <remarks>
            /// As one 32-byte store of a constant vector, which the JIT loads from its data: a few bytes of code instead of
            /// four 64-bit immediates, and a store the next opcode's full-width load of the slot can be forwarded from.
            /// </remarks>
            private void StoreConstant(int slot, ulong u0, ulong u1, ulong u2, ulong u3)
            {
                if (!Vector256.IsHardwareAccelerated)
                {
                    StoreLimb(slot, 0, u0);
                    StoreLimb(slot, 1, u1);
                    StoreLimb(slot, 2, u2);
                    StoreLimb(slot, 3, u3);
                    return;
                }

                Slot(slot);
                if ((u0 | u1 | u2 | u3) == 0)
                {
                    il.Emit(OpCodes.Initobj, typeof(EvmWord));
                    return;
                }

                il.Emit(OpCodes.Ldc_I8, (long)u0);
                if ((u1 | u2 | u3) == 0)
                {
                    il.Emit(OpCodes.Call, VectorCreateScalar);
                }
                else
                {
                    il.Emit(OpCodes.Ldc_I8, (long)u1);
                    il.Emit(OpCodes.Ldc_I8, (long)u2);
                    il.Emit(OpCodes.Ldc_I8, (long)u3);
                    il.Emit(OpCodes.Call, VectorCreate);
                }
                il.Emit(OpCodes.Call, VectorAsByte);
                il.Emit(OpCodes.Stobj, typeof(EvmWord));
            }

            /// <summary>Writes the 64-bit value on the evaluation stack to a slot as a word, its upper limbs zero, in one store.</summary>
            /// <remarks>The slot's address is pushed first, by the caller, under the value.</remarks>
            private void StoreSmallWord(int slot, Action value)
            {
                if (!Vector256.IsHardwareAccelerated)
                {
                    Slot(slot);
                    value();
                    il.Emit(OpCodes.Stind_I8);
                    StoreLimb(slot, 1, 0);
                    StoreLimb(slot, 2, 0);
                    StoreLimb(slot, 3, 0);
                    return;
                }

                Slot(slot);
                value();
                il.Emit(OpCodes.Call, VectorCreateScalar);
                il.Emit(OpCodes.Call, VectorAsByte);
                il.Emit(OpCodes.Stobj, typeof(EvmWord));
            }

            private void Swap(int a, int b)
            {
                Slot(a);
                il.Emit(OpCodes.Ldobj, typeof(EvmWord));
                il.Emit(OpCodes.Stloc, _word);
                Slot(a);
                Slot(b);
                il.Emit(OpCodes.Ldobj, typeof(EvmWord));
                il.Emit(OpCodes.Stobj, typeof(EvmWord));
                Slot(b);
                il.Emit(OpCodes.Ldloc, _word);
                il.Emit(OpCodes.Stobj, typeof(EvmWord));
            }
        }
    }
}

public static unsafe partial class IlEvm
{
    /// <summary>Which core members the generated code binds by name are missing, or null when all are there.</summary>
    public static string? MissingBindings() => IlCompiler.MissingBindings();
}
