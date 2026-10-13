// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

/// <summary>Runs the frames it is offered on compiled code once their contract is hot, and leaves the rest to the interpreter.</summary>
/// <remarks>
/// A frame of code that is not compiled yet is declined with a request for the interpreter's opcode count, which is
/// what makes the code hot. A compiled run that hands back declines from the instruction where it stopped.
/// </remarks>
public sealed class IlEvmFrameRunner : FrameCodeRunner<EthereumGasPolicy>
{
    public static IlEvmFrameRunner Instance { get; } = new();

    private IlEvmFrameRunner() { }

    /// <summary>The timing of the frame the interpreter is running for diagnostics, and whether compiled code handed it back.</summary>
    [ThreadStatic] private static long _interpretedSince;
    [ThreadStatic] private static bool _handedBack;

    /// <summary>The self-check sample the interpreter is finishing on this thread: whose, since when, and what compiled code ran of it.</summary>
    [ThreadStatic] private static IlSelfCheck? _sampleCheck;
    [ThreadStatic] private static IlEvm.IlContract? _sampleContract;
    [ThreadStatic] private static CodeInfo? _sampleCode;
    [ThreadStatic] private static long _sampleStart;
    [ThreadStatic] private static int _sampleCompiledOps;
    [ThreadStatic] private static int _sampleChunkEvents;
    [ThreadStatic] private static bool _sampleInterpreted;
    [ThreadStatic] private static uint _sampleRandom;

    // Optimized from the first call, as are the helpers compiled code calls: a tier-0 copy would slow the frames run
    // before tiering catches up, and the self-check would judge compiled code by them.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, FrameRunFlags flags)
    {
        if (!IlEvmSettings.Enabled)
            return IlEvmDiagnostics.Enabled ? TimeInterpreted(handedBack: false) : Declined;

        IlEvm.IlContract? contract = IlEvm.Find(codeInfo, bodies, out _);
        if (contract is null)
            return IlEvmDiagnostics.Enabled ? TimeInterpreted(handedBack: false) : DeclinedReportingOpcodes;

        bool cancelable = (flags & FrameRunFlags.Cancelable) != 0;
        int verdict = Volatile.Read(ref contract.Check.Verdict);
        if (verdict >= IlSelfCheck.Interpreted && IlEvmSettings.SelfCheck && (verdict == IlSelfCheck.Interpreted || !contract.Check.TryRecheck()))
            return Declined;
        if (verdict == IlSelfCheck.Undecided && IlEvmSettings.SelfCheck && !IlEvmDiagnostics.Enabled &&
            Environment.TickCount64 - contract.CompiledAt >= IlEvmSettings.SelfCheckDelayMs)
            return RunChecked(codeInfo, contract, vm, ref stack, ref gas, ref programCounter, cancelable);
        if (!IlEvmDiagnostics.Enabled)
            return contract.Run(vm, ref stack, ref gas, ref programCounter, cancelable);

        int ops = vm.OpCodeCount;
        long start = Stopwatch.GetTimestamp();
        EvmExceptionType result = contract.Run(vm, ref stack, ref gas, ref programCounter, cancelable);
        long ticks = Stopwatch.GetTimestamp() - start;
        Interlocked.Increment(ref IlEvmDiagnostics.Runs);
        Interlocked.Add(ref IlEvmDiagnostics.RunTicks, ticks);
        IlEvmDiagnostics.ContractStats stats = IlEvmDiagnostics.For(codeInfo, codeInfo.CodeSpan.Length);
        Interlocked.Increment(ref stats.CompiledFrames);
        Interlocked.Add(ref stats.CompiledOps, vm.OpCodeCount - ops);
        Interlocked.Add(ref stats.CompiledTicks, ticks);
        if (result != Declined)
            return result;

        Interlocked.Increment(ref IlEvmDiagnostics.Deopts);
        return TimeInterpreted(handedBack: true);
    }

