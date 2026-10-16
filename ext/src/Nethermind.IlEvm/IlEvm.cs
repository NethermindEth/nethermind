// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

/// <summary>Which rules the production runner follows, one per process.</summary>
public enum IlMode
{
    /// <summary>The startup list and what live promotion adds, every frame part of them compiled; see <see cref="IlSimple"/>.</summary>
    Simple,
    /// <summary>The shipped list and what live profiling promotes, each frame part admitted on its own; see <see cref="IlRuntime"/>.</summary>
    Adaptive,
    /// <summary>The rules before Adaptive: run-time compiles of hot code, and the shipped contracts on every block frame.</summary>
    Legacy,
    /// <summary>A measurement run (<see cref="IlEvmSettings.Stats"/>): the timed runners, on the Legacy rules.</summary>
    Stats,
    /// <summary>A profiling run (<see cref="IlEvmSettings.Profile"/>): every frame counted, nothing compiled.</summary>
    Profile,
}

/// <summary>Switches for the EVM bytecode-to-IL compiler, set from <see cref="IIlEvmConfig"/> when the plugin loads.</summary>
/// <remarks>
/// In <see cref="IlMode.Legacy"/> a contract is compiled once it has run <see cref="Threshold"/> frames, on a background
/// thread unless <see cref="CompileSynchronously"/> is set; until then, and for any frame the compiled code hands back,
/// the interpreter runs it. The environment variables give defaults for tests and the replay harness.
/// </remarks>
public static class IlEvmSettings
{
    /// <summary>The kill switch: while off, the runner declines every frame.</summary>
    public static bool Enabled { get; set; } =
        RuntimeFeature.IsDynamicCodeSupported && Environment.GetEnvironmentVariable("NETHERMIND_ILEVM") != "0";

    /// <summary>Which rules the production runner follows; <see cref="IlEvmModule"/> sets it from the config.</summary>
    /// <remarks>
    /// The default, for tests and the replay harness, is <c>NETHERMIND_ILEVM_MODE</c> (Simple, Adaptive or Legacy), else
    /// Adaptive; Legacy whenever <see cref="IlEvmDiagnostics"/> are on, whose counters only the Legacy path keeps.
    /// </remarks>
    public static IlMode Mode { get; set; } = IlEvmDiagnostics.Enabled ? IlMode.Legacy :
        Enum.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MODE"), ignoreCase: true, out IlMode mode) && mode is IlMode.Legacy or IlMode.Simple ? mode : IlMode.Adaptive;

    /// <summary>Which frames feed live profiling, as the config set it; for the exit line and the live profile.</summary>
    public static IlEvmPrefer Prefer { get; set; } = IlEvmPrefer.Rpc;

    /// <summary>Where compiled code runs, as <see cref="IlEvmModule"/> attached the runners: the config's, or All where <see cref="IlEvmPrefer.Blocks"/> needs it.</summary>
    public static IlEvmScope Scope { get; set; } = IlEvmScope.Rpc;

    /// <summary>Whether <see cref="IlEvmPrefer.Blocks"/> overrode a configured Scope of Rpc, which the startup step warns of.</summary>
    public static bool ScopeForcedToAll { get; set; }

