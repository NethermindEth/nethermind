// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Threading;
using Nethermind.Core.Container;

namespace Nethermind.IlEvm;

/// <summary>Which rules the production runner follows, one per process.</summary>
public enum IlMode
{
    /// <summary>The startup list and what live promotion adds, every frame part of them compiled; see <see cref="IlSimple"/>.</summary>
    Simple,
    /// <summary>The shipped list and what live profiling promotes, each frame part admitted on its own; see <see cref="IlRuntime"/>.</summary>
    Adaptive,
    /// <summary>Run-time compiles of hot code, and the shipped contracts on every block frame.</summary>
    Legacy,
    /// <summary>A measurement run (<see cref="IlEvmSettings.Stats"/>): the timed runners, on the Legacy rules.</summary>
    Stats,
    /// <summary>A profiling run (<see cref="IlEvmSettings.Profile"/>): every frame counted, nothing compiled.</summary>
    Profile,
}

/// <summary>Switches for the EVM bytecode-to-IL compiler, set from <see cref="IIlEvmConfig"/> when the plugin loads; the environment variables give defaults for tests and the replay harness.</summary>
public static class IlEvmSettings
{
    /// <summary>The kill switch: while off, the runner declines every frame.</summary>
    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            LegacyChanged();
        }
    }

    private static bool _enabled = RuntimeFeature.IsDynamicCodeSupported && Environment.GetEnvironmentVariable("NETHERMIND_ILEVM") != "0";

    /// <summary>Which rules the production runner follows, set by <see cref="IlEvmModule"/> from the config; else <c>NETHERMIND_ILEVM_MODE</c>, else Adaptive, and Legacy with <see cref="IlEvmDiagnostics"/> on, whose counters only Legacy keeps.</summary>
    public static IlMode Mode
    {
        get => _mode;
        set
        {
            _mode = value;
            LegacyChanged();
        }
    }

    private static IlMode _mode = IlEvmDiagnostics.Enabled ? IlMode.Legacy :
        Enum.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MODE"), ignoreCase: true, out IlMode mode) && mode is IlMode.Legacy or IlMode.Simple ? mode : IlMode.Adaptive;

    /// <summary>Which frames feed live profiling, as the config set it; for the exit line and the live profile.</summary>
    public static IlEvmPrefer Prefer { get; set; } = IlEvmPrefer.Rpc;

    /// <summary>Where compiled code runs, as <see cref="IlEvmModule"/> attached the runners: the config's, or All where <see cref="IlEvmPrefer.Blocks"/> needs it.</summary>
    public static IlEvmScope Scope { get; set; } = IlEvmScope.Rpc;

    /// <summary>Whether <see cref="IlEvmPrefer.Blocks"/> overrode a configured Scope of Rpc, which the startup step warns of.</summary>
    public static bool ScopeForcedToAll { get; set; }

    /// <summary>The kinds of read-only query whose environments <see cref="IlEvmModule"/> attached a runner to, for the exit line: every kind with <see cref="IlEvmScope.All"/>.</summary>
    public static ReadOnlyQueryKind Surfaces { get; set; } = ReadOnlyQueryKind.All;

    /// <summary>Adaptive: whether the visit gate admits each part of an admitted contract on its own, rather than every part compiled.</summary>
    public static bool Gate { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_GATE") != "0";

    /// <summary>Adaptive: whether the read-only queries' runner times compiled code against the interpreter, and takes it away where it loses.</summary>
    public static bool QueryCheck { get; set; } = true;

    /// <summary>Adaptive: as <see cref="QueryCheck"/>, for block processing's runner.</summary>
    public static bool BlockCheck { get; set; } = true;

    /// <summary>Adaptive diagnostics: block processing's runner declines every frame, so a run measures the hook alone.</summary>
    public static bool BlockDeclineAll { get; set; }

    /// <summary>The opcodes a contract runs on the interpreter, over all its frames, after which it is compiled: catches few frames running long loops.</summary>
    public static long OpcodeThreshold { get; set; } =
        long.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_OPCODES"), out long opcodes) && opcodes > 0 ? opcodes : 20_000;

    /// <summary>The most compiled contracts kept for reuse by code content, across the code infos that carry them.</summary>
    public static int MaxCachedContracts { get; set; } = 8_192;

    /// <summary>The most IL the contracts kept for reuse by content may hold together, oldest out first: state-override code can be made hot at will. Evicted contracts stay compiled where held.</summary>
    public static long MaxCachedIlBytes { get; set; } =
        long.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MAX_CACHED_IL"), out long maxCachedIl) && maxCachedIl > 0 ? maxCachedIl : 128L << 20;

    /// <summary>The most IL one contract may compile to, bounding its JIT to a second or two at about a millisecond per KiB; past it the contract stays on the interpreter.</summary>
    public static int MaxContractIlBytes { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MAX_CONTRACT_IL"), out int maxContractIl) && maxContractIl > 0 ? maxContractIl : 2 << 20;

    /// <summary>The most IL the compiled contracts alive at once may hold (native code takes about as much again); past it nothing more is compiled until some is collected.</summary>
    public static long MaxLiveIlBytes { get; set; } =
        long.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MAX_LIVE_IL"), out long maxLiveIl) && maxLiveIl > 0 ? maxLiveIl : 256L << 20;

    /// <summary>The most compiles waiting for the background compiler; code that turns hot past it tries again later.</summary>
    public static int MaxPendingCompiles { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MAX_PENDING"), out int maxPending) && maxPending > 0 ? maxPending : 64;

    private static long _liveIl;

    /// <summary>The IL of the compiled contracts not yet collected.</summary>
    public static long LiveIlBytes => Interlocked.Read(ref _liveIl);

    internal static void AddLiveIl(long bytes) => Interlocked.Add(ref _liveIl, bytes);

    /// <summary>Adds <paramref name="bytes"/> only if they fit <see cref="MaxLiveIlBytes"/> once <paramref name="freed"/> are let go of, in one atomic step: two publications that each fit alone cannot both pass.</summary>
    internal static bool TryReserveLiveIl(long bytes, long freed = 0)
    {
        long current = Interlocked.Read(ref _liveIl);
        while (true)
        {
            if (current - freed + bytes > MaxLiveIlBytes)
                return false;
            long seen = Interlocked.CompareExchange(ref _liveIl, current + bytes, current);
            if (seen == current)
                return true;
            current = seen;
        }
    }

    /// <summary>The number of frames a contract runs on the interpreter before it is compiled.</summary>
    public static int Threshold { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_THRESHOLD"), out int threshold) ? threshold : 32;

    /// <summary>EVM instructions per compiled chunk to start from; a chunk that compiles too large is cut smaller.</summary>
    public static int ChunkInstructions { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_CHUNK"), out int chunk) && chunk > 0 ? chunk : 1_400;

    /// <summary>Estimated JIT locals that inlining may add to one chunk, kept under RyuJIT's limit of 1024 locals, past which the rest of the method inlines nothing.</summary>
    public static int ChunkInlineBudget { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_INLINE_BUDGET"), out int budget) && budget > 0 ? budget : 600;

    /// <summary>Cut a contract into chunks along its control flow, a function with the helpers it calls, rather than by code order.</summary>
    public static bool Regions { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_REGIONS") != "0";

    /// <summary>How many copies of units already compiled in another chunk the chunks may hold, relative to the units.</summary>
    public static double ChunkDuplication { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_DUPLICATION"), System.Globalization.CultureInfo.InvariantCulture, out double duplication) && duplication >= 0 ? duplication : 1.0;

    /// <summary>Inline the memory and calldata fast paths rather than call them; off by default, as inlined sites make chunks smaller and colder.</summary>
    public static bool InlineMemory { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_INLINE_MEMORY") == "1";

    /// <summary>Inline the 256-bit arithmetic, comparison and shift helpers into compiled code rather than call them.</summary>
    public static bool InlineArithmetic { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_INLINE_ARITH") == "1";

    /// <summary>Compile the common forms with a constant operand - comparisons, masks, shifts, small additions - straight to limb operations.</summary>
    public static bool DirectArithmetic { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_DIRECT") != "0";

    /// <summary>Keep a segment's words in vector locals on AVX2 hardware, on unless <c>NETHERMIND_ILEVM_VECTOR=0</c>; words reach their slots in one 32-byte store.</summary>
    /// <remarks>A word written in parts and read whole cannot be store-forwarded, costing many times the operation; MLOAD and MSTORE stay helper calls, which measured faster.</remarks>
    public static bool VectorForms { get; set; } =
        System.Runtime.Intrinsics.X86.Avx2.IsSupported && Vector256.IsHardwareAccelerated && Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_VECTOR") != "0";

    /// <summary>
    /// Cold paths hand the words they pass their helpers (an addition's carry, a CALLDATALOAD past the input) by reference to
    /// locals of their own: passed or returned by value, a 32-byte vector goes through a stack copy that the JIT makes the hot
    /// local's own home, which keeps that local in memory throughout the chunk. On unless <c>NETHERMIND_ILEVM_COLD_LOCALS=0</c>.
    /// </summary>
    public static bool ColdLocals { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_COLD_LOCALS") != "0";

    /// <summary>
    /// With the vector forms, two-word ADD and SUB as one vector add or subtract when both words are below 2^63 and a SUB
    /// does not borrow (one test of the operands and the result); out of line the carries or borrows move up a lane in vector
    /// registers. <c>NETHERMIND_ILEVM_FAST_ADD=1</c>.
    /// </summary>
    public static bool FastAddSub { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_FAST_ADD") == "1";

    /// <summary>
    /// Two-word DIV and MOD of a dividend of three 64-bit digits by a divisor of two (2^64 &lt;= divisor &lt; 2^128) divide
    /// twice, skipping the quotient digit that must be zero, where UInt256 divides three times; <c>NETHERMIND_ILEVM_FAST_DIVIDE=1</c>.
    /// </summary>
    public static bool FastDivide { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_FAST_DIVIDE") == "1";

    /// <summary>
    /// With the vector forms, MLOAD reads in place, with no call, from a window of the memory a chunk keeps in locals (its start
    /// and the end below which a word is active and initialized): asked for on a miss, dropped after any body that may touch
    /// memory; <c>NETHERMIND_ILEVM_MEMORY_WINDOW=1</c>.
    /// </summary>
    public static bool MemoryWindow { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_MEMORY_WINDOW") == "1";

    /// <summary>
    /// With <see cref="MemoryWindow"/>, MSTORE at a constant offset overwrites a word inside the window in place, with no call;
    /// <c>NETHERMIND_ILEVM_WINDOW_STORES=1</c>.
    /// </summary>
    public static bool WindowStores { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_WINDOW_STORES") == "1";

    /// <summary>Let the JIT inline the bodies compiled code runs directly (CALLDATALOAD and the like) rather than call them out of line.</summary>
    public static bool InlineBodies { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_INLINE_BODIES") != "0";

    /// <summary>The most body call sites of one contract the JIT may inline, as each costs it a few hundred IL instructions' worth; the rest call the body.</summary>
    public static int InlineBodyBudget { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_INLINE_BODY_BUDGET"), out int inlineBodyBudget) && inlineBodyBudget >= 0 ? inlineBodyBudget : 256;

    /// <summary>Entries a chunk of a laid-out contract waits for before it is compiled; the interpreter runs it until then.</summary>
    public static int ChunkThreshold { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_CHUNK_THRESHOLD"), out int chunkThreshold) && chunkThreshold > 0 ? chunkThreshold : 2;

    /// <summary>Compile every chunk once a contract is laid out, rather than each when frames reach it.</summary>
    public static bool EagerChunks { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_EAGER_CHUNKS") == "1";

    /// <summary>Interpreted opcodes per second a contract must also run before it is compiled, with a <see cref="Threshold"/> above 1; 0 for none. Code run rarely meets its compiled code cold, where the interpreter's hot handlers win.</summary>
    public static int HotOpcodesPerSecond { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_HOT_RATE"), out int hotRate) && hotRate >= 0 ? hotRate : 50_000;

    /// <summary>Legacy only: compile on the thread that crosses the threshold instead of in the background.</summary>
    public static bool CompileSynchronously { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SYNC") == "1";

    /// <summary>Legacy only: the threads that run the background compiles, 1 unless <c>NETHERMIND_ILEVM_COMPILE_THREADS</c> says more; a change applies from the next queued compile.</summary>
    public static int CompileThreads { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_COMPILE_THREADS"), out int compileThreads) && compileThreads >= 1 ? compileThreads : 1;

    /// <summary>Legacy only: compile first what the most interpreted opcodes wait on, the oldest of equals; <c>NETHERMIND_ILEVM_COMPILE_ORDER=fifo</c> takes the oldest.</summary>
    /// <remarks>A layout waits by the opcodes its code ran since it was queued, a chunk by the frames handed back at it times its contract's opcodes per part, so both rank together.</remarks>
    public static bool CompileOrderHot { get; set; } =
        !string.Equals(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_COMPILE_ORDER"), "fifo", StringComparison.OrdinalIgnoreCase);

    /// <summary>Legacy only: with <c>NETHERMIND_ILEVM_ENTRY_CHUNK=1</c> a run-time layout JITs its entry chunk before it is published, so its first frames do not hand back there.</summary>
    public static bool EntryChunk { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_ENTRY_CHUNK") == "1";

    /// <summary>Time each compiled contract against the interpreter on its own frames, and give back one compiled code does not speed up (<see cref="IlSelfCheck"/>).</summary>
    public static bool SelfCheck
    {
        get => _selfCheck;
        set
        {
            _selfCheck = value;
            LegacyChanged();
        }
    }

    private static bool _selfCheck = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK") != "0";

    /// <summary>How long after a contract is compiled its self-check starts, so the helpers it calls can tier up first; until then compiled code runs every frame, untimed.</summary>
    public static int SelfCheckDelayMs { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_DELAY_MS"), out int checkDelayMs) && checkDelayMs >= 0 ? checkDelayMs : 5_000;

    /// <summary>Legacy only: with <c>NETHERMIND_ILEVM_SELF_CHECK_COLD_RESTART=1</c> a checked frame that reaches a cold chunk restarts the check, dropping its pairs.</summary>
    /// <remarks>Cold-chunk frames go untimed, so while many chunks are cold only frames staying in compiled chunks are sampled; the restart keeps verdicts off those pairs.</remarks>
    public static bool SelfCheckColdRestart { get; set; } = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_COLD_RESTART") == "1";

    /// <summary>
    /// Legacy only, off (0) unless <c>NETHERMIND_ILEVM_SHORT_PARTS</c> sets it: the fewest opcodes a compiled contract's parts
    /// must run on average, over the first <see cref="ShortPartsSample"/> compiled parts its query runners run, for compiled
    /// code to keep running it. A contract below goes to the interpreter for good: its frame parts are too short for
    /// compiled code to win where the code meets caches other contracts have used since, as on a node replaying blocks.
    /// </summary>
    /// <remarks>The parts are measured only on the runners' slow path, before a contract's parts are decided: the entry is not touched.</remarks>
    public static int ShortParts
    {
        get => _shortParts;
        set
        {
            _shortParts = Math.Max(0, value);
            LegacyChanged();
        }
    }

    private static int _shortParts =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SHORT_PARTS"), out int shortParts) && shortParts > 0 ? shortParts : 0;

    /// <summary>The compiled parts the short-part rule (<see cref="ShortParts"/>) measures a contract on, <c>NETHERMIND_ILEVM_SHORT_PARTS_SAMPLE</c> or 64.</summary>
    public static int ShortPartsSample { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SHORT_PARTS_SAMPLE"), out int shortPartsSample) && shortPartsSample > 0 ? shortPartsSample : 64;

    private static int _shortContracts, _longContracts;

    /// <summary>The contracts the short-part rule gave to the interpreter, and those it kept compiled.</summary>
    public static (int Short, int Long) ShortPartVerdicts => (Volatile.Read(ref _shortContracts), Volatile.Read(ref _longContracts));

    internal static void CountShortPartVerdict(bool isShort) => Interlocked.Increment(ref isShort ? ref _shortContracts : ref _longContracts);

    /// <summary>How long a contract the self-check first finds slower runs on the interpreter before the second check that can release it; the first may have met tier-0 helpers or a burst of load.</summary>
    public static int SelfCheckRecheckMs { get; set; } =
        int.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_RECHECK_MS"), out int recheckMs) && recheckMs >= 0 ? recheckMs : 30_000;

    /// <summary>Compiled code must run a contract's opcodes in at most this share of the interpreter's time to keep it: 1.15, or <c>NETHERMIND_ILEVM_SELF_CHECK_RATIO</c>; a margin, as state-bound code measures near 1 either way.</summary>
    public static double SelfCheckKeepRatio { get; set; } =
        double.TryParse(Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_RATIO"), System.Globalization.CultureInfo.InvariantCulture, out double keepRatio) && keepRatio > 0 ? keepRatio : 1.15;

    /// <summary>For this long after the first frame, while tier-up and first calls skew timings, a contract the self-check finds slower is re-checked after <see cref="SelfCheckEarlyRecheckMs"/> and never released.</summary>
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
        catch (Exception exception)
        {
            // No start time on this platform: the window counts from the first use of the settings, which is plugin load.
            RecordCaught("process start time", exception);
            return Environment.TickCount64;
        }
    }

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

    internal static bool InWarmUpAt(long now)
    {
        long first = Volatile.Read(ref _firstFrameAt);
        return first == 0 || now - first < SelfCheckWarmUpMs;
    }

    /// <summary>Count every contract's frames and interpreted opcodes and compile nothing; <see cref="IlEvmProfile"/> logs the busiest at shutdown.</summary>
    public static bool Profile
    {
        get => _profile;
        set
        {
            _profile = value;
            LegacyChanged();
        }
    }

    private static bool _profile;

    /// <summary>Time every contract's frame parts by the path each took, with no self-check; <see cref="IlEvmStats"/> logs the totals at shutdown.</summary>
    /// <remarks>The module attaches <see cref="IlEvmTimedFrameRunner"/>; block frames run only shipped contracts compiled, and the counts are kept until shutdown.</remarks>
    public static bool Stats { get; set; }

    /// <summary>Whether block processing (Scope=All) compiles contracts outside the shipped list once they get hot, and runs them; off, it runs only the shipped contracts compiled, none a query compiled at run time.</summary>
    public static bool BlockRuntimeCompile
    {
        get => _blockRuntimeCompile;
        set
        {
            _blockRuntimeCompile = value;
            LegacyChanged();
        }
    }

    private static bool _blockRuntimeCompile;

    /// <summary>Whether block processing (Scope=All) runs the self-check on compiled contracts.</summary>
    public static bool BlockSelfCheck
    {
        get => _blockSelfCheck;
        set
        {
            _blockSelfCheck = value;
            LegacyChanged();
        }
    }

    private static bool _blockSelfCheck;

    /// <summary>On Linux, run the background compiler under SCHED_IDLE, so it takes only CPU time nothing else wants. Off unless <c>NETHERMIND_ILEVM_COMPILE_IDLE=1</c>.</summary>
    /// <remarks>Off by default: threads inherit their creator's scheduling class, so a tiering worker the compiler thread starts would run idle too.</remarks>
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

    private static long _caught;

    /// <summary>Exceptions the plugin caught and went on from, by place, type and message's first line: none is expected, so each is kept for the exit line and the tests.</summary>
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> CaughtByKind = new();

    /// <summary>The exceptions <see cref="RecordCaught"/> recorded.</summary>
    public static long Caught => Interlocked.Read(ref _caught);

    internal static void RecordCaught(string where, Exception exception)
    {
        Interlocked.Increment(ref _caught);
        // A bounded table: past 64 kinds only the count grows.
        string kind = $"{where}: {exception.GetType().Name}: {exception.Message.Split('\n')[0]}";
        if (CaughtByKind.Count < 64 || CaughtByKind.ContainsKey(kind))
            CaughtByKind.AddOrUpdate(kind.Length > 200 ? kind[..200] : kind, 1, static (_, v) => v + 1);
    }

    /// <summary>The caught exceptions for an exit line: empty for none.</summary>
    public static string DescribeCaught() =>
        Caught == 0 ? "" : $"; {Caught} exceptions caught and gone on from: " + string.Join(", ", CaughtByKind.Select(static p => $"{p.Value}x {p.Key}"));

    internal static void CountFailed(string? reason)
    {
        Interlocked.Increment(ref _failed);
        string kind = reason is null ? "unknown" : System.Text.RegularExpressions.Regex.Replace(reason.Split('\n')[0], "[0-9]+", "#");
        FailuresByReason.AddOrUpdate(kind.Length > 160 ? kind[..160] : kind, 1, static (_, v) => v + 1);
    }

    /// <summary>
    /// Legacy: whether the query runners act on their code states' decisions (<see cref="IlEvm.IlCodeState.Decided"/>),
    /// on unless <c>NETHERMIND_ILEVM_DECIDED_ENTRY=0</c>; off, every part goes through the lookup, as before decisions,
    /// for A/B runs of the entry within one build.
    /// </summary>
    public static bool DecidedEntry
    {
        get => _decidedEntry;
        set
        {
            _decidedEntry = value;
            LegacyChanged();
        }
    }

    private static bool _decidedEntry = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_DECIDED_ENTRY") != "0";

    private static int _legacyEpochs;

    /// <summary>
    /// What the Legacy rules' decisions kept in code states (<see cref="IlEvm.IlCodeState.Decided"/>) were taken under:
    /// positive while the query runners may act on them, that is under the Legacy rules with the kill switch and
    /// <see cref="DecidedEntry"/> on and neither profiling nor diagnostics, and -1 otherwise. A change of any of those
    /// switches or of <see cref="SelfCheck"/>, <see cref="BlockRuntimeCompile"/> or <see cref="BlockSelfCheck"/> moves it to
    /// a value never used before, which voids every decision taken under the old one.
    /// </summary>
    /// <remarks>Last of the initializers, so that the switches it reads have theirs.</remarks>
    internal static int LegacyEpoch = NextLegacyEpoch();

    private static int NextLegacyEpoch() =>
        _enabled && _decidedEntry && _mode == IlMode.Legacy && !_profile && !IlEvmDiagnostics.Enabled ? Interlocked.Increment(ref _legacyEpochs) : -1;

    private static void LegacyChanged() => Volatile.Write(ref LegacyEpoch, NextLegacyEpoch());
}