    /// <summary>Runs a frame of a contract whose self-check has no verdict yet: now and then on the interpreter, timed either way.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static EvmExceptionType RunChecked(CodeInfo codeInfo, IlEvm.IlContract contract, VirtualMachine<EthereumGasPolicy> vm,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, bool cancelable)
    {
        IlSelfCheck check = contract.Check;
        // A random choice rather than every nth frame, which frames that alternate between functions would alias.
        uint random = _sampleRandom == 0 ? (uint)Environment.CurrentManagedThreadId * 2654435761u | 1 : _sampleRandom;
        random ^= random << 13;
        random ^= random >> 17;
        random ^= random << 5;
        _sampleRandom = random;
        if (random % IlSelfCheck.SampleEvery == 0)
        {
            _sampleCheck = check;
            _sampleContract = contract;
            _sampleCode = codeInfo;
            _sampleInterpreted = true;
            _sampleStart = Stopwatch.GetTimestamp();
            return DeclinedReportingOpcodes;
        }

        int chunkEvents = Volatile.Read(ref contract.ChunkEvents);
        int ops = vm.OpCodeCount;
        IlEvm.IlContract.HitColdChunk = false;
        long start = Stopwatch.GetTimestamp();
        EvmExceptionType result = contract.Run(vm, ref stack, ref gas, ref programCounter, cancelable);
        if (result == Declined)
        {
            // A chunk still waiting for the JIT handed it back: the interpreter's part says nothing of compiled code.
            if (IlEvm.IlContract.HitColdChunk)
                return Declined;

            // The interpreter finishes what compiled code handed back, and that counts as compiled code's time.
            _sampleCheck = check;
            _sampleContract = contract;
            _sampleCode = codeInfo;
            _sampleInterpreted = false;
            _sampleStart = start;
            _sampleCompiledOps = vm.OpCodeCount - ops;
            _sampleChunkEvents = chunkEvents;
            return DeclinedReportingOpcodes;
        }

        long ticks = Stopwatch.GetTimestamp() - start;
        if (Volatile.Read(ref contract.ChunkEvents) == chunkEvents)
            check.AddCompiled(ticks, vm.OpCodeCount - ops);
        return result;
    }

    private static void FinishSample(int opCodes)
    {
        long ticks = Stopwatch.GetTimestamp() - _sampleStart;
        IlSelfCheck check = _sampleCheck!;
        if (_sampleInterpreted)
            check.AddInterpreted(ticks, opCodes);
        else if (Volatile.Read(ref _sampleContract!.ChunkEvents) == _sampleChunkEvents)
            check.AddCompiled(ticks, _sampleCompiledOps + opCodes);
        _sampleCheck = null;
        _sampleContract = null;
        _sampleCode = null;
    }