    /// <summary>Adaptive: whether the visit gate admits each part of an admitted contract on its own, rather than every part compiled.</summary>
    public static bool Gate { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_GATE") != "0";

    /// <summary>Adaptive: whether the read-only queries' runner times compiled code against the interpreter, and takes it away where it loses.</summary>
    public static bool QueryCheck { get; set; } = true;

    /// <summary>Adaptive: as <see cref="QueryCheck"/>, for block processing's runner.</summary>
    public static bool BlockCheck { get; set; } = true;

    /// <summary>Adaptive diagnostics: block processing's runner declines every frame, so a run measures the hook alone.</summary>
    public static bool BlockDeclineAll { get; set; }

    /// <summary>The opcodes a contract runs on the interpreter, over all its frames, after which it is compiled.</summary>
    /// <remarks>
    /// Catches a contract whose few frames run long loops, which <see cref="Threshold"/> alone would miss. A long, as
    /// the counter it is compared with: an int counter wrapped past 2^31, so a threshold near it was met by the
    /// busiest code first rather than never.
    /// </remarks>
    public static long OpcodeThreshold { get; set; } =
        long.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_OPCODES"), out long opcodes) && opcodes > 0 ? opcodes : 20_000;

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

    /// <summary>
    /// Keep a segment's words in vector registers where they can be, on AVX2 hardware with <c>NETHERMIND_ILEVM_VECTOR=1</c> (off by default until the runner measures them again; their 154 runner corpus mismatches were an MSTORE body swapping an aliased slot, fixed):
    /// bitwise operations, comparisons, CALLDATALOAD, NOT, ISZERO, and shifts, small additions and subtractions and
    /// power-of-two multiplications, divisions and remainders by a constant compile to 256-bit vector code whose results
    /// stay in locals; DUP and SWAP move nothing, and words reach their stack slots in one 32-byte store where the
    /// interpreter, a helper or a body needs them there.
    /// </summary>
    /// <remarks>
    /// A word written in parts - one limb of an addition, four limbs of a mask - and read whole by the next opcode cannot be
    /// forwarded from the store buffer: the read waits for the stores to reach the cache, many times the operation's cost.
    /// MLOAD and MSTORE stay helper calls: in place below the initialized limit they measured slower, and 45% more code.
    /// </remarks>
    public static bool VectorForms { get; set; } =
        System.Runtime.Intrinsics.X86.Avx2.IsSupported && Vector256.IsHardwareAccelerated && Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_VECTOR") == "1";

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
    /// <remarks>Under the Adaptive rules each chunk is still a step of its own between blocks, queued once the layout is published, never one long step.</remarks>
    public static bool EagerChunks { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_EAGER_CHUNKS") == "1";

    /// <summary>Interpreted opcodes per second a contract must also run before it is compiled; 0 for no rate condition.</summary>
    /// <remarks>
    /// Compiled code pays off only while it stays in the CPU caches: a contract that runs rarely meets its code cold
    /// on every frame, where the interpreter's few hot handlers win. Applies with a <see cref="Threshold"/> above 1.
    /// </remarks>
    public static int HotOpcodesPerSecond { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_HOT_RATE"), out int hotRate) && hotRate >= 0 ? hotRate : 50_000;

    /// <summary>Legacy only: compile on the thread that crosses the threshold instead of in the background.</summary>
    /// <remarks>The Adaptive rules ignore it: they JIT only between blocks, on the scheduler's workers, never on a frame's thread.</remarks>
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

    /// <summary>For this long after the process starts, a contract the self-check finds slower is re-checked after <see cref="SelfCheckEarlyRecheckMs"/> and never released.</summary>
    /// <remarks>
    /// Early in a process the runtime is still tiering up the node's code and the helpers compiled code calls, and a burst
    /// of first calls competes for the CPU, so a verdict then mostly measures start-up. Measured on a cold RPC node, most
    /// contracts lost their first check this way and ran on the interpreter for the next 30 seconds.
    /// </remarks>
    public static int SelfCheckWarmUpMs { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_WARMUP_MS"), out int warmUpMs) && warmUpMs >= 0 ? warmUpMs : 120_000;

    public static int SelfCheckEarlyRecheckMs { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_EARLY_RECHECK_MS"), out int earlyRecheckMs) && earlyRecheckMs >= 0 ? earlyRecheckMs : 5_000;

    /// <summary>When the process started, in <see cref="Environment.TickCount64"/> milliseconds, for <see cref="SelfCheckWarmUpMs"/>.</summary>
    public static readonly long ProcessStartedAt = StartedAt();

    private static long StartedAt()
    {
        try
        {
            using System.Diagnostics.Process self = System.Diagnostics.Process.GetCurrentProcess();
            return Environment.TickCount64 - (long)(DateTime.Now - self.StartTime).TotalMilliseconds;
        }
        catch (Exception)
        {
            // No start time on this platform: the window counts from the first use of the settings, which is plugin load.
            return Environment.TickCount64;
        }
    }

    /// <summary>When a runner first saw a frame, in <see cref="Environment.TickCount64"/> milliseconds; 0 until then.</summary>
    private static long _firstFrameAt;

    /// <summary>Marks the first frame, from which the start-up window runs; a node may take minutes to start serving.</summary>
    internal static void SawFrame()
    {
        if (Volatile.Read(ref _firstFrameAt) == 0)
            Interlocked.CompareExchange(ref _firstFrameAt, Environment.TickCount64, 0);
    }

    /// <summary>When a runner first saw a frame, in <see cref="Environment.TickCount64"/> milliseconds; 0 until then.</summary>
    internal static long FirstFrameAt => Volatile.Read(ref _firstFrameAt);

    /// <summary>Whether the process is still in its start-up window, for the self-check: <see cref="SelfCheckWarmUpMs"/> from the first frame.</summary>
    internal static bool InWarmUp => InWarmUpAt(Environment.TickCount64);

    /// <summary>Whether the start-up window lasts at <paramref name="now"/>, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    internal static bool InWarmUpAt(long now)
    {
        long first = Volatile.Read(ref _firstFrameAt);
        return first == 0 || now - first < SelfCheckWarmUpMs;
    }

    /// <summary>
    /// Count every contract's frames and interpreted opcodes and compile nothing; <see cref="IlEvmProfile"/> logs the
    /// busiest at shutdown.
    /// </summary>
    public static bool Profile { get; set; }

    /// <summary>
    /// Time every contract's frame parts on the path each took - the interpreter, compiled code, or compiled code that
    /// handed back - and run no self-check; <see cref="IlEvmStats"/> logs the per-contract totals at shutdown.
    /// </summary>
    /// <remarks>
    /// For measurement runs: the module attaches <see cref="IlEvmTimedFrameRunner"/> instead of the production runner,
    /// and block processing runs compiled only the contracts the startup compile shipped. The counts take memory for
    /// every contract each thread ran, kept until shutdown.
    /// </remarks>
    public static bool Stats { get; set; }

    /// <summary>Whether block processing (Scope=All) compiles contracts outside the shipped list once they get hot.</summary>
    /// <remarks>Off, a block frame runs compiled only when its code was precompiled, and otherwise goes to the interpreter with no bookkeeping.</remarks>
    public static bool BlockRuntimeCompile { get; set; }

    /// <summary>Whether block processing (Scope=All) runs the self-check on compiled contracts.</summary>
    public static bool BlockSelfCheck { get; set; }

    /// <summary>On Linux, run the background compiler under SCHED_IDLE, so it takes only CPU time nothing else wants. Off unless <c>NETHERMIND_ILEVM_COMPILE_IDLE=1</c>.</summary>
    /// <remarks>
    /// Linux ignores lowered .NET thread priorities for unprivileged processes; an idle-class thread is the supported way
    /// down. Off by default: threads a thread creates inherit its scheduling class, and the runtime starts its tiering
    /// worker on whichever thread first needs it, so an idle-class compiler could leave the whole process's tier-up idle.
    /// </remarks>
    public static bool CompileIdle { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_COMPILE_IDLE") == "1";

    /// <summary>Of the frames of code that is not compiled, one in this many counts toward hotness, with this weight, so threads running the same code do not all write its counters.</summary>
    public static int HotnessSampling { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_HOTNESS_SAMPLING"), out int sampling) && sampling >= 1 && (sampling & (sampling - 1)) == 0 ? sampling : 8;

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

    /// <summary>The background compiler's thread priority; Linux ignores anything but normal for unprivileged threads.</summary>
    public static ThreadPriority CompilePriority { get; set; } =
        Enum.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_COMPILE_PRIORITY"), out ThreadPriority priority) && Enum.IsDefined(priority) ? priority : ThreadPriority.BelowNormal;

    /// <summary>Compiles waiting for the background compiler, and the <see cref="System.Diagnostics.Stopwatch"/> ticks it has spent compiling, for diagnostics.</summary>
    public static int PendingCompiles => IlEvm.IlCompileQueue.Pending;
    public static long CompileBusyTicks => Interlocked.Read(ref IlEvm.IlCompileQueue.BusyTicks);

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

    /// <summary>
    /// Have compiled code store the id of what it runs next - an opcode, a segment's checks, a shared path - in
    /// <see cref="Marker"/> as it goes, with <c>NETHERMIND_ILEVM_MARKERS=1</c>, for a sampling profiler that reads it
    /// with each sample. The runtime keeps no IL-to-native map of dynamic methods, so this is how samples in compiled
    /// code, and in the helpers and bodies it calls, are told apart by opcode.
    /// </summary>
    /// <remarks>Each marker costs a store; unlike the other diagnostics it leaves the rules as they are set.</remarks>
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
        /// <summary><see cref="OpCodes"/> when the machine's opcode count last took them in, at a chunk's exit.</summary>
        public int Counted;
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
        /// <summary>The body set the contract was compiled for, <see cref="IlFork.Bodies"/>: a frame of another runs on the interpreter, a check that needs no fork lookup.</summary>
        public readonly OpcodeBodySet Bodies = fork.Bodies;
        /// <summary>The code compiled, to verify a content-hash hit byte for byte.</summary>
        public readonly ReadOnlyMemory<byte> Code = code;
        /// <summary>Adaptive: the profile of the code, which holds this contract as shipped or live; null for a contract the Legacy rules compiled.</summary>
        public IlCodeProfile? Profile;
        /// <summary>
        /// Adaptive: which kinds of thread may run the contract, <see cref="IlRuntime.RunMain"/>, <see cref="IlRuntime.RunWorkers"/>
        /// and <see cref="IlRuntime.RunQuery"/>; a check that finds compiled code slower clears its runner's bits.
        /// </summary>
        /// <remarks>Written racily, a byte at a time: a frame that reads a stale mask runs one part either way.</remarks>
        public byte RunMask;
        /// <summary>Adaptive: whether the contract was shipped or promoted by live profiling, and by which source.</summary>
        public IlOrigin Origin;
        /// <summary>Adaptive: when a code state first took the contract, in <see cref="Environment.TickCount64"/> milliseconds; 0 until then.</summary>
        public long AttachedAt;
        /// <summary>Adaptive: the switchback checks of block processing's runner and of the read-only queries', made at publishing for each whose check is on; null for one whose is off.</summary>
        public IlCheck? BlockCheck, QueryCheck;
        /// <summary>Adaptive: the IL and tables live profiling's budget counts for a contract it promoted, while it counts them; 0 for any other, and once released.</summary>
        public long BudgetBytes;
        /// <summary>
        /// Adaptive: a copy of the contract laid out against the ahead twins of the helpers it calls, optimized from their
        /// first call (<see cref="PrecompileAhead"/>), which the queries' runner runs in its place until the runtime has
        /// had time to optimize the helpers on calls; null for none, and once the copy is dropped (<see cref="IlWarmUp.SwitchFromAhead"/>).
        /// </summary>
        /// <remarks>Only for a shipped contract the list vouches for on every visit (<c>qminv=0</c>), compiled whole at startup.</remarks>
        public volatile IlContract? Ahead;
        /// <summary>Per code offset, the chunk of the compiled unit that starts there, or -1; empty once an Adaptive contract is released, so its frames hand back at once.</summary>
        public int[] EntryChunk = entryChunk;
        /// <summary>Per code offset, the index of the compiled unit that starts there within its chunk.</summary>
        public int[] EntryIndex = entryIndex;
        /// <summary>Per code offset, the ordinal of the JUMPDEST there among the code's, or -1 where a jump may not land.</summary>
        public int[] JumpOrdinal = jumpOrdinal;
        /// <summary>Per chunk and JUMPDEST ordinal less the chunk's base, the jump case of that JUMPDEST in the chunk, or -1 where the chunk does not compile it.</summary>
        public short[][] JumpCases = jumpCases;
        /// <summary>Per chunk, the ordinal its jump-case table starts at.</summary>
        public int[] JumpBases = jumpBases;
        /// <summary>The bytes of the contract's tables, which the memory budgets count with its IL.</summary>
        public long TableBytes;
        /// <summary>Each chunk's runner once the JIT has compiled it; null while frames that reach the chunk are left to the interpreter.</summary>
        public readonly IlRunner?[] Runners = new IlRunner?[chunks];
        /// <summary>Each chunk's emitted method, bound to its <see cref="IlChunk"/> but not JIT-compiled until frames reach it.</summary>
        public readonly IlRunner[] Prepared = new IlRunner[chunks];
        /// <summary>Each chunk's IL in bytes, which the inter-block scheduler estimates its JIT by.</summary>
        public readonly int[] ChunkIlBytes = new int[chunks];
        /// <summary>The methods each chunk calls, compiled ahead with it under the Adaptive rules (<see cref="IlWarmUp"/>); null once they are, or with nothing to compile.</summary>
        private MethodBase[]?[]? _callees = new MethodBase[]?[chunks];

        /// <summary>Records the methods <paramref name="chunk"/> calls, as its emitter found them.</summary>
        internal void SetCallees(int chunk, HashSet<MethodBase> callees)
        {
            if (_callees is { } all)
                all[chunk] = [.. callees];
        }

        /// <summary>The methods <paramref name="chunk"/> calls that are not compiled ahead yet; for tests.</summary>
        internal MethodBase[]? CalleesOf(int chunk) => _callees?[chunk];
        private readonly int[] _entries = new int[chunks];
        private readonly int[] _states = new int[chunks];
        /// <summary>The prefix of the compiled methods' names.</summary>
        public readonly string Name = name;
        public int ChunkCount => Prepared.Length;
        /// <summary>Why the last chunk that failed to compile did, for diagnostics.</summary>
        public string? ChunkFailure;
        public int CompiledChunks;
        /// <summary>Per segment instruction, its static gas, stack inputs and stack growth, and whether its walk must hand over; empty once an Adaptive contract is released.</summary>
        private SegmentTables _segments = new(costs, inputs, growths, dynamic, opaque);

        /// <summary>The tables a failed segment's walk reads, replaced whole, so a walk reads one set.</summary>
        private sealed class SegmentTables(ulong[] costs, int[] inputs, int[] growths, bool[] dynamic, bool[] opaque)
        {
            public static readonly SegmentTables Empty = new([], [], [], [], []);
            public readonly ulong[] Costs = costs;
            public readonly int[] Inputs = inputs;
            public readonly int[] Growths = growths;
            public readonly bool[] Dynamic = dynamic;
            public readonly bool[] Opaque = opaque;
        }
        /// <summary>The size of the compiled methods, and how long the JIT took over them, for diagnostics.</summary>
        public int IlBytes;
        public TimeSpan JitTime;
        /// <summary>Whether compiled code has proved faster than the interpreter on this contract's frames.</summary>
        public readonly IlSelfCheck Check = new();
        /// <summary>Chunks queued or compiled so far: a frame during which this moved waited on the JIT, and its time says nothing of the code.</summary>
        public int ChunkEvents;
        /// <summary>When the contract was laid out, in <see cref="Environment.TickCount64"/> milliseconds; its self-check waits <see cref="IlEvmSettings.SelfCheckDelayMs"/> from then.</summary>
        /// <remarks>A shipped contract is laid out at startup, long before it runs: its clock restarts on its first frame.</remarks>
        public long CompiledAt = Environment.TickCount64;
        /// <summary>Set once a precompiled contract first meets a frame.</summary>
        public int Attached;
        /// <summary>Whether <see cref="IlBytes"/> counts toward <see cref="IlEvmSettings.LiveIlBytes"/>; cleared once by release or collection.</summary>
        private int _live;

        private volatile bool _released;

        /// <summary>Whether the contract's code was released, so its frames run on the interpreter.</summary>
        public bool IsReleased => _released;

        /// <summary>What the memory budgets count for the contract: its IL and its tables.</summary>
        public long MemoryBytes => IlBytes + TableBytes;

        /// <summary>Counts the contract's IL as alive, once it is laid out.</summary>
        public void MarkLive()
        {
            if (Interlocked.Exchange(ref _live, 1) == 0)
                IlEvmSettings.AddLiveIl(MemoryBytes);
        }

        /// <summary>Drops the compiled code, for a contract the interpreter runs from now on; its frames are left to the interpreter.</summary>
        /// <remarks>
        /// An Adaptive contract, or one released with <paramref name="dropTables"/> as the Simple rules' evictions do, also
        /// drops its tables: code states and threads' visits may hold it until they next resolve or reuse their ways, and
        /// the memory the budget took it back for should be free by then. Its frames then hand back on entry; one still
        /// running a chunk reads tables of its own, and a walk of a failed segment hands over.
        /// </remarks>
        public void Release() => Release(dropTables: false);

        /// <inheritdoc cref="Release()"/>
        public void Release(bool dropTables)
        {
            lock (Prepared)
            {
                _released = true;
                for (int chunk = 0; chunk < Prepared.Length; chunk++)
                {
                    Volatile.Write(ref _states[chunk], ChunkFailed);
                    Volatile.Write(ref Runners[chunk], null);
                    Prepared[chunk] = null!;
                }

                if (Profile is not null || dropTables)
                {
                    Volatile.Write(ref EntryChunk, []);
                    EntryIndex = JumpOrdinal = JumpBases = [];
                    JumpCases = [];
                    Volatile.Write(ref _segments, SegmentTables.Empty);
                    _callees = null;
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
            // Read once: a release replaces it with an empty table.
            int[] entryChunk = EntryChunk;
            if ((nuint)pc >= (nuint)entryChunk.Length || entryChunk[pc] < 0)
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
                entryChunk = EntryChunk;
                int chunk = (nuint)pc < (nuint)entryChunk.Length ? entryChunk[pc] : -1;
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
            // The Adaptive rules JIT only between blocks, whatever the knob says: never on a frame's thread.
            IlMode mode = IlEvmSettings.Mode;
            if (IlEvmSettings.CompileSynchronously && mode is not (IlMode.Adaptive or IlMode.Simple))
                return CompileChunk(chunk);
            HitColdChunk = true;
            // The Simple rules compile every contract whole, off the frames' threads: a chunk not compiled failed its
            // JIT, or a test left it cold, and its frames hand back.
            if (mode == IlMode.Simple)
                return null;
            // Queued already: frames wait for the JIT without writing the shared counter again.
            if (Volatile.Read(ref _states[chunk]) != ChunkCold)
                return null;
            if (Interlocked.Increment(ref _entries[chunk]) >= IlEvmSettings.ChunkThreshold &&
                Interlocked.CompareExchange(ref _states[chunk], ChunkQueued, ChunkCold) == ChunkCold)
            {
                Interlocked.Increment(ref ChunkEvents);
                // Under the Adaptive rules the windows between blocks JIT it, as every compile; shipped contracts are
                // compiled whole, so only one laid out with chunks left cold, as a live one is, gets here. The Legacy
                // rules' background compiler takes it whenever it gets to it, blocks or not. A queue that turns it away
                // leaves it to frames to ask again.
                bool queued = IlEvmSettings.Mode == IlMode.Adaptive
                    ? IlScheduler.TryEnqueue(new ChunkJob(this, chunk))
                    : IlCompileQueue.Enqueue(() => CompileChunk(chunk));
                if (!queued)
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
            IlRunner runner;
            lock (Prepared)
            {
                if (Runners[chunk] is { } compiled)
                    return compiled;
                if (_states[chunk] == ChunkFailed || Prepared[chunk] is null)
                    return null;
                runner = Prepared[chunk];
            }

            // The JIT runs outside the lock, which a release (the self-check giving the code back) takes: a frame's
            // thread must never wait on a JIT the compiler thread is running.
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                // An offset past the code returns at once: the call only has the JIT compile the method.
                EvmStack probeStack = default;
                EthereumGasPolicy probeGas = default;
                nint probePc = -1;
                IlRunState probeState = default;
                runner(null!, ref probeStack, ref probeGas, ref probePc, ref probeState);
            }
            catch (Exception exception)
            {
                lock (Prepared)
                {
                    ChunkFailure = exception.ToString();
                    Volatile.Write(ref _states[chunk], ChunkFailed);
                }

                return null;
            }

            TimeSpan jit = System.Diagnostics.Stopwatch.GetElapsedTime(started);
            // Under the Adaptive rules what the chunk calls is compiled here too, off the frames' threads; the other rules
            // leave it to the first frames, as they did.
            if (IlEvmSettings.Mode == IlMode.Adaptive && Volatile.Read(ref _callees) is { } allCallees && allCallees[chunk] is { } callees)
            {
                IlWarmUp.Prepare(callees);
                allCallees[chunk] = null;
            }

            lock (Prepared)
            {
                if (Runners[chunk] is { } raced)
                    return raced;
                // Released while the JIT ran: the code is gone, and the chunk stays with the interpreter.
                if (_states[chunk] == ChunkFailed || !ReferenceEquals(Prepared[chunk], runner))
                    return null;
                JitTime += jit;
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
            // Read once: a release replaces the tables with empty ones, and a walk of a released contract hands over at the
            // segment's start, where the interpreter faults the same way.
            SegmentTables tables = Volatile.Read(ref _segments);
            if ((uint)(first + count) > (uint)tables.Costs.Length)
                return IlDeopt;
            for (int i = first; i < first + count; i++)
            {
                if (tables.Opaque[i]) return IlDeopt;
                opCodes++;
                ulong cost = tables.Costs[i];
                if (gas < cost) return EvmExceptionType.OutOfGas;
                gas -= cost;
                if (head < tables.Inputs[i]) return EvmExceptionType.StackUnderflow;
                int growth = tables.Growths[i];
                if (growth > 0 && head + growth >= EvmStack.MaxStackSize) return EvmExceptionType.StackOverflow;
                if (tables.Dynamic[i]) return IlDeopt;
                head += growth;
            }

            throw new InvalidOperationException("A segment failed its precondition with every opcode fitting.");
        }

        /// <summary>Whether a release dropped the contract's tables; for tests.</summary>
        internal bool TablesReleased => EntryChunk.Length == 0 && Volatile.Read(ref _segments) == SegmentTables.Empty;
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
    /// <remarks>
    /// The Adaptive fields, which <see cref="IlRuntime"/> writes when it resolves the code and every part a runner is
    /// offered reads, come first, laid out by hand next to the object's header: a part of code a state holds reads one
    /// cache line of it, the one its type check reads, rather than the three the runtime's own layout spread them over.
    /// The Legacy rules' hotness and lookup fields follow.
    /// </remarks>
    [StructLayout(LayoutKind.Explicit)]
    internal sealed class IlCodeState
    {
        /// <summary>Adaptive: the <see cref="IlRuntime.Version"/> the runs below were resolved at; a state of another version resolves them again.</summary>
        [FieldOffset(0)] public int Version;
        /// <summary>Adaptive: spreads the states over each thread's visit table (<see cref="IlThread"/>), read beside the fields here rather than hashing the object.</summary>
        [FieldOffset(4)] public readonly uint GateHash = IlThread.NextStateHash();
        /// <summary>Adaptive: what block processing's runner runs the code on, or null for the interpreter; compiled for one body set, which each part checks.</summary>
        [FieldOffset(8)] public volatile IlContract? BlockRun;
        /// <summary>Adaptive: what the read-only queries' runner runs the code on, or null for the interpreter.</summary>
        [FieldOffset(16)] public volatile IlContract? QueryRun;
        /// <summary>Adaptive: the code's profile, or null when it has none, as for code no runner counts.</summary>
        [FieldOffset(24)] public IlCodeProfile? Profile;
        /// <summary>
        /// Adaptive: the body set a part of block processing's runner, and of the queries' runner, last found no contract
        /// for (<see cref="IlRuntime.ForkMiss"/>): parts of it go to the interpreter without looking again, until the state
        /// resolves again at a new version.
        /// </summary>
        [FieldOffset(32)] public OpcodeBodySet? BlockMissed;
        [FieldOffset(40)] public OpcodeBodySet? QueryMissed;

        [FieldOffset(48)] public volatile IlContract? Contract;
        [FieldOffset(56)] public volatile IlFork? FailedFor;
        [FieldOffset(64)] public string? FailureReason;
        /// <summary>The precompiled contract for this code, kept apart so a run-time compile for another fork cannot displace it.</summary>
        [FieldOffset(72)] public volatile IlContract? Shipped;
        /// <summary>Interpreted opcodes counted toward hotness; a long, as sampled frames count with a weight and busy code runs billions.</summary>
        [FieldOffset(80)] public long OpCodesRun;
        /// <summary>When the current one-second window of <see cref="WindowOpCodes"/> began.</summary>
        [FieldOffset(88)] public long WindowStart;
        /// <summary>Until when a hot frame does not ask for a compile again, after one was turned away by a budget.</summary>
        [FieldOffset(96)] public long RetryAfter;
        /// <summary>
        /// A fast 64-bit hash of the code, taken once the code runs a second frame, keying the shared cache; also the key
        /// code no cache stamped is found by among the shipped contracts and the profiles, hashed once per state.
        /// </summary>
        [FieldOffset(104)] public long ContentHash;
        [FieldOffset(112)] public int Executions;
        /// <summary>Opcodes the interpreter ran for this code in the current one-second window.</summary>
        [FieldOffset(116)] public int WindowOpCodes;
        /// <summary>Frames that reported in the current window.</summary>
        [FieldOffset(120)] public int WindowFrames;
        [FieldOffset(124)] public int Pending;
        [FieldOffset(128)] public bool HasContentHash;
        /// <summary>Whether the code was looked up among the precompiled contracts, which happens on its first frame of a fork they were compiled for.</summary>
        [FieldOffset(129)] public bool PrecompileChecked;
        /// <summary>Adaptive: the <see cref="IlRuntime.MissEpoch"/> the misses were recorded at; at a newer one their body sets are looked for again.</summary>
        [FieldOffset(132)] public int MissEpoch;
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

    internal static IlFork IlGetFork(OpcodeBodySet bodies)
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
    internal static long IlContentHash(CodeInfo codeInfo) => IlContentHash(codeInfo.CodeSpan);

    /// <summary>The content hash of <paramref name="code"/>, as <see cref="IlContentHash(CodeInfo)"/> takes it of a code info's code.</summary>
    internal static long IlContentHash(ReadOnlySpan<byte> code) =>
        ((long)code.FastHash() << 32) | (uint)code[(code.Length / 2)..].FastHash();

    /// <summary>The compiled contract to run a frame of <paramref name="codeInfo"/> on, or null for the interpreter.</summary>
    /// <remarks>Counts the frame towards the code's hotness and requests its compile once it is hot.</remarks>
    /// <param name="count">Whether the interpreter should report the frame's opcodes, which counts it toward hotness.</param>
    internal static IlContract? Find(CodeInfo codeInfo, OpcodeBodySet bodies, out IlCodeState state, out bool count)
    {
        ref object? slot = ref IlEvmFrameRunner.CodeSlot(codeInfo);
        if (slot is not IlCodeState existing)
            existing = NewState(ref slot);

        state = existing;
        count = false;
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

        IlContract? shipped = state.Shipped;
        if (shipped is not null && ReferenceEquals(shipped.Fork, fork))
            return shipped;

        // A precompiled contract runs compiled from the code's first frame.
        if (!state.PrecompileChecked && Attach(codeInfo, state, fork) is { } precompiled)
            return precompiled;

        if (ReferenceEquals(state.FailedFor, fork))
            return null;

        // Past its second frame, code counts toward hotness on a random one frame in HotnessSampling, with that weight:
        // every frame writing the counters would have threads that run the same contract fight over its cache line.
        int weight = SamplingWeight;
        if (weight > 1 && state.Executions >= 2)
        {
            uint random = _hotnessRandom == 0 ? (uint)Environment.CurrentManagedThreadId * 2654435761u | 1 : _hotnessRandom;
            random ^= random << 13;
            random ^= random >> 17;
            random ^= random << 5;
            _hotnessRandom = random;
            if ((random & (uint)(weight - 1)) != 0)
                return null;
        }

        count = true;
        int executions = state.Executions += state.Executions < 2 ? 1 : weight;
        // The rate rule needs two counted frames in its window when frames are sampled, so one large sampled frame,
        // weighted, cannot stand in for a second of steady use.
        bool hot = (executions >= IlEvmSettings.Threshold || state.OpCodesRun >= IlEvmSettings.OpcodeThreshold) &&
            (IlEvmSettings.Threshold <= 1 || (state.WindowOpCodes >= IlEvmSettings.HotOpcodesPerSecond && (weight == 1 || state.WindowFrames >= 2)));
        // A second frame, or the first hot one, looks the code up by content: it may be compiled under another code info.
        if ((executions == 2 || hot) && IlShared(codeInfo, state, fork) is { } shared)
            return shared;

        return hot && (state.RetryAfter == 0 || Environment.TickCount64 >= state.RetryAfter) ? IlRequest(codeInfo, state, fork) : null;
    }

    [ThreadStatic] private static uint _hotnessRandom;

    /// <summary>
    /// A new state, published in the slot over nothing, or over an Adaptive mark in a process that switched rules; the
    /// state another thread published first wins, and over anything else the new state serves the frame unpublished.
    /// </summary>
    private static IlCodeState NewState(ref object? slot)
    {
        object? seen = slot;
        if (seen is not IlQuiet)
            seen = null;
        IlCodeState created = new();
        return Interlocked.CompareExchange(ref slot, created, seen) as IlCodeState ?? created;
    }

    /// <summary>The weight of a counted frame: <see cref="IlEvmSettings.HotnessSampling"/>, or 1 with diagnostics on, which report every frame.</summary>
    internal static int SamplingWeight => IlEvmDiagnostics.Enabled ? 1 : IlEvmSettings.HotnessSampling;

    /// <summary>
    /// The compiled contract a block frame runs when block processing compiles nothing at run time: the precompiled one
    /// for its code, or one the read-only query path compiled, else null for the interpreter, with no counting.
    /// </summary>
    internal static IlContract? FindPrecompiledOnly(CodeInfo codeInfo, OpcodeBodySet bodies)
    {
        ref object? slot = ref IlEvmFrameRunner.CodeSlot(codeInfo);
        if (slot is IlCodeState state)
        {
            IlContract? contract = state.Contract;
            IlContract? shipped = state.Shipped;
            if (contract is null && shipped is null && state.PrecompileChecked)
                return null;
            IlFork fork = IlGetFork(bodies);
            if (contract is not null && ReferenceEquals(contract.Fork, fork))
                return contract;
            if (shipped is not null && ReferenceEquals(shipped.Fork, fork))
                return shipped;
            return state.PrecompileChecked ? null : Attach(codeInfo, state, fork);
        }

        // Nothing shipped: the code is left alone, the slot unclaimed.
        if (Volatile.Read(ref _precompiled) is null)
            return null;
        return Attach(codeInfo, NewState(ref slot), IlGetFork(bodies));
    }

    /// <summary>
    /// As <see cref="FindPrecompiledOnly"/>, but never a contract the read-only query path compiled at run time: the
    /// block frames of a <see cref="IlEvmSettings.Stats"/> run differ between two runs only by the list they shipped.
    /// </summary>
    internal static IlContract? FindShippedOnly(CodeInfo codeInfo, OpcodeBodySet bodies)
    {
        ref object? slot = ref IlEvmFrameRunner.CodeSlot(codeInfo);
        if (slot is IlCodeState state)
        {
            IlContract? shipped = state.Shipped;
            if (shipped is null && state.PrecompileChecked)
                return null;
            IlFork fork = IlGetFork(bodies);
            if (shipped is not null && ReferenceEquals(shipped.Fork, fork))
                return shipped;
            return state.PrecompileChecked ? null : Attach(codeInfo, state, fork);
        }

        if (Volatile.Read(ref _precompiled) is null)
            return null;
        return Attach(codeInfo, NewState(ref slot), IlGetFork(bodies));
    }

    /// <summary>
    /// Looks the code up among the precompiled contracts and attaches the one for <paramref name="fork"/>. The code is
    /// marked checked unless it was shipped for another fork, so a frame of that fork looks again.
    /// </summary>
    private static IlContract? Attach(CodeInfo codeInfo, IlCodeState state, IlFork fork)
    {
        IlContract? precompiled = Precompiled(codeInfo, state, fork, out bool otherFork);
        if (precompiled is null)
        {
            if (!otherFork)
                state.PrecompileChecked = true;
            return null;
        }

        state.Shipped = precompiled;
        if (state.Contract is null)
            state.Contract = precompiled;
        state.PrecompileChecked = true;
        // Laid out at startup: its self-check waits from its first frame, so the helpers it calls can tier up first.
        if (Interlocked.Increment(ref _precompiledAttached) > 0 && precompiled.Attached == 0 && Interlocked.Exchange(ref precompiled.Attached, 1) == 0)
            precompiled.CompiledAt = Environment.TickCount64;
        return precompiled;
    }

    private static long _precompiledAttached;

    /// <summary>Code states that took a precompiled contract on their first frame: how much of the shipped list running code met.</summary>
    public static long PrecompiledAttached => Interlocked.Read(ref _precompiledAttached);

    /// <summary>The contracts compiled from the shipped list, by the keccak of their code; null when there are none.</summary>
    /// <remarks>Code no cache stamped with its keccak is found by content instead, through <see cref="IlRuntime.ShippedByContent"/>.</remarks>
    private static Dictionary<ValueHash256, IlContract>? _precompiled;

    /// <summary>Publishes contracts compiled for a list in place of whatever was published, for tests; the startup compile adds through <see cref="IlRuntime.PublishShipped"/>.</summary>
    internal static void PublishPrecompiled(IReadOnlyDictionary<ValueHash256, IlContract> contracts)
    {
        if (contracts.Count == 0)
            return;
        ClearPrecompiled();
        IlRuntime.PublishShipped([.. contracts.Select(static c => (new IlEvmPrecompileList.Entry(c.Key), c.Value))]);
    }

    /// <summary>Adds shipped contracts to those frames find by the keccak of their code, for <see cref="IlRuntime.PublishShipped"/>.</summary>
    /// <remarks>Copied on write: a frame reads the map it finds without a lock.</remarks>
    internal static void AddPrecompiled(IEnumerable<(ValueHash256 Hash, IlContract Contract)> contracts)
    {
        Dictionary<ValueHash256, IlContract> map = Volatile.Read(ref _precompiled) is { } published ? new(published) : [];
        foreach ((ValueHash256 hash, IlContract contract) in contracts)
            map[hash] = contract;
        Volatile.Write(ref _precompiled, map);
    }

    /// <summary>Forgets the precompiled contracts, and with them every profile the runtime keeps, for tests.</summary>
    internal static void ClearPrecompiled()
    {
        Volatile.Write(ref _precompiled, null);
        IlRuntime.ResetForTests();
    }

    /// <summary>How many contracts the startup compile published, for tests and diagnostics.</summary>
    public static int PrecompiledCount => Volatile.Read(ref _precompiled)?.Count ?? 0;

    /// <summary>Whether the startup compile published a contract for the code of keccak <paramref name="hash"/>.</summary>
    public static bool IsPrecompiled(in ValueHash256 hash) => Volatile.Read(ref _precompiled)?.ContainsKey(hash) == true;

    private static IlContract? Precompiled(CodeInfo codeInfo, IlCodeState state, IlFork fork, out bool otherFork)
    {
        otherFork = false;
        Dictionary<ValueHash256, IlContract>? map = Volatile.Read(ref _precompiled);
        if (map is null)
            return null;
        IlContract? contract;
        ValueHash256 hash = codeInfo.CodeHash;
        if (hash != default)
        {
            if (!map.TryGetValue(hash, out contract))
                return null;
        }
        else
        {
            // Code no cache stamped, as from a state override: found among the shipped contracts as long by its bytes.
            // Never by its keccak, which takes tens of microseconds over large code, on the frame that met a new code info.
            contract = IlRuntime.ShippedByContent(codeInfo)?.Shipped;
            if (contract is null)
                return null;
        }

        if (ReferenceEquals(contract.Fork, fork))
            return contract;
        otherFork = true;
        return null;
    }

    /// <summary>Lays out <paramref name="code"/> for <paramref name="bodies"/> and JIT-compiles every chunk, for the startup compile.</summary>
    /// <remarks>The contract is permanent, so it counts toward none of the budgets that bound what run-time compiles may hold.</remarks>
    internal static IlContract? Precompile(byte[] code, OpcodeBodySet bodies, out string? reason)
    {
        IlContract? contract = IlCompiler.Compile(new CodeInfo(code), IlGetFork(bodies), out reason);
        if (contract is null)
            return null;
        contract.CompileAllChunks();
        // Never released: a verdict against it only keeps the runner whose self-check reached it off it.
        contract.Check.Name = contract.Name;
        return contract;
    }

    /// <summary>
    /// As <see cref="Precompile"/>, laid out against the ahead twins of the out-of-line helpers, optimized from their first
    /// call: a contract's ahead copy (<see cref="IlContract.Ahead"/>), the same code calling other methods.
    /// </summary>
    internal static IlContract? PrecompileAhead(byte[] code, OpcodeBodySet bodies, out string? reason)
    {
        bool layingOutAhead = _layingOutAhead;
        _layingOutAhead = true;
        try
        {
            return Precompile(code, bodies, out reason);
        }
        finally
        {
            _layingOutAhead = layingOutAhead;
        }
    }

    /// <summary>The body set the core runs for <paramref name="spec"/>, from its internal per-spec cache; null when this core has none.</summary>
    internal static OpcodeBodySet? BodySetFor(Nethermind.Core.Specs.IReleaseSpec spec) =>
        _bodySetFor?.Invoke(null, [spec]) as OpcodeBodySet;

    private static readonly MethodInfo? _bodySetFor =
        typeof(VirtualMachine<EthereumGasPolicy>).GetMethod("GetOpcodeBodySet", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Nethermind.Core.Specs.IReleaseSpec)]);

    /// <summary>How long code that a budget turned away waits before asking again.</summary>
    private const int RetryDelayMs = 5_000;

    /// <summary>The interpreter ran <paramref name="opCodes"/> opcodes of a frame of <paramref name="codeInfo"/>; what makes it hot.</summary>
    internal static void OnInterpreted(CodeInfo codeInfo, int opCodes)
    {
        if (IlEvmFrameRunner.CodeSlot(codeInfo) is not IlCodeState state)
            return;
        // Past its second frame only sampled frames report, each standing for HotnessSampling of them. Weighted, a
        // frame's count can pass an int's range, so it is summed as a long and the window's count saturates.
        long ran = opCodes;
        if (state.Executions > 2)
            ran *= SamplingWeight;
        state.OpCodesRun += ran;
        long now = Environment.TickCount64;
        if (now - state.WindowStart >= 1000)
        {
            state.WindowStart = now;
            state.WindowOpCodes = 0;
            state.WindowFrames = 0;
        }

        state.WindowOpCodes = (int)Math.Min(int.MaxValue, state.WindowOpCodes + ran);
        state.WindowFrames++;
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
        CompiledOf(codeInfo) is { } contract ? (contract.IlBytes, contract.JitTime, contract.CompiledChunks) : null;

    /// <summary>Whether <paramref name="codeInfo"/> has a compiled contract, for tests and diagnostics.</summary>
    public static bool IsCompiled(CodeInfo codeInfo) => CompiledOf(codeInfo) is not null;

    /// <summary>The Legacy rules' contract of the code's state, else what an Adaptive runner runs it on.</summary>
    private static IlContract? CompiledOf(CodeInfo codeInfo) => IlEvmFrameRunner.CodeSlot(codeInfo) switch
    {
        IlCodeState state => state.Contract ?? state.QueryRun ?? state.BlockRun,
        IlSimpleState simple => simple.Query ?? simple.Block,
        _ => null,
    };

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
        // Built as one vector: four limb stores read back whole could not be forwarded.
        UInt256 constant = Unsafe.BitCast<Vector256<ulong>, UInt256>(Vector256.Create(c0, c1, c2, c3));
        ref UInt256 value = ref Unsafe.As<byte, UInt256>(ref operand);
        UInt256 result;
        if (constantOnTop)
            TOpMath.Operation(in constant, in value, out result);
        else
            TOpMath.Operation(in value, in constant, out result);
        // One 32-byte store, which the next opcode's whole-word read is forwarded from.
        Unsafe.WriteUnaligned(ref destination, Vector256.Create(result.u0, result.u1, result.u2, result.u3));
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
        UInt256 constant = Unsafe.BitCast<Vector256<ulong>, UInt256>(Vector256.Create(c0, c1, c2, c3));
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
        Unsafe.WriteUnaligned(ref Unsafe.As<UInt256, byte>(ref b), Vector256.Create(result.u0, result.u1, result.u2, result.u3));
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

    /// <summary>CALLDATALOAD of an offset held in a register, past the end of the input or not below 2^32: zeros where the input ends.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector256<ulong> IlCallDataWord(ref byte input, nint length, Vector256<ulong> offset) => IlCallDataWordAt(ref input, length, offset);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static Vector256<ulong> IlCallDataWordAhead(ref byte input, nint length, Vector256<ulong> offset) => IlCallDataWordAt(ref input, length, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> IlCallDataWordAt(ref byte input, nint length, Vector256<ulong> offset)
    {
        // Past the input, or past 2^64, the body reads nothing and pushes zero.
        ulong at = offset.GetElement(0);
        if ((offset.GetElement(1) | offset.GetElement(2) | offset.GetElement(3)) != 0 || at >= (ulong)length)
            return Vector256<ulong>.Zero;
        // Not through IlCallDataLoadAt: its InitBlockUnaligned of a word in a vector local is emitted by the .NET 10 JIT
        // as a VEX.256 vmovq, which the processor faults on.
        uint available = (uint)length - (uint)at;
        ref byte source = ref Unsafe.Add(ref input, (nint)at);
        EvmWord word;
        if (available >= EvmStack.WordSize)
        {
            word = Unsafe.ReadUnaligned<EvmWord>(ref source);
        }
        else
        {
            // The input's last bytes, then zeros.
            word = default;
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<EvmWord, byte>(ref word), ref source, available);
        }

        return word.ByteSwap().AsUInt64();
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
    private static void IlCarry(ref byte slot) => IlCarryWord(ref slot);

    /// <summary>Borrows one from limb 1 and up of the word at <paramref name="slot"/>, for a subtraction whose low limb wrapped.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlBorrow(ref byte slot) => IlBorrowWord(ref slot);

    /// <summary>Carries one into limb 1 and up of <paramref name="word"/>, held in a register by compiled code, for an addition whose low limb wrapped.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector256<ulong> IlCarryVector(Vector256<ulong> word) => IlCarryLimbs(word);

    /// <summary>Borrows one from limb 1 and up of <paramref name="word"/>, held in a register by compiled code, for a subtraction whose low limb wrapped.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector256<ulong> IlBorrowVector(Vector256<ulong> word) => IlBorrowLimbs(word);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static Vector256<ulong> IlCarryVectorAhead(Vector256<ulong> word) => IlCarryLimbs(word);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static Vector256<ulong> IlBorrowVectorAhead(Vector256<ulong> word) => IlBorrowLimbs(word);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> IlCarryLimbs(Vector256<ulong> word)
    {
        ulong u1 = word.GetElement(1) + 1, u2 = word.GetElement(2), u3 = word.GetElement(3);
        if (u1 == 0 && ++u2 == 0)
            u3++;
        return Vector256.Create(word.GetElement(0), u1, u2, u3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> IlBorrowLimbs(Vector256<ulong> word)
    {
        ulong u1 = word.GetElement(1), u2 = word.GetElement(2), u3 = word.GetElement(3);
        if (u1-- == 0 && u2-- == 0)
            u3--;
        return Vector256.Create(word.GetElement(0), u1, u2, u3);
    }

    /// <summary>The carry, reading and writing the word whole, as compiled code wrote it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlCarryWord(ref byte slot)
    {
        Vector256<ulong> word = Unsafe.ReadUnaligned<Vector256<ulong>>(ref slot);
        ulong u1 = word.GetElement(1) + 1, u2 = word.GetElement(2), u3 = word.GetElement(3);
        if (u1 == 0 && ++u2 == 0)
            u3++;
        Unsafe.WriteUnaligned(ref slot, Vector256.Create(word.GetElement(0), u1, u2, u3));
    }

    /// <summary>The borrow, reading and writing the word whole, as compiled code wrote it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlBorrowWord(ref byte slot)
    {
        Vector256<ulong> word = Unsafe.ReadUnaligned<Vector256<ulong>>(ref slot);
        ulong u1 = word.GetElement(1), u2 = word.GetElement(2), u3 = word.GetElement(3);
        if (u1-- == 0 && u2-- == 0)
            u3--;
        Unsafe.WriteUnaligned(ref slot, Vector256.Create(word.GetElement(0), u1, u2, u3));
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

    // Ahead twins of the called twins, and of the carry and the borrow: the same code, optimized from its first call.
    // A contract's ahead copy calls them (IlContract.Ahead): it serves a node's first calls while the called twins, which
    // tier up on calls and are profiled on them, still run tier-0 code, slower than the interpreter. Profiled on nothing,
    // the ahead twins run a few percent slower than the called twins once those are optimized, so the copy is dropped
    // then (IlWarmUp.SwitchFromAhead). Each is found by its called twin's name with the "Ahead" suffix.

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlCarryAhead(ref byte slot) => IlCarryWord(ref slot);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlBorrowAhead(ref byte slot) => IlBorrowWord(ref slot);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlMath2CalledAhead<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlMath2<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlMath2InPlaceCalledAhead<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlMath2InPlace<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlMath2ConstCalledAhead<TOpMath>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param =>
        IlMath2Const<TOpMath>(ref operand, ref destination, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlBitwiseCalledAhead<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise => IlBitwise<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlBitwiseConstCalledAhead<TOp>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise =>
        IlBitwiseConst<TOp>(ref operand, ref destination, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlMath1CalledAhead<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param => IlMath1<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlPredicate2CalledAhead<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlPredicate2<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlPredicate2ConstCalledAhead<TOpMath>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param =>
        IlPredicate2Const<TOpMath>(ref operand, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlPredicateBitwiseCalledAhead<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise => IlPredicateBitwise<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlPredicateBitwiseConstCalledAhead<TOp>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise =>
        IlPredicateBitwiseConst<TOp>(ref operand, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlPredicate1CalledAhead<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param => IlPredicate1<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlTryMLoadCalledAhead(ref EvmPooledMemory memory, ref byte slot) => IlTryMLoad(ref memory, ref slot);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong IlTryMStoreCalledAhead(ref EvmPooledMemory memory, ref byte offsetSlot, ulong gas) => IlTryMStore(ref memory, ref offsetSlot, gas);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlTryMLoadAtCalledAhead(ref EvmPooledMemory memory, ulong offset, ref byte destination) => IlTryMLoadAt(ref memory, offset, ref destination);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong IlTryMStoreAtCalledAhead(ref EvmPooledMemory memory, ulong offset, ref byte value, ulong gas) => IlTryMStoreAt(ref memory, offset, ref value, gas);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong IlTryMStoreWordAtCalledAhead(ref EvmPooledMemory memory, ulong offset, ulong m0, ulong m1, ulong m2, ulong m3, ulong gas) =>
        IlTryMStoreWordAt(ref memory, offset, m0, m1, m2, m3, gas);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlCallDataLoadAtCalledAhead(ref byte input, nint length, ulong offset, ref byte destination) => IlCallDataLoadAt(ref input, length, offset, ref destination);

    private static readonly ConcurrentDictionary<MethodInfo, MethodInfo> _ilCalledTwins = new(), _ilAheadTwins = new();

    /// <summary>Whether the code being laid out on this thread is a contract's ahead copy, whose calls go to the ahead twins (<see cref="PrecompileAhead"/>).</summary>
    [ThreadStatic] private static bool _layingOutAhead;

    /// <summary>The called twin of an inlined helper, found by name; its ahead twin while an ahead copy is laid out.</summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Opcode bodies are rooted by the dispatch table; the compiler is off without dynamic code.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The twins are private static methods of this type, referenced here.")]
    private static MethodInfo IlCalledTwin(MethodInfo helper) => _layingOutAhead
        ? _ilAheadTwins.GetOrAdd(helper, static h => IlTwin(h, "CalledAhead"))
        : _ilCalledTwins.GetOrAdd(helper, static h => IlTwin(h, "Called"));

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Opcode bodies are rooted by the dispatch table; the compiler is off without dynamic code.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The twins are private static methods of this type, referenced here.")]
    private static MethodInfo IlTwin(MethodInfo helper, string suffix)
    {
        MethodInfo twin = typeof(IlEvm).GetMethod(helper.Name + suffix, BindingFlags.NonPublic | BindingFlags.Static)!;
        return helper.IsGenericMethod ? twin.MakeGenericMethod(helper.GetGenericArguments()) : twin;
    }

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
    private static EvmExceptionType IlEmbedded(IlChunk chunk, int site, VirtualMachine<EthereumGasPolicy> vm, ref EvmStack stack, ref EthereumGasPolicy gas, ref nint programCounter, out int opsAdjust, nint head, ulong gasNow) =>
        IlEmbeddedRun(chunk, site, vm, ref stack, ref gas, ref programCounter, out opsAdjust, head, gasNow);

    /// <summary><see cref="IlEmbedded"/> optimized from its first call, for a contract's ahead copy (<see cref="IlContract.Ahead"/>).</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static EvmExceptionType IlEmbeddedAhead(IlChunk chunk, int site, VirtualMachine<EthereumGasPolicy> vm, ref EvmStack stack, ref EthereumGasPolicy gas, ref nint programCounter, out int opsAdjust, nint head, ulong gasNow) =>
        IlEmbeddedRun(chunk, site, vm, ref stack, ref gas, ref programCounter, out opsAdjust, head, gasNow);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType IlEmbeddedRun(IlChunk chunk, int site, VirtualMachine<EthereumGasPolicy> vm, ref EvmStack stack, ref EthereumGasPolicy gas, ref nint programCounter, out int opsAdjust, nint head, ulong gasNow)
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
        public static long BusyTicks;

        public static int Pending => _work.Count;

        /// <summary>Whether the compiler thread runs under Linux's SCHED_IDLE, for diagnostics: null until it starts.</summary>
        public static bool? IdleScheduling;

        private struct SchedParam
        {
            public int Priority;
        }

        [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "sched_setscheduler", SetLastError = true)]
        private static extern int SchedSetScheduler(int pid, int policy, ref SchedParam param);

        /// <summary>Moves the calling thread to SCHED_IDLE, which an unprivileged thread may always do; false if the call fails.</summary>
        /// <remarks>The inter-block scheduler's workers take it too, under <see cref="IlEvmSettings.CompileIdle"/>.</remarks>
        internal static bool SetIdleScheduling()
        {
            const int SchedIdle = 5;
            try
            {
                SchedParam param = default;
                // pid 0 is the calling thread.
                return SchedSetScheduler(0, SchedIdle, ref param) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>Queues <paramref name="work"/> for the background compiler, unless <see cref="IlEvmSettings.MaxPendingCompiles"/> wait already.</summary>
        public static bool Enqueue(Action work)
        {
            if (_work.Count >= IlEvmSettings.MaxPendingCompiles)
                return false;
            _work.Add(work);
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                Thread worker = new(Run) { IsBackground = true, Name = "EVM IL compiler", Priority = IlEvmSettings.CompilePriority };
                worker.Start();
            }

            return true;
        }

        private static void Run()
        {
            if (IlEvmSettings.CompileIdle && OperatingSystem.IsLinux())
                IdleScheduling = SetIdleScheduling();
            foreach (Action work in _work.GetConsumingEnumerable())
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    work();
                }
                catch
                {
                    // IlCompileInto records its own failures; nothing else may take the worker down.
                }

                Interlocked.Add(ref BusyTicks, System.Diagnostics.Stopwatch.GetTimestamp() - started);
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
        private static readonly MethodInfo EmbeddedAheadMethod = typeof(IlEvm).GetMethod(nameof(IlEmbeddedAhead), BindingFlags.NonPublic | BindingFlags.Static)!;
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
        private static readonly FieldInfo StateCounted = typeof(IlRunState).GetField(nameof(IlRunState.Counted))!;
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
        private static readonly MethodInfo CarryAheadMethod = typeof(IlEvm).GetMethod(nameof(IlCarryAhead), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo VectorCreate = typeof(Vector256).GetMethod(nameof(Vector256.Create), [typeof(ulong), typeof(ulong), typeof(ulong), typeof(ulong)])!;
        private static readonly MethodInfo VectorCreateScalar = typeof(Vector256).GetMethod(nameof(Vector256.CreateScalar), [typeof(ulong)])!;
        private static readonly MethodInfo VectorAsByte = typeof(Vector256).GetMethod(nameof(Vector256.AsByte))!.MakeGenericMethod(typeof(ulong));
        private static readonly MethodInfo VectorOnesComplement = typeof(Vector256).GetMethod(nameof(Vector256.OnesComplement))!.MakeGenericMethod(typeof(byte));
        private static readonly MethodInfo BorrowMethod = typeof(IlEvm).GetMethod(nameof(IlBorrow), BindingFlags.NonPublic | BindingFlags.Static)!;

        /// <summary>A generic <see cref="Vector256"/> method of <paramref name="parameters"/> vector parameters, made for <paramref name="element"/>.</summary>
        private static MethodInfo VectorMethod(string name, Type element, int parameters) =>
            typeof(Vector256).GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == name && m.IsGenericMethodDefinition && m.GetParameters().Length == parameters &&
                             m.GetParameters().All(static p => p.ParameterType.IsGenericType && p.ParameterType.GetGenericTypeDefinition() == typeof(Vector256<>)))
                .MakeGenericMethod(element);

        private static readonly MethodInfo VecAdd = VectorMethod(nameof(Vector256.Add), typeof(ulong), 2);
        private static readonly MethodInfo VecSubtract = VectorMethod(nameof(Vector256.Subtract), typeof(ulong), 2);
        private static readonly MethodInfo VecAnd = VectorMethod(nameof(Vector256.BitwiseAnd), typeof(ulong), 2);
        private static readonly MethodInfo VecOr = VectorMethod(nameof(Vector256.BitwiseOr), typeof(ulong), 2);
        private static readonly MethodInfo VecXor = VectorMethod(nameof(Vector256.Xor), typeof(ulong), 2);
        private static readonly MethodInfo VecEqualsAll = VectorMethod(nameof(Vector256.EqualsAll), typeof(ulong), 2);
        private static readonly MethodInfo VecToScalar = VectorMethod(nameof(Vector256.ToScalar), typeof(ulong), 1);
        private static readonly MethodInfo VecAsInt64 = VectorMethod(nameof(Vector256.AsInt64), typeof(ulong), 1);
        private static readonly MethodInfo VecGreaterThanSigned = VectorMethod(nameof(Vector256.GreaterThan), typeof(long), 2);
        private static readonly MethodInfo VecMaskSigned = VectorMethod(nameof(Vector256.ExtractMostSignificantBits), typeof(long), 1);
        private static readonly MethodInfo VecZero = typeof(Vector256<ulong>).GetProperty(nameof(Vector256<ulong>.Zero))!.GetGetMethod()!;
        private static readonly MethodInfo VecOnesComplement = VectorMethod(nameof(Vector256.OnesComplement), typeof(ulong), 1);
        private static readonly MethodInfo VecGetElement = typeof(Vector256).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(static m => m.Name == nameof(Vector256.GetElement) && m.IsGenericMethodDefinition).MakeGenericMethod(typeof(ulong));
        private static readonly MethodInfo CarryVectorMethod = typeof(IlEvm).GetMethod(nameof(IlCarryVector), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo CallDataWordMethod = typeof(IlEvm).GetMethod(nameof(IlCallDataWord), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo CallDataWordAheadMethod = typeof(IlEvm).GetMethod(nameof(IlCallDataWordAhead), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo VecAsByte = VectorMethod(nameof(Vector256.AsByte), typeof(ulong), 1);
        private static readonly MethodInfo VecBytesAsUInt64 = VectorMethod(nameof(Vector256.AsUInt64), typeof(byte), 1);
        private static readonly MethodInfo Permute4x64 = typeof(System.Runtime.Intrinsics.X86.Avx2).GetMethod(nameof(System.Runtime.Intrinsics.X86.Avx2.Permute4x64), [typeof(Vector256<ulong>), typeof(byte)])!;
        private static readonly MethodInfo ShuffleBytes = typeof(System.Runtime.Intrinsics.X86.Avx2).GetMethod(nameof(System.Runtime.Intrinsics.X86.Avx2.Shuffle), [typeof(Vector256<byte>), typeof(Vector256<byte>)])!;
        private static readonly MethodInfo TestZ = typeof(System.Runtime.Intrinsics.X86.Avx).GetMethod(nameof(System.Runtime.Intrinsics.X86.Avx.TestZ), [typeof(Vector256<ulong>), typeof(Vector256<ulong>)])!;
        private static readonly MethodInfo CarryVectorAheadMethod = typeof(IlEvm).GetMethod(nameof(IlCarryVectorAhead), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo BorrowVectorMethod = typeof(IlEvm).GetMethod(nameof(IlBorrowVector), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo BorrowVectorAheadMethod = typeof(IlEvm).GetMethod(nameof(IlBorrowVectorAhead), BindingFlags.NonPublic | BindingFlags.Static)!;
        private static readonly MethodInfo BorrowAheadMethod = typeof(IlEvm).GetMethod(nameof(IlBorrowAhead), BindingFlags.NonPublic | BindingFlags.Static)!;

        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
        internal static IlContract? Compile(CodeInfo codeInfo, IlFork fork, out string? reason)
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

                // The Adaptive rules' layouts run as steps between blocks, each its own estimate's length: their eager chunks
                // are jobs of their own (LayoutJob), never one long step.
                if (IlEvmSettings.EagerChunks && IlEvmSettings.Mode != IlMode.Adaptive)
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

            IlWriter generator = new(method.GetILGenerator()) { MethodName = method.Name };
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
            contract.ChunkIlBytes[index] = generator.ILOffset;
            contract.SetCallees(index, generator.Callees);
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

            /// <summary>Whether the segment keeps words in vector locals (<see cref="IlEvmSettings.VectorForms"/>), whose forms are larger.</summary>
            private static bool Tracked => IlEvmSettings.VectorForms && IlEvmSettings.DirectArithmetic;

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
                        // With the vector forms a bitwise result, and a constant shift's or addition's, stays in a local.
                        bool tracked = Tracked && !op.IsPredicate &&
                            (op.Family == IlFamily.Bitwise || ((op.Family == IlFamily.Shift || op.Family == IlFamily.Math2) && (At(h - 1) == Held.Constant || At(h - 2) == Held.Constant)));
                        Set(h - 1, Held.Memory);
                        _height = h - 1;
                        if (folds)
                        {
                            Set(h - 2, Held.Constant);
                            break;
                        }

                        Set(h - 2, op.IsPredicate ? Held.Flag : tracked ? Held.Constant : Held.Memory);
                        _il += constantShift ? 72 : op.IsPredicate && Tracked ? 36 : op.IsPredicate || IlEvmSettings.DirectArithmetic ? 24 : 14;
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
                    case IlKind.CheckedBody when Tracked && op.IsCallDataLoad:
                        _il += 50;
                        Set(h - 1, Held.Constant);
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

            /// <summary>Every method the chunk calls, directly or through an opcode body's entry point, which its JIT does not compile with it: they are compiled ahead, off the frames' threads (<see cref="IlWarmUp"/>).</summary>
            public readonly HashSet<MethodBase> Callees = [];

            /// <summary>Records a method the chunk calls through a function pointer.</summary>
            public void Calls(MethodBase? method)
            {
                if (method is not null)
                    Callees.Add(method);
            }

            public int ILOffset => generator.ILOffset;

            public LocalBuilder DeclareLocal(Type type)
            {
                Locals++;
                return generator.DeclareLocal(type);
            }

            public Label DefineLabel() => generator.DefineLabel();

            public void MarkLabel(Label label) => generator.MarkLabel(label);

            /// <summary>With <see cref="IlEvmDiagnostics.Markers"/>: the method's name, the code offset of the opcode being emitted, and the category last marked.</summary>
            public string MethodName = "";
            public int Pc = -1;
            private string? _marked;

            /// <summary>With <see cref="IlEvmDiagnostics.Markers"/>: stores <paramref name="id"/> in the marker.</summary>
            public void StoreMarker(int id)
            {
                generator.Emit(OpCodes.Ldc_I4, id);
                generator.Emit(OpCodes.Stsfld, MarkerField);
            }

            private static readonly FieldInfo MarkerField = typeof(IlEvmDiagnostics).GetField(nameof(IlEvmDiagnostics.Marker))!;

            private void Count(OpCode opcode)
            {
                Instructions++;
                if (IlEvmDiagnostics.Markers && !ReferenceEquals(_marked, Category))
                {
                    _marked = Category;
                    StoreMarker(IlEvmDiagnostics.NextMarker(MethodName, Category, Pc));
                }
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
                Callees.Add(method);
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
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
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
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldloc, _failOps);
                il.Emit(OpCodes.Stfld, StateOpCodes);
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
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                il.Emit(OpCodes.Ldloc, _opsAdjust);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stfld, StateOpCodes);
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
                // The chain's opcode count and poll mark stay in the run state, in memory: as locals live through every
                // segment, the JIT moved them between registers and the stack at nearly every block boundary.

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
                il.Pc = segment.StartPc;
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
                    if (IlEvmDiagnostics.Markers)
                    {
                        il.Category = ((Instruction)instruction.Opcode).ToString();
                        il.Pc = instruction.Pc;
                    }
                    else if (IlEvmDiagnostics.Enabled)
                        il.Category = op.Kind is IlKind.Native or IlKind.CheckedBody or IlKind.EmbeddedBody ? $"{op.Kind} {op.Name}" : op.Kind.ToString();
                    switch (op.Kind)
                    {
                        case IlKind.Push:
                            SetValue(delta, VVal.Constant(instruction.U0, instruction.U1, instruction.U2, instruction.U3));
                            delta++;
                            break;
                        case IlKind.ProgramCounter:
                            SetValue(delta, VVal.Constant((ulong)instruction.Pc, 0, 0, 0));
                            delta++;
                            break;
                        case IlKind.Dup when _vstack:
                        {
                            // The copy is the source's value, or an alias of its slot: nothing moves. The aliases of the
                            // copy's slot are resolved before the source is read: the source may be one of them, and a
                            // spill while resolving may write it to its slot and free its local.
                            int source = delta - op.Count;
                            if (!_virtual.ContainsKey(delta))
                                ResolveAliasesOf(delta);
                            SetValue(delta, Virtual(source) ?? VVal.AliasOf(source));
                            delta++;
                            break;
                        }
                        case IlKind.Swap when _vstack:
                            SwapValues(delta - 1, delta - 1 - op.Count);
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
                        case IlKind.Gas when _vstack:
                            // GAS reads what the interpreter would hold after it: the segment charged the rest already.
                            il.Emit(OpCodes.Ldloc, _gas);
                            if (instruction.GasAfter > 0)
                            {
                                il.Emit(OpCodes.Ldc_I8, (long)instruction.GasAfter);
                                il.Emit(OpCodes.Add);
                            }

                            il.Emit(OpCodes.Call, VectorCreateScalar);
                            il.Emit(OpCodes.Stloc, VecScratch);
                            BindScratch(delta);
                            delta++;
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
                                BeforeWrite(delta - 1, delta);
                                il.Emit(OpCodes.Ldarg_S, ArgStack);
                                il.Emit(OpCodes.Ldfld, StackInputData);
                                il.Emit(OpCodes.Ldarg_S, ArgStack);
                                il.Emit(OpCodes.Ldfld, StackInputDataLength);
                                il.Emit(OpCodes.Ldc_I8, (long)offset.U0);
                                Slot(delta - 1);
                                il.Emit(OpCodes.Call, Memory(CallDataLoadAt));
                            }
                            break;
                        case IlKind.CheckedBody when op.IsCallDataLoad && _vstack:
                            EmitCallDataLoadTracked(delta);
                            break;
                        case IlKind.CheckedBody:
                            for (int position = delta - op.StackInputs; position < delta; position++)
                                Materialize(position);
                            BeforeWrite(delta - op.StackInputs, Math.Max(delta, delta + op.StackGrowth));
                            SyncHead(delta);
                            CallBody(instruction, op);
                            il.Emit(OpCodes.Pop);
                            delta += op.StackGrowth;
                            break;
                        case IlKind.Native when op.Native is null && !(IlEvmSettings.DirectArithmetic &&
                            Virtual(delta - 1) is { Kind: VKind.Const } && (_vstack || Virtual(delta - 2) is null or { Kind: VKind.Const })):
                            // An operation the plugin has no helper for, without the constant amount its direct form needs.
                            for (int position = delta - op.StackInputs; position < delta; position++)
                                Materialize(position);
                            BeforeWrite(delta - op.StackInputs, Math.Max(delta, delta + op.StackGrowth));
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
                            // The body writes its slots; whatever aliases them reads them first, and the hand-back snapshot
                            // names what it read them into.
                            BeforeWrite(delta - op.StackInputs, Math.Max(delta, after));
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
                /// <summary>A word held whole in a <c>Vector256&lt;ulong&gt;</c> local (<see cref="IlEvmSettings.VectorForms"/>).</summary>
                Vec,
                /// <summary>
                /// The word in the stack slot of another position, <see cref="VVal.Source"/>, whose own value is in its slot: what
                /// DUP leaves (<see cref="IlEvmSettings.VectorForms"/>). Before that slot is written, or its position takes a
                /// value of its own, the alias reads it into a vector local.
                /// </summary>
                Alias,
            }

            /// <summary>An immutable virtual stack value; positions that share one (after DUP) share the object.</summary>
            private sealed class VVal
            {
                public VKind Kind;
                public ulong U0, U1, U2, U3;
                public LocalBuilder? Flag;
                /// <summary>For <see cref="VKind.Bool"/>: the value is <see cref="Flag"/> inverted (ISZERO of it).</summary>
                public bool Negated;

                /// <summary>For <see cref="VKind.Vec"/>: the local holding the word.</summary>
                public LocalBuilder? Local;
                /// <summary>For <see cref="VKind.Alias"/>: the position whose slot holds the word.</summary>
                public int Source;

                public static VVal Constant(ulong u0, ulong u1, ulong u2, ulong u3) => new() { Kind = VKind.Const, U0 = u0, U1 = u1, U2 = u2, U3 = u3 };
                public static VVal Boolean(LocalBuilder flag, bool negated) => new() { Kind = VKind.Bool, Flag = flag, Negated = negated };
                public static VVal Vector(LocalBuilder local) => new() { Kind = VKind.Vec, Local = local };
                public static VVal AliasOf(int source) => new() { Kind = VKind.Alias, Source = source };
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

                if (value.Kind is VKind.Vec or VKind.Alias)
                {
                    // One 32-byte store, of the local or of the aliased slot's word: an alias's slot holds its own position's
                    // value, never written while the alias lives, so the stores of several positions go in any order.
                    Slot(position);
                    LoadVec(position, value);
                    il.Emit(OpCodes.Stobj, typeof(Vector256<ulong>));
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

            /// <summary>SWAP: exchanges two positions' values; a word in its slot whose position turns virtual is read into a local first.</summary>
            private void SwapValues(int a, int b)
            {
                VVal? va = Virtual(a);
                VVal? vb = Virtual(b);
                if (va is not null && vb is not null)
                {
                    _virtual[a] = vb;
                    _virtual[b] = va;
                    return;
                }

                if (va is null && vb is null)
                {
                    // Both in their slots: each takes the other's word, read before anything is written.
                    Slot(a);
                    il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                    il.Emit(OpCodes.Stloc, VecScratchB);
                    Slot(b);
                    il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                    il.Emit(OpCodes.Stloc, VecScratch);
                    ResolveAliasesOf(a);
                    ResolveAliasesOf(b);
                    LocalBuilder forA = NewVec();
                    il.Emit(OpCodes.Ldloc, VecScratch);
                    il.Emit(OpCodes.Stloc, forA);
                    _virtual[a] = VVal.Vector(forA);
                    LocalBuilder forB = NewVec();
                    il.Emit(OpCodes.Ldloc, VecScratchB);
                    il.Emit(OpCodes.Stloc, forB);
                    _virtual[b] = VVal.Vector(forB);
                    return;
                }

                // One in its slot: the other position takes its word, read now; it takes the other's value.
                int inSlot = va is null ? a : b;
                int other = va is null ? b : a;
                VVal value = (va ?? vb)!;
                // An alias of that very slot: both hold its word, and nothing changes.
                if (value.Kind == VKind.Alias && value.Source == inSlot)
                    return;
                // The slot's aliases first: a spill while resolving them may write the other position to its slot and
                // free the local its value names, so the swap starts again from what is left.
                if (ResolveAliasesOf(inSlot))
                {
                    SwapValues(a, b);
                    return;
                }

                Slot(inSlot);
                il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                il.Emit(OpCodes.Stloc, VecScratch);
                _virtual[inSlot] = value;
                LocalBuilder local = NewVec();
                il.Emit(OpCodes.Ldloc, VecScratch);
                il.Emit(OpCodes.Stloc, local);
                _virtual[other] = VVal.Vector(local);
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

            /// <summary>Whether this chunk keeps words in vector locals and DUPs as aliases (<see cref="IlEvmSettings.VectorForms"/>).</summary>
            private readonly bool _vstack = IlEvmSettings.VectorForms && IlEvmSettings.DirectArithmetic;

            /// <summary>The chunk's vector locals; one holds a word while some position's <see cref="VKind.Vec"/> value is it.</summary>
            private readonly List<LocalBuilder> _vecLocals = [];

            /// <summary>The most vector locals a chunk declares; past them the words held in locals are written to their slots.</summary>
            private const int MaxVecLocals = 48;

            private LocalBuilder? _vecScratchB;

            /// <summary>A second scratch vector, for an operand an operation reads again after computing into <see cref="VecScratch"/>.</summary>
            private LocalBuilder VecScratchB => _vecScratchB ??= il.DeclareLocal(typeof(Vector256<ulong>));

            /// <summary>A vector local no position's value holds, or null when the chunk has declared all it may.</summary>
            /// <remarks>
            /// A local a cold path's snapshot still names may be taken: the path reads it where it branches off, before anything
            /// later in the segment writes it again.
            /// </remarks>
            private LocalBuilder? TryNewVec()
            {
                HashSet<LocalBuilder>? used = null;
                foreach (VVal value in _virtual.Values)
                {
                    if (value.Kind == VKind.Vec)
                        (used ??= []).Add(value.Local!);
                }

                foreach (LocalBuilder local in _vecLocals)
                {
                    if (used is null || !used.Contains(local))
                        return local;
                }

                if (_vecLocals.Count >= MaxVecLocals)
                    return null;
                LocalBuilder created = il.DeclareLocal(typeof(Vector256<ulong>));
                _vecLocals.Add(created);
                return created;
            }

            /// <summary>A vector local for a new value, writing the words held in locals out to their slots if none is free.</summary>
            private LocalBuilder NewVec()
            {
                if (TryNewVec() is { } local)
                    return local;
                SpillVecs();
                return TryNewVec()!;
            }

            /// <summary>Writes every word held in a vector local to its slot, freeing the locals.</summary>
            private void SpillVecs()
            {
                foreach (int position in _virtual.Where(static e => e.Value.Kind == VKind.Vec).Select(static e => e.Key).Order().ToArray())
                    Materialize(position);
            }

            /// <summary>
            /// Before the slot of <paramref name="position"/> is written, or its position takes a value of its own: the aliases
            /// of the slot read the word it holds into one vector local.
            /// </summary>
            /// <returns>Whether the slot had aliases, and resolving them may have spilled the vector locals.</returns>
            private bool ResolveAliasesOf(int position)
            {
                if (!_vstack)
                    return false;
                List<int>? aliased = null;
                foreach ((int at, VVal value) in _virtual)
                {
                    if (value.Kind == VKind.Alias && value.Source == position)
                        (aliased ??= []).Add(at);
                }

                if (aliased is null)
                    return false;
                LocalBuilder local = NewVec();
                Slot(position);
                il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                il.Emit(OpCodes.Stloc, local);
                VVal word = VVal.Vector(local);
                foreach (int at in aliased)
                    _virtual[at] = word;
                return true;
            }

            /// <summary>Before an operation writes the slots of the positions from <paramref name="from"/> to <paramref name="to"/>, exclusive.</summary>
            private void BeforeWrite(int from, int to)
            {
                for (int position = from; position < to; position++)
                    ResolveAliasesOf(position);
            }

            /// <summary>Gives <paramref name="position"/>, whose old value has been consumed, a value it does not hold in its slot.</summary>
            private void SetValue(int position, VVal value)
            {
                if (!_virtual.ContainsKey(position))
                    ResolveAliasesOf(position);
                _virtual[position] = value;
            }

            /// <summary>Gives <paramref name="position"/>, whose old value has been consumed, the word left in <see cref="VecScratch"/>.</summary>
            private void BindScratch(int position)
            {
                _virtual.Remove(position);
                ResolveAliasesOf(position);
                LocalBuilder local = NewVec();
                il.Emit(OpCodes.Ldloc, VecScratch);
                il.Emit(OpCodes.Stloc, local);
                _virtual[position] = VVal.Vector(local);
            }

            /// <summary>For an operation that reads its operands from their slots: writes a virtual one other than a constant there.</summary>
            private void InSlotUnlessConstant(int position, ref VVal? value)
            {
                if (value is not null && value.Kind != VKind.Const)
                {
                    Materialize(position);
                    value = null;
                }
            }

            /// <summary>Pushes limb <paramref name="limb"/> of the word at <paramref name="position"/>, whose virtual value is <paramref name="value"/>.</summary>
            private void LoadLimb(int position, VVal? value, int limb)
            {
                switch (value?.Kind)
                {
                    case null:
                        SlotLimb(position, limb);
                        break;
                    case VKind.Alias:
                        SlotLimb(value.Source, limb);
                        break;
                    case VKind.Const:
                        il.Emit(OpCodes.Ldc_I8, (long)(limb switch { 0 => value.U0, 1 => value.U1, 2 => value.U2, _ => value.U3 }));
                        break;
                    case VKind.Vec:
                        il.Emit(OpCodes.Ldloc, value.Local!);
                        il.Emit(OpCodes.Ldc_I4, limb);
                        il.Emit(OpCodes.Call, VecGetElement);
                        break;
                    default:
                        if (limb != 0)
                        {
                            il.Emit(OpCodes.Ldc_I8, 0L);
                            break;
                        }

                        il.Emit(OpCodes.Ldloc, value.Flag!);
                        if (value.Negated)
                        {
                            il.Emit(OpCodes.Ldc_I4_1);
                            il.Emit(OpCodes.Xor);
                        }

                        il.Emit(OpCodes.Conv_U8);
                        break;
                }
            }

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
                if (_vstack)
                {
                    EmitNativeTracked(op, ref delta);
                    return;
                }

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

            /// <summary>Pushes the word at <paramref name="position"/>, whose virtual value is <paramref name="value"/>, as four limbs in one vector.</summary>
            private void LoadVec(int position, VVal? value)
            {
                if (value is null || value.Kind == VKind.Alias)
                {
                    Slot(value is null ? position : value.Source);
                    il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                }
                else if (value.Kind == VKind.Vec)
                {
                    il.Emit(OpCodes.Ldloc, value.Local!);
                }
                else if (value.Kind == VKind.Const)
                {
                    EmitVecConst(value.U0, value.U1, value.U2, value.U3);
                }
                else
                {
                    il.Emit(OpCodes.Ldloc, value.Flag!);
                    if (value.Negated)
                    {
                        il.Emit(OpCodes.Ldc_I4_1);
                        il.Emit(OpCodes.Xor);
                    }

                    il.Emit(OpCodes.Conv_U8);
                    il.Emit(OpCodes.Call, VectorCreateScalar);
                }
            }

            /// <summary>Pushes a constant word as a vector, which the JIT loads from its data.</summary>
            private void EmitVecConst(ulong u0, ulong u1, ulong u2, ulong u3)
            {
                if ((u0 | u1 | u2 | u3) == 0)
                {
                    il.Emit(OpCodes.Call, VecZero);
                    return;
                }

                il.Emit(OpCodes.Ldc_I8, (long)u0);
                if ((u1 | u2 | u3) == 0)
                {
                    il.Emit(OpCodes.Call, VectorCreateScalar);
                    return;
                }

                il.Emit(OpCodes.Ldc_I8, (long)u1);
                il.Emit(OpCodes.Ldc_I8, (long)u2);
                il.Emit(OpCodes.Ldc_I8, (long)u3);
                il.Emit(OpCodes.Call, VectorCreate);
            }

            private LocalBuilder? _vecScratch;
            private LocalBuilder? _signedScratch0;
            private LocalBuilder? _signedScratch1;

            private LocalBuilder VecScratch => _vecScratch ??= il.DeclareLocal(typeof(Vector256<ulong>));

            /// <summary>The bit flipping a limb's sign, so that unsigned limbs compare as signed ones.</summary>
            private const ulong SignBit = 0x8000_0000_0000_0000;

            /// <summary>
            /// EQ of any two words, and LT, GT, SLT and SGT of any two that are not a small constant and a word, as a flag on
            /// the evaluation stack, from whole-word vector reads.
            /// </summary>
            /// <remarks>
            /// A comparison of words is decided by their most significant limb that differs. With each limb's sign bit flipped
            /// (all but the top one's for the signed comparisons), signed limb comparisons give the limbs where the first
            /// word is below the second and where it is above as two 4-bit masks, and the first is below exactly when its
            /// mask is the larger.
            /// </remarks>
            private bool TryEmitVectorComparison(IlOp op, VVal? va, VVal? vb, int top, int second)
            {
                Type? operation = OperationOf(op);
                if (operation == typeof(EvmInstructions.OpBitwiseEq))
                {
                    LoadVec(top, va);
                    LoadVec(second, vb);
                    il.Emit(OpCodes.Call, VecEqualsAll);
                    return true;
                }

                bool below = operation == typeof(EvmInstructions.OpLt) || operation == typeof(EvmInstructions.OpSLt);
                bool signed = operation == typeof(EvmInstructions.OpSLt) || operation == typeof(EvmInstructions.OpSGt);
                if (!below && !signed && operation != typeof(EvmInstructions.OpGt))
                    return false;

                // A small constant against a word in its slot reads three limbs and compares one, as cheap as this.
                VVal? constant = va is { Kind: VKind.Const } ? va : vb is { Kind: VKind.Const } ? vb : null;
                VVal? word = ReferenceEquals(constant, va) ? vb : va;
                if (!signed && constant is not null && word is null && constant.U1 == 0 && constant.U2 == 0 && constant.U3 == 0)
                    return false;

                ulong topFlip = signed ? 0 : SignBit;
                _signedScratch0 ??= il.DeclareLocal(typeof(Vector256<long>));
                _signedScratch1 ??= il.DeclareLocal(typeof(Vector256<long>));
                LoadFlipped(top, va, topFlip);
                il.Emit(OpCodes.Stloc, _signedScratch0);
                LoadFlipped(second, vb, topFlip);
                il.Emit(OpCodes.Stloc, _signedScratch1);
                // The opcode compares the top (a) with the second (b): LT and SLT ask a < b, GT and SGT a > b.
                LocalBuilder low = below ? _signedScratch0 : _signedScratch1;
                LocalBuilder high = below ? _signedScratch1 : _signedScratch0;
                // Limbs where low < high, then where low > high; low < high when the first mask is the larger.
                il.Emit(OpCodes.Ldloc, high);
                il.Emit(OpCodes.Ldloc, low);
                il.Emit(OpCodes.Call, VecGreaterThanSigned);
                il.Emit(OpCodes.Call, VecMaskSigned);
                il.Emit(OpCodes.Ldloc, low);
                il.Emit(OpCodes.Ldloc, high);
                il.Emit(OpCodes.Call, VecGreaterThanSigned);
                il.Emit(OpCodes.Call, VecMaskSigned);
                il.Emit(OpCodes.Cgt_Un);
                return true;
            }

            /// <summary>Pushes the word at <paramref name="position"/> with each limb's sign bit flipped, the top limb's only by <paramref name="topFlip"/>, as signed limbs.</summary>
            private void LoadFlipped(int position, VVal? value, ulong topFlip)
            {
                if (value is { Kind: VKind.Const })
                {
                    EmitVecConst(value.U0 ^ SignBit, value.U1 ^ SignBit, value.U2 ^ SignBit, value.U3 ^ topFlip);
                }
                else
                {
                    LoadVec(position, value);
                    EmitVecConst(SignBit, SignBit, SignBit, topFlip);
                    il.Emit(OpCodes.Call, VecXor);
                }

                il.Emit(OpCodes.Call, VecAsInt64);
            }

            /// <summary>An arithmetic, comparison or bitwise opcode over values the segment tracks (<see cref="IlEvmSettings.VectorForms"/>).</summary>
            /// <remarks>
            /// The vector forms read their operands wherever they are and leave their results in vector locals or flags; the
            /// helpers read theirs from the slots, constants aside, and write the second slot.
            /// </remarks>
            private void EmitNativeTracked(IlOp op, ref int delta)
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
                        // ISZERO: of a flag, the flag inverted; of a word, a new flag from one whole-word test.
                        if (value is { Kind: VKind.Bool })
                        {
                            _virtual[x] = VVal.Boolean(value.Flag!, !value.Negated);
                            return;
                        }

                        LocalBuilder flag = NewFlag();
                        LoadVec(x, value);
                        il.Emit(OpCodes.Call, VecZero);
                        il.Emit(OpCodes.Call, VecEqualsAll);
                        il.Emit(OpCodes.Stloc, flag);
                        _virtual.Remove(x);
                        SetValue(x, VVal.Boolean(flag, negated: false));
                        return;
                    }

                    if (op.Operation?.DeclaringType == typeof(EvmInstructions.OpNot))
                    {
                        LoadVec(x, value);
                        il.Emit(OpCodes.Call, VecOnesComplement);
                        il.Emit(OpCodes.Stloc, VecScratch);
                        BindScratch(x);
                        return;
                    }

                    // CLZ and the like: the helper over the slot, in place.
                    InSlotUnlessConstant(x, ref value);
                    BeforeWrite(x, x + 1);
                    Slot(x);
                    il.Emit(OpCodes.Call, Arithmetic(op.Native!));
                    return;
                }

                int top = delta - 1;
                int second = delta - 2;
                VVal? va = Virtual(top);
                VVal? vb = Virtual(second);
                delta--;
                if (va is { Kind: VKind.Const } && vb is { Kind: VKind.Const })
                {
                    _virtual.Remove(top);
                    _virtual[second] = Fold(op, va, vb);
                    return;
                }

                if (op.IsPredicate)
                {
                    LocalBuilder flag = NewFlag();
                    if (!TryEmitVectorComparison(op, va, vb, top, second))
                    {
                        // A small constant against a word in its slot, or a comparison the vector forms do not take: the limb
                        // compare and the helpers read the operands from their slots.
                        InSlotUnlessConstant(top, ref va);
                        InSlotUnlessConstant(second, ref vb);
                        if (TryEmitComparison(op, va, vb, top, second))
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
                    }

                    il.Emit(OpCodes.Stloc, flag);
                    _virtual.Remove(top);
                    _virtual.Remove(second);
                    SetValue(second, VVal.Boolean(flag, negated: false));
                    return;
                }

                if (TryEmitVectorForm(op, va, vb, top, second))
                {
                    _virtual.Remove(top);
                    BindScratch(second);
                    return;
                }

                // The helpers read both operands from their slots, a constant aside, and write the second slot.
                InSlotUnlessConstant(top, ref va);
                InSlotUnlessConstant(second, ref vb);
                _virtual.Remove(top);
                _virtual.Remove(second);
                BeforeWrite(second, second + 1);
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

            /// <summary>
            /// AND, OR and XOR of any two words; SHL and SHR by a constant amount; ADD and SUB of a constant below 2^64, or of
            /// one whose two's complement is: the result left in <see cref="VecScratch"/>, the operands read wherever they are.
            /// </summary>
            private bool TryEmitVectorForm(IlOp op, VVal? va, VVal? vb, int top, int second)
            {
                Type? operation = OperationOf(op);
                MethodInfo? bitwise = operation == typeof(EvmInstructions.OpBitwiseAnd) ? VecAnd
                    : operation == typeof(EvmInstructions.OpBitwiseOr) ? VecOr
                    : operation == typeof(EvmInstructions.OpBitwiseXor) ? VecXor
                    : null;
                if (bitwise is not null)
                {
                    LoadVec(top, va);
                    LoadVec(second, vb);
                    il.Emit(OpCodes.Call, bitwise);
                    il.Emit(OpCodes.Stloc, VecScratch);
                    return true;
                }

                // Arithmetic of two words is left to the helpers: a vector form of ADD and SUB, with each limb's carry moved
                // up a limb, measured slower than the helper call.
                VVal? constant = va is { Kind: VKind.Const } ? va : vb is { Kind: VKind.Const } ? vb : null;
                if (constant is null)
                    return false;

                bool constantOnTop = ReferenceEquals(constant, va);
                int x = constantOnTop ? second : top;
                VVal? vx = constantOnTop ? vb : va;

                // The core's shift operations are internal, so they are told apart by name.
                if (operation?.Name is "OpShr" or "OpShl")
                {
                    // Only a constant amount, on top, shifting the second.
                    if (!constantOnTop)
                        return false;
                    if (constant.U1 != 0 || constant.U2 != 0 || constant.U3 != 0 || constant.U0 >= 256)
                    {
                        il.Emit(OpCodes.Call, VecZero);
                        il.Emit(OpCodes.Stloc, VecScratch);
                        return true;
                    }

                    EmitShiftConstant(second, vb, (int)constant.U0, right: operation.Name == "OpShr");
                    il.Emit(OpCodes.Stloc, VecScratch);
                    return true;
                }

                // MUL by a power of two is a left shift, DIV by one a right shift, MOD by one a mask; by zero each is zero.
                bool mul = operation == typeof(EvmInstructions.OpMul);
                if (mul || ((operation == typeof(EvmInstructions.OpDiv) || operation == typeof(EvmInstructions.OpMod)) && !constantOnTop))
                {
                    if (constant.IsZero)
                    {
                        il.Emit(OpCodes.Call, VecZero);
                        il.Emit(OpCodes.Stloc, VecScratch);
                        return true;
                    }

                    int bit = PowerOfTwo(constant);
                    if (bit < 0)
                        return false;
                    if (operation == typeof(EvmInstructions.OpMod))
                    {
                        LoadVec(x, vx);
                        EmitVecConst(LowBits(bit, 0), LowBits(bit, 1), LowBits(bit, 2), LowBits(bit, 3));
                        il.Emit(OpCodes.Call, VecAnd);
                    }
                    else
                    {
                        EmitShiftConstant(x, vx, bit, right: !mul);
                    }

                    il.Emit(OpCodes.Stloc, VecScratch);
                    return true;
                }

                bool add = operation == typeof(EvmInstructions.OpAdd);
                if (!add && operation != typeof(EvmInstructions.OpSub))
                    return false;
                if (!add && constantOnTop)
                    return false;

                // x + c or x - c; a constant whose two's complement is small adds or subtracts that instead.
                ulong amount;
                bool subtract = !add;
                if (constant.U1 == 0 && constant.U2 == 0 && constant.U3 == 0)
                {
                    amount = constant.U0;
                }
                else if (constant.U1 == ulong.MaxValue && constant.U2 == ulong.MaxValue && constant.U3 == ulong.MaxValue && constant.U0 != 0)
                {
                    amount = 0 - constant.U0;
                    subtract = !subtract;
                }
                else
                {
                    return false;
                }

                LoadVec(x, vx);
                if (amount == 0)
                {
                    il.Emit(OpCodes.Stloc, VecScratch);
                    return true;
                }

                // The low limb changes by the amount in the vector; when it wraps, the limbs above take the carry or the
                // borrow out of line.
                il.Emit(OpCodes.Stloc, VecScratchB);
                il.Emit(OpCodes.Ldloc, VecScratchB);
                EmitVecConst(amount, 0, 0, 0);
                il.Emit(OpCodes.Call, subtract ? VecSubtract : VecAdd);
                il.Emit(OpCodes.Stloc, VecScratch);
                Label wrapped = NewLabel();
                Label back = NewLabel();
                il.Emit(OpCodes.Ldloc, VecScratchB);
                il.Emit(OpCodes.Call, VecToScalar);
                if (!subtract)
                {
                    // Added: wrapped when the new low limb is below the amount. Subtracted: when the old one was.
                    il.Emit(OpCodes.Ldc_I8, (long)amount);
                    il.Emit(OpCodes.Add);
                }

                il.Emit(OpCodes.Ldc_I8, (long)amount);
                il.Emit(OpCodes.Blt_Un, wrapped);
                il.MarkLabel(back);
                AddOutOfLine(() =>
                {
                    il.MarkLabel(wrapped);
                    il.Emit(OpCodes.Ldloc, VecScratch);
                    il.Emit(OpCodes.Call, !subtract ? (_layingOutAhead ? CarryVectorAheadMethod : CarryVectorMethod) : (_layingOutAhead ? BorrowVectorAheadMethod : BorrowVectorMethod));
                    il.Emit(OpCodes.Stloc, VecScratch);
                    il.Emit(OpCodes.Br, back);
                });
                return true;
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
                        il.Emit(OpCodes.Call, add ? (_layingOutAhead ? CarryAheadMethod : CarryMethod) : (_layingOutAhead ? BorrowAheadMethod : BorrowMethod));
                        il.Emit(OpCodes.Br, back);
                    });
                    return true;
                }

                return false;
            }

            private LocalBuilder? _offsetScratch;

            /// <summary>The offset a CALLDATALOAD reads at, which its cold path reads again; <see cref="VecScratch"/> takes its result.</summary>
            private LocalBuilder OffsetScratch => _offsetScratch ??= il.DeclareLocal(typeof(Vector256<ulong>));

            /// <summary>Byte-reverses the vector on the evaluation stack, between the stack's limb order and memory's big-endian bytes.</summary>
            private void EmitByteSwap()
            {
                // The 128-bit halves swapped, then each half's bytes reversed.
                il.Emit(OpCodes.Ldc_I4, 0b01_00_11_10);
                il.Emit(OpCodes.Call, Permute4x64);
                il.Emit(OpCodes.Call, VecAsByte);
                EmitVecConst(0x18191a1b1c1d1e1fUL, 0x1011121314151617UL, 0x08090a0b0c0d0e0fUL, 0x0001020304050607UL);
                il.Emit(OpCodes.Call, VecAsByte);
                il.Emit(OpCodes.Call, ShuffleBytes);
                il.Emit(OpCodes.Call, VecBytesAsUInt64);
            }

            /// <summary>Branches to <paramref name="far"/> unless the word in <paramref name="local"/> is an offset below 2^32.</summary>
            private void BranchUnlessOffset(LocalBuilder local, Label far)
            {
                il.Emit(OpCodes.Ldloc, local);
                EmitVecConst(0xFFFF_FFFF_0000_0000UL, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue);
                il.Emit(OpCodes.Call, TestZ);
                il.Emit(OpCodes.Brfalse, far);
            }

            /// <summary>CALLDATALOAD of a tracked offset: 32 bytes inside the input read into a local; anything else through a helper.</summary>
            private void EmitCallDataLoadTracked(int delta)
            {
                int offset = delta - 1;
                VVal? value = Virtual(offset);
                LocalBuilder at = OffsetScratch;
                Label slow = NewLabel();
                Label back = NewLabel();
                LoadVec(offset, value);
                il.Emit(OpCodes.Stloc, at);
                BranchUnlessOffset(at, slow);
                // offset + 32 <= the input's length, then the 32 bytes there.
                il.Emit(OpCodes.Ldloc, at);
                il.Emit(OpCodes.Call, VecToScalar);
                il.Emit(OpCodes.Ldc_I8, 32L);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldfld, StackInputDataLength);
                il.Emit(OpCodes.Conv_U8);
                il.Emit(OpCodes.Bgt_Un, slow);
                il.Emit(OpCodes.Ldarg_S, ArgStack);
                il.Emit(OpCodes.Ldfld, StackInputData);
                il.Emit(OpCodes.Ldloc, at);
                il.Emit(OpCodes.Call, VecToScalar);
                il.Emit(OpCodes.Conv_I);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Unaligned, (byte)1);
                il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                EmitByteSwap();
                il.Emit(OpCodes.Stloc, VecScratch);
                il.MarkLabel(back);
                BindScratch(offset);
                AddOutOfLine(() =>
                {
                    il.MarkLabel(slow);
                    il.Emit(OpCodes.Ldarg_S, ArgStack);
                    il.Emit(OpCodes.Ldfld, StackInputData);
                    il.Emit(OpCodes.Ldarg_S, ArgStack);
                    il.Emit(OpCodes.Ldfld, StackInputDataLength);
                    il.Emit(OpCodes.Ldloc, at);
                    il.Emit(OpCodes.Call, _layingOutAhead ? CallDataWordAheadMethod : CallDataWordMethod);
                    il.Emit(OpCodes.Stloc, VecScratch);
                    il.Emit(OpCodes.Br, back);
                });
            }

            /// <summary>Pushes the word at <paramref name="position"/> shifted by a constant amount below 256, as one vector.</summary>
            private void EmitShiftConstant(int position, VVal? value, int shift, bool right)
            {
                if (shift == 0)
                {
                    LoadVec(position, value);
                    return;
                }

                int q = shift / 64;
                int r = shift % 64;
                // Every limb of the result from the value's limbs, then all four into one vector.
                for (int limb = 0; limb < 4; limb++)
                {
                    int source = right ? limb + q : limb - q;
                    if (source < 0 || source > 3)
                    {
                        il.Emit(OpCodes.Ldc_I8, 0L);
                        continue;
                    }

                    LoadLimb(position, value, source);
                    if (r == 0)
                        continue;
                    il.Emit(OpCodes.Ldc_I4, r);
                    il.Emit(right ? OpCodes.Shr_Un : OpCodes.Shl);
                    int carrySource = right ? source + 1 : source - 1;
                    if (carrySource is >= 0 and <= 3)
                    {
                        LoadLimb(position, value, carrySource);
                        il.Emit(OpCodes.Ldc_I4, 64 - r);
                        il.Emit(right ? OpCodes.Shl : OpCodes.Shr_Un);
                        il.Emit(OpCodes.Or);
                    }
                }

                il.Emit(OpCodes.Call, VectorCreate);
            }

            /// <summary>Limb <paramref name="limb"/> of 2^<paramref name="bit"/> - 1.</summary>
            private static ulong LowBits(int bit, int limb) =>
                bit >= 64 * (limb + 1) ? ulong.MaxValue : bit <= 64 * limb ? 0 : (1UL << (bit - 64 * limb)) - 1;

            /// <summary>The bit a power-of-two constant has set, or -1 when it has none or more than one.</summary>
            private static int PowerOfTwo(VVal constant)
            {
                ulong[] limbs = [constant.U0, constant.U1, constant.U2, constant.U3];
                int bit = -1;
                for (int limb = 0; limb < 4; limb++)
                {
                    if (limbs[limb] == 0)
                        continue;
                    if (bit >= 0 || (limbs[limb] & (limbs[limb] - 1)) != 0)
                        return -1;
                    bit = limb * 64 + System.Numerics.BitOperations.TrailingZeroCount(limbs[limb]);
                }

                return bit;
            }

            private void EmitMLoad(IlUnit segment, int index, IlOp op, int delta)
            {
                int offset = delta - 1;
                VVal? value = Virtual(offset);
                if (value is not null && (value.Kind != VKind.Const || !value.FitsOffset))
                {
                    Materialize(offset);
                    value = null;
                }

                // The word loaded lands in the offset's slot.
                BeforeWrite(offset, offset + 1);

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
                // The body, on the out-of-line path, turns the word's slot to memory byte order in place: whatever aliases
                // the slot reads it first. Before the operands are looked at, since a spill while resolving may write them.
                BeforeWrite(word, offset + 1);
                VVal? vo = Virtual(offset);
                VVal? vw = Virtual(word);
                if (vw is not null && vw.Kind != VKind.Const)
                {
                    Materialize(word);
                    vw = null;
                }
                if (vo is not null && (vo.Kind != VKind.Const || !vo.FitsOffset))
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
                il.Calls(op.Execute);

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
                il.Emit(OpCodes.Call, _layingOutAhead ? EmbeddedAheadMethod : EmbeddedMethod);
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
                il.Category = IlEvmDiagnostics.Markers ? "single " + (Instruction)unit.Instructions[0].Opcode : "single " + unit.Instructions[0].Op.Kind;
                il.Pc = unit.StartPc;
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
                if (vd is not null && vd.Kind != VKind.Const)
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
                if (vc is { Kind: VKind.Bool })
                {
                    il.Emit(OpCodes.Ldloc, vc.Flag!);
                    il.Emit(vc.Negated ? OpCodes.Brtrue : OpCodes.Brfalse, notTaken);
                }
                else if (vc is not null)
                {
                    // A word in a local, or in the slot an alias names, which nothing above has written.
                    LoadVec(condition, vc);
                    il.Emit(OpCodes.Call, VecZero);
                    il.Emit(OpCodes.Call, VecEqualsAll);
                    il.Emit(OpCodes.Brtrue, notTaken);
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
                if (IlEvmDiagnostics.Markers)
                    il.StoreMarker(0);
                il.Emit(OpCodes.Ldarg_S, ArgVm);
                il.Emit(OpCodes.Ldarg_S, ArgVm);
                if (OpCodeCountField is not null)
                    il.Emit(OpCodes.Ldfld, OpCodeCountField);
                else
                    il.Emit(OpCodes.Call, GetOpCodeCount);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateCounted);
                il.Emit(OpCodes.Sub);
                il.Emit(OpCodes.Add);
                if (OpCodeCountField is not null)
                    il.Emit(OpCodes.Stfld, OpCodeCountField);
                else
                    il.Emit(OpCodes.Call, SetOpCodeCount);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                il.Emit(OpCodes.Stfld, StateCounted);
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
                    il.Calls(op.Execute);
                }
            }

            /// <summary>Polls for cancellation at a taken jump once the opcode count reaches the poll mark, as the dispatch loop does.</summary>
            /// <param name="landedJumpDest">1 where the dispatch loop has counted the landed JUMPDEST before its check (JUMP), 0 where not (JUMPI).</param>
            private void Poll(int landedJumpDest)
            {
                Label slow = NewLabel();
                Label back = NewLabel();
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                if (landedJumpDest != 0)
                {
                    il.Emit(OpCodes.Ldc_I4, landedJumpDest);
                    il.Emit(OpCodes.Add);
                }
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StatePollAt);
                il.Emit(OpCodes.Bge, slow);
                il.MarkLabel(back);

                AddOutOfLine(() =>
                {
                    il.MarkLabel(slow);
                    il.Emit(OpCodes.Ldarg_S, ArgState);
                    il.Emit(OpCodes.Ldarg_S, ArgState);
                    il.Emit(OpCodes.Ldfld, StateOpCodes);
                    il.Emit(OpCodes.Ldc_I4, landedJumpDest + IlPollInterval);
                    il.Emit(OpCodes.Add);
                    il.Emit(OpCodes.Stfld, StatePollAt);
                    il.Emit(OpCodes.Ldarg_S, ArgVm);
                    il.Emit(OpCodes.Call, PollCancellation);
                    il.Emit(OpCodes.Br, back);
                });
            }

            private void AddOps(int count)
            {
                if (count == 0) return;
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldarg_S, ArgState);
                il.Emit(OpCodes.Ldfld, StateOpCodes);
                il.Emit(OpCodes.Ldc_I4, count);
                il.Emit(OpCodes.Add);
                il.Emit(OpCodes.Stfld, StateOpCodes);
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
                if (IlEvmSettings.VectorForms)
                {
                    // One whole-word read, tested against zero.
                    Slot(slot);
                    il.Emit(OpCodes.Ldobj, typeof(Vector256<ulong>));
                    il.Emit(OpCodes.Call, VecZero);
                    il.Emit(OpCodes.Call, VecEqualsAll);
                    return;
                }

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