    private static EvmExceptionType TimeInterpreted(bool handedBack)
    {
        _handedBack = handedBack;
        _interpretedSince = Stopwatch.GetTimestamp();
        return DeclinedReportingOpcodes;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override void OnInterpreted(CodeInfo codeInfo, int opCodes)
    {
        if (_sampleCheck is not null)
        {
            if (ReferenceEquals(_sampleCode, codeInfo))
            {
                FinishSample(opCodes);
                return;
            }

            // The frame the sample was taken for never reached here, as when its interpreter run threw: it is dropped.
            _sampleCheck = null;
            _sampleContract = null;
            _sampleCode = null;
        }

        if (IlEvmSettings.Enabled)
            IlEvm.OnInterpreted(codeInfo, opCodes);
        if (!IlEvmDiagnostics.Enabled)
            return;

        long ticks = Stopwatch.GetTimestamp() - _interpretedSince;
        if (_handedBack)
        {
            Interlocked.Add(ref IlEvmDiagnostics.DeoptTicks, ticks);
            return;
        }

        IlEvmDiagnostics.ContractStats stats = IlEvmDiagnostics.For(codeInfo, codeInfo.CodeSpan.Length);
        if (IlEvmSettings.Enabled)
        {
            Interlocked.Increment(ref IlEvmDiagnostics.InterpretedFrames);
            Interlocked.Add(ref IlEvmDiagnostics.InterpretedTicks, ticks);
        }
        else
        {
            Interlocked.Increment(ref IlEvmDiagnostics.PlainFrames);
            Interlocked.Add(ref IlEvmDiagnostics.PlainTicks, ticks);
            Interlocked.Increment(ref stats.PlainFrames);
            Interlocked.Add(ref stats.PlainOps, opCodes);
            Interlocked.Add(ref stats.PlainTicks, ticks);
        }
    }

    /// <summary>Polls cancellation where compiled code reaches a dispatch loop's poll point.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Poll(VirtualMachine<EthereumGasPolicy> vm) => PollCancellation(vm);

    /// <summary>The compiler's per-code slot, shared by every machine that runs the code.</summary>
    internal static ref object? CodeSlot(CodeInfo codeInfo) => ref CodeState(codeInfo);

    /// <summary>The opcode count after which a taken jump polls, as the dispatch loop's.</summary>
    internal static int PollInterval => CancellationPollInterval;
}

/// <summary>Times a compiled contract against the interpreter on its own frames, and gives it back to the interpreter if compiled code is not faster.</summary>
/// <remarks>
/// <para>
/// Whether compiled code wins depends on the code's shape and on the CPU it runs on. A long straight run of body calls
/// has more distinct call sites than the branch predictor and the decoded-instruction cache hold, while the interpreter's
/// few handlers stay hot in them; how many is too many differs from one CPU to the next. So the contract's own frames
/// decide: until a verdict, about one frame in <see cref="SampleEvery"/> runs on the interpreter, and frames are timed
/// on the path they took, pooled per path into samples of at least <see cref="BucketOps"/> opcodes so that short frames
/// measure past the timer's resolution.
/// </para>
/// <para>
/// Each interpreted sample is paired with the compiled sample completed just before it, so both meet the same load on
/// the machine and the compiled one ran warm, and the contract stays compiled only if the median of the pairs' ratios -
/// compiled time per opcode over the interpreter's - is at most <see cref="IlEvmSettings.SelfCheckKeepRatio"/>. A median, so that a pair that met a garbage
/// collection or a preemption does not decide; frames that waited on the JIT for a chunk are left out. After the
/// verdict the timing stops.
/// </para>
/// </remarks>
internal sealed class IlSelfCheck
{
    public const int Undecided = 0;
    public const int Compiled = 1;
    /// <summary>Slower twice: the interpreter runs the contract from now on, and its compiled code is released.</summary>
    public const int Interpreted = 2;
    /// <summary>Slower once: the interpreter runs the contract until <see cref="IlEvmSettings.SelfCheckRecheckMs"/> have passed, then it is checked again.</summary>
    public const int Probation = 3;

    /// <summary>One frame in this many, on average, runs on the interpreter until the verdict.</summary>
    public const int SampleEvery = 16;
    /// <summary>The fewest opcodes a sample pools.</summary>
    public const int BucketOps = 1_024;
    private const int Pairs = 31;
    /// <summary>Pairs after which a verdict that every pair agrees on by a clear margin is taken early.</summary>
    private const int EarlyPairs = 7;
    private const double EarlyCompiled = 0.85;
    private const double EarlyInterpreted = 1.18;

    public int Verdict;
    /// <summary>Compiled time per opcode over the interpreter's, the median of the pairs, at the verdict.</summary>
    public double Ratio;
    /// <summary>Pairs timed so far.</summary>
    public int InterpretedSamples;
    private readonly Bucket _interpretedBucket = new();
    private readonly Bucket _compiledBucket = new();
    private readonly double[] _ratios = new double[Pairs];
    private readonly double[] _interpreted = new double[Pairs];
    private readonly double[] _compiled = new double[Pairs];
    /// <summary>The latest compiled sample, the partner of the next interpreted one, or 0.</summary>
    private double _lastCompiled;

    private sealed class Bucket
    {
        public long Ticks;
        public long Ops;
    }

    private static int _keptCount, _probationCount, _givenBackCount;

    /// <summary>Verdicts so far in the process: kept compiled, put on probation, given back to the interpreter.</summary>
    public static (int Kept, int Probation, int GivenBack) Verdicts => (Volatile.Read(ref _keptCount), Volatile.Read(ref _probationCount), Volatile.Read(ref _givenBackCount));

    /// <summary>Called once, when the verdict gives the contract back to the interpreter.</summary>
    public Action? OnGivenBack;
    /// <summary>Whether the check runs for the second time, after a probation.</summary>
    private bool _second;
    /// <summary>When a contract on probation is checked again, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    private long _recheckAt;

    /// <summary>Starts the second check of a contract on probation once its time has come; false while it has not.</summary>
    public bool TryRecheck()
    {
        if (Environment.TickCount64 < Volatile.Read(ref _recheckAt))
            return false;
        lock (this)
        {
            if (Verdict != Probation)
                return Verdict == Undecided;
            _second = true;
            InterpretedSamples = 0;
            _lastCompiled = 0;
            Interlocked.Exchange(ref _interpretedBucket.Ops, 0);
            Interlocked.Exchange(ref _interpretedBucket.Ticks, 0);
            Interlocked.Exchange(ref _compiledBucket.Ops, 0);
            Interlocked.Exchange(ref _compiledBucket.Ticks, 0);
            Volatile.Write(ref Verdict, Undecided);
            return true;
        }
    }

    public void AddInterpreted(long ticks, int opCodes) => Add(_interpretedBucket, ticks, opCodes, interpreted: true);

    public void AddCompiled(long ticks, int opCodes) => Add(_compiledBucket, ticks, opCodes, interpreted: false);

    private void Add(Bucket bucket, long ticks, int opCodes, bool interpreted)
    {
        if (opCodes <= 0)
            return;
        Interlocked.Add(ref bucket.Ticks, ticks);
        if (Interlocked.Add(ref bucket.Ops, opCodes) < BucketOps)
            return;

        lock (this)
        {
            long ops = Interlocked.Exchange(ref bucket.Ops, 0);
            long pooled = Interlocked.Exchange(ref bucket.Ticks, 0);
            if (ops <= 0 || Verdict != Undecided)
                return;
            // A bucket emptied by another thread a moment ago holds too little to time.
            if (ops < BucketOps / 2)
                return;

            double perOp = Math.Max(pooled, 1) / (double)ops;
            if (!interpreted)
            {
                _lastCompiled = perOp;
                return;
            }

            if (_lastCompiled == 0)
                return;
            _interpreted[InterpretedSamples] = perOp;
            _compiled[InterpretedSamples] = _lastCompiled;
            _ratios[InterpretedSamples++] = _lastCompiled / perOp;
            _lastCompiled = 0;
            int pairs = InterpretedSamples;
            // Every pair clearly one way decides early; anything closer runs to the full count.
            bool early = pairs >= EarlyPairs && (_ratios.AsSpan(0, pairs).IndexOfAnyExceptInRange(0, EarlyCompiled) < 0 ||
                _ratios.AsSpan(0, pairs).IndexOfAnyExceptInRange(EarlyInterpreted, double.MaxValue) < 0);
            if (early || pairs == Pairs)
            {
                double[] sorted = _ratios[..pairs];
                Array.Sort(sorted);
                Ratio = sorted[pairs / 2];
                if (Ratio <= IlEvmSettings.SelfCheckKeepRatio)
                {
                    Interlocked.Increment(ref _keptCount);
                    Volatile.Write(ref Verdict, Compiled);
                }
                else if (!_second)
                {
                    // Slower once: interpreted for a while, then checked again before the code is let go.
                    Interlocked.Increment(ref _probationCount);
                    Volatile.Write(ref _recheckAt, Environment.TickCount64 + IlEvmSettings.SelfCheckRecheckMs);
                    Volatile.Write(ref Verdict, Probation);
                }
                else
                {
                    Interlocked.Increment(ref _givenBackCount);
                    Volatile.Write(ref Verdict, Interpreted);
                    OnGivenBack?.Invoke();
                }
            }
        }
    }

    /// <summary>The pairs so far, in nanoseconds per opcode, for diagnostics.</summary>
    public string Describe()
    {
        lock (this)
        {
            double scale = 1e9 / Stopwatch.Frequency;
            return string.Join(" ", Enumerable.Range(0, InterpretedSamples).Select(i =>
                string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{_interpreted[i] * scale:F2}/{_compiled[i] * scale:F2}")));
        }
    }
}
