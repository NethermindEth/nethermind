// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

/// <summary>Runs the frame parts it is offered on compiled code where the rules say, and leaves the rest to the interpreter.</summary>
/// <remarks>
/// Code not compiled yet is declined with a request for its opcode count, which makes it hot.
/// A compiled run that hands back declines from the instruction where it stopped. One instance per <see cref="IlEvmSurface"/>.
/// </remarks>
public sealed class IlEvmFrameRunner : FrameCodeRunner<EthereumGasPolicy>
{
    /// <summary>The read-only call environments' runner (<see cref="IlEvmSurface.Call"/>); under Legacy it compiles hot code at run time and self-checks it.</summary>
    public static IlEvmFrameRunner Instance { get; } = new(IlEvmSurface.Call);

    /// <summary>As <see cref="Instance"/>, for the replay and traced-call environments (<see cref="IlEvmSurface.Trace"/>).</summary>
    public static IlEvmFrameRunner TraceInstance { get; } = new(IlEvmSurface.Trace);

    /// <summary>As <see cref="Instance"/>, for the simulation environments (<see cref="IlEvmSurface.Simulate"/>).</summary>
    public static IlEvmFrameRunner SimulateInstance { get; } = new(IlEvmSurface.Simulate);

    /// <summary>
    /// Block processing's runner (Scope=All); under Legacy it compiles or self-checks at run time only as
    /// <see cref="IlEvmSettings.BlockRuntimeCompile"/> and <see cref="IlEvmSettings.BlockSelfCheck"/> say, and without
    /// run-time compiles it runs only the shipped contracts compiled, none a query compiled at run time.
    /// </summary>
    public static IlEvmFrameRunner Block { get; } = new(IlEvmSurface.Blocks);

    public static IlEvmFrameRunner For(IlEvmSurface surface) => surface switch
    {
        IlEvmSurface.Call => Instance,
        IlEvmSurface.Trace => TraceInstance,
        IlEvmSurface.Simulate => SimulateInstance,
        _ => Block,
    };

    private readonly bool _block;

    /// <summary>The environments the instance serves, which its parts are counted under.</summary>
    public IlEvmSurface Surface { get; }

    private IlEvmFrameRunner(IlEvmSurface surface)
    {
        Surface = surface;
        _block = surface == IlEvmSurface.Blocks;
    }

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

    /// <summary>The core's offer of a frame part, counted under the runner's surface (<see cref="IlSurfaceCounts"/>), then run as the rules say.</summary>
    /// <remarks>
    /// Under the Legacy rules a query runner's part is mostly decided by its code state alone (<see cref="IlEvm.IlCodeState.Decided"/>):
    /// compiled code that nothing but the settings can take off the code runs at once, and the parts of code given back or
    /// on probation go to the interpreter at once, as do those of code with nothing compiled, but for the one in
    /// <see cref="IlEvmSettings.HotnessSampling"/> drawn for counting. The rest go out of line. Code states are shared by
    /// every runner, and the decisions are the query runners' alone: block processing's runner neither acts on them nor
    /// takes them, so its own rules (<see cref="IlEvmSettings.BlockRuntimeCompile"/>, <see cref="IlEvmSettings.BlockSelfCheck"/>)
    /// judge every part it is offered. With the temperature gate on (<see cref="IlTemperature"/>), the trace runner first takes
    /// its own verdict on the code (<see cref="IlEvm.IlCodeState.TraceGate"/>): cold sends the part to the interpreter, hot
    /// goes on to the shared decisions, none goes out of line; and about one of its parts in
    /// <see cref="IlTemperature.SampleEvery"/> per thread is a sample, out of line before anything else (<see cref="RunSampled"/>).
    /// Every runner pays one compare of the thread's part counter for the samples, which only the trace runner's ever reach.
    /// </remarks>
    // Optimized from the first call, as are the helpers compiled code calls: a tier-0 copy would slow the frames run
    // before tiering catches up, and the self-check would judge compiled code by them.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        long[] counts = IlSurfaceCounts.OfThisThread();
        int surface = (int)Surface;
        // A temperature sample is due: about one part in IlTemperature.SampleEvery while the gate is on, none while off.
        if (++counts[IlSurfaceCounts.Offered + surface] >= counts[IlSurfaceCounts.SampleAt + surface])
            return RunSampled(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags, counts, surface);
        bool drawn = false;
        if (!_block && CodeState(codeInfo) is IlEvm.IlCodeState state)
        {
            // The trace runner's own verdict first, kept apart from the shared decisions: cold is the interpreter's, none is
            // judged out of line. With the gate off it is 0, as the epoch is, and every trace part goes on to them.
            int gateEpoch;
            if (surface == (int)IlEvmSurface.Trace && state.TraceGate != (gateEpoch = IlTemperature.GateEpoch))
            {
                if (state.TraceGate == ~gateEpoch)
                    return Declined;
            }
            else if (Volatile.Read(ref state.DecidedEpoch) == IlEvmSettings.LegacyEpoch)
            {
                IlEvm.IlContract? decided = state.Decided;
                if (decided is null)
                {
                    // Nothing compiled when decided, and nothing since: the part is drawn for counting as the lookup would draw it.
                    if (state.Contract is null && state.Shipped is null)
                    {
                        if (IlEvm.SkipsCount())
                            return Declined;
                        drawn = true;
                    }
                }
                else if (ReferenceEquals(decided.Bodies, bodies))
                {
                    long until = state.DecidedUntil;
                    if (until == 0)
                    {
                        int before = vm.OpCodeCount;
                        EvmExceptionType result = decided.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, (flags & FrameRunFlags.Cancelable) != 0);
                        if (vm.OpCodeCount != before)
                            counts[IlSurfaceCounts.Compiled + surface]++;
                        return result;
                    }

                    // Given back, or on probation until a time not come yet.
                    if (until == long.MaxValue || Environment.TickCount64 < until)
                        return Declined;
                }
            }
        }

        return RunCounted(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags, counts, surface, drawn);
    }

    /// <summary>A part its code state does not decide, run as the rules say; counted as compiled if compiled code ran any of it.</summary>
    /// <param name="drawn">The entry drew the part for counting toward hotness already.</param>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private EvmExceptionType RunCounted(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies, scoped ref EvmStack stack,
        scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags, long[] counts, int surface, bool drawn)
    {
        int before = vm.OpCodeCount;
        EvmExceptionType result = RunLegacy(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags, drawn);
        // Compiled when the opcode count moved: only compiled code moves it before the interpreter takes a part over, and
        // a hand-back before its first opcode moves nothing.
        if (vm.OpCodeCount != before)
            counts[IlSurfaceCounts.Compiled + surface]++;
        return result;
    }

    /// <summary>A temperature sample of a trace part (<see cref="IlTemperature"/>): the part runs as the rules say, and its opcodes count toward its code's temperature.</summary>
    /// <remarks>
    /// The opcodes compiled code ran are counted here; a part the interpreter takes, or finishes, reports its opcodes
    /// (<see cref="OnInterpreted"/>), which complete the sample. A part the rules declined without wanting a report is
    /// declined with one all the same, and the report counts for the sample alone.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private EvmExceptionType RunSampled(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies, scoped ref EvmStack stack,
        scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags, long[] counts, int surface)
    {
        if (!IlTemperature.On || Surface != IlEvmSurface.Trace)
        {
            // The gate was turned off since this thread's clock was set, or the part is not a trace's: the clock stops.
            counts[IlSurfaceCounts.SampleAt + surface] = long.MaxValue;
            return RunCounted(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags, counts, surface, drawn: false);
        }

        counts[IlSurfaceCounts.SampleAt + surface] = counts[IlSurfaceCounts.Offered + surface] + IlTemperature.NextGap();
        IlTemperature.Roll();
        int before = vm.OpCodeCount;
        EvmExceptionType result = RunLegacy(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags, drawn: false);
        int compiledOps = vm.OpCodeCount - before;
        if (compiledOps != 0)
            counts[IlSurfaceCounts.Compiled + surface]++;
        // Initcode and profiling keep no code state of the plugin's: nothing to count toward.
        if (CodeState(codeInfo) is not IlEvm.IlCodeState state)
            return result;
        if (result == Declined)
        {
            IlTemperature.Mark(codeInfo, state, compiledOps, hotness: false);
            return DeclinedReportingOpcodes;
        }

        if (result == DeclinedReportingOpcodes)
        {
            IlTemperature.Mark(codeInfo, state, compiledOps, hotness: true);
            return result;
        }

        IlTemperature.Add(state, compiledOps);
        return result;
    }

    /// <summary>The Legacy rules' run of a part its code state does not decide, and the profiling and diagnostics runs'.</summary>
    /// <remarks>
    /// Under the Legacy rules the query runners also decide what the entry does with the code's next parts (<see cref="Decide"/>).
    /// Block processing's runner runs only the shipped contracts compiled unless <see cref="IlEvmSettings.BlockRuntimeCompile"/>
    /// is on, never one a query compiled at run time, and decides nothing.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private EvmExceptionType RunLegacy(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags, bool drawn)
    {
        if (!IlEvmSettings.Enabled)
            return IlEvmDiagnostics.Enabled ? TimeInterpreted(handedBack: false) : Declined;
        // Initcode is never compiled: it is never in the code database, so never shipped, and a
        // call without a recipient brings any it likes, with a new code info each time that its own frames, offered again on
        // every resume, would make hot. Nothing is counted for it, and its code state is never made.
        if (vm.VmState.ExecutionType.IsAnyCreate())
            return Declined;
        if (IlEvmSettings.Profile)
        {
            // A frame is offered again on each resume: counted on entry, its opcodes on every part.
            IlEvmProfile.Frame(codeInfo, entry: programCounter == 0);
            return DeclinedReportingOpcodes;
        }

        // Read before what a decision rests on: a change of the settings meanwhile leaves the decision void, not wrong.
        int epoch = IlEvmSettings.LegacyEpoch;
        // The temperature gate judges the trace runner's parts only; its verdicts are taken under its own epoch.
        int gateEpoch = Surface == IlEvmSurface.Trace ? IlTemperature.GateEpoch : 0;
        IlEvm.IlContract? contract;
        // The code state a query runner keeps its decisions in; none for block processing, which takes no decisions.
        IlEvm.IlCodeState? state;
        bool count = false;
        if (_block && !IlEvmSettings.BlockRuntimeCompile)
        {
            contract = IlEvm.FindShippedOnly(codeInfo, bodies);
            state = null;
        }
        else
        {
            contract = IlEvm.Find(codeInfo, bodies, out IlEvm.IlCodeState found, out count, drawn, _block ? null : vm, gated: gateEpoch != 0);
            state = _block ? null : found;
        }

        // A trace verdict: code with nothing compiled has nothing to gate, and one taken with the gate off is 0, as its epoch.
        if (state is not null && Surface == IlEvmSurface.Trace && (contract is null || gateEpoch == 0) && state.TraceGate != gateEpoch)
            state.TraceGate = gateEpoch;

        if (contract is null)
        {
            // Code that has been looked up and counted twice with nothing compiled: the entry draws its parts from now on.
            if (epoch > 0 && state is { Contract: null, Shipped: null, FailedFor: null, PrecompileChecked: true, Executions: >= 2 })
                Decide(state, null, 0, epoch);
            return IlEvmDiagnostics.Enabled ? TimeInterpreted(handedBack: false) : count ? DeclinedReportingOpcodes : Declined;
        }

        bool cancelable = (flags & FrameRunFlags.Cancelable) != 0;
        IlEvmSettings.SawFrame();
        // The temperature gate, on the trace runner: a contract that is not hot in traces goes to the interpreter, and so do
        // its next trace parts until the gate's next window; a hot one runs as the rules below say. The verdict stays in the
        // trace runner's own field: the decisions the other runners share are not touched.
        if (state is not null && gateEpoch != 0)
        {
            long[] verdicts = IlSurfaceCounts.OfThisThread();
            bool hot = IlTemperature.IsHot(codeInfo, state);
            verdicts[hot ? IlSurfaceCounts.HotDecisions : IlSurfaceCounts.ColdDecisions]++;
            int trace = hot ? gateEpoch : ~gateEpoch;
            if (state.TraceGate != trace)
                state.TraceGate = trace;
            if (!hot)
                return Declined;
        }

        int verdict = Volatile.Read(ref contract.Check.Verdict);
        if (IlEvmSettings.SelfCheck && (!_block || IlEvmSettings.BlockSelfCheck))
        {
            if (verdict == IlSelfCheck.Interpreted)
            {
                if (epoch > 0 && state is not null)
                    Decide(state, contract, long.MaxValue, epoch);
                return Declined;
            }

            if (verdict == IlSelfCheck.Probation && !contract.Check.TryRecheck())
            {
                if (epoch > 0 && state is not null)
                    Decide(state, contract, Math.Max(1, contract.Check.RecheckAt), epoch);
                return Declined;
            }

            if (verdict == IlSelfCheck.Undecided && !IlEvmDiagnostics.Enabled &&
                Environment.TickCount64 - contract.CompiledAt >= IlEvmSettings.SelfCheckDelayMs)
                return RunChecked(codeInfo, contract, vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
        }

        // The short-part rule: a contract whose parts it found too short is the interpreter's for good, and one it still
        // measures runs compiled with nothing decided, so that its next parts come back here to be measured.
        int shortParts = IlEvmSettings.ShortParts;
        if (shortParts > 0 && state is not null && !IlEvmDiagnostics.Enabled)
        {
            int partsVerdict = Volatile.Read(ref contract.PartsVerdict);
            if (partsVerdict == IlEvm.IlContract.PartsShort)
            {
                if (epoch > 0)
                    Decide(state, contract, long.MaxValue, epoch);
                return Declined;
            }

            if (partsVerdict == 0)
            {
                int opsBefore = vm.OpCodeCount;
                EvmExceptionType measured = contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
                contract.SamplePart(vm.OpCodeCount - opsBefore, shortParts, IlEvmSettings.ShortPartsSample);
                return measured;
            }
        }

        // Compiled code runs the part, and the code's next parts with no lookup once nothing but the settings can change
        // that: a kept contract's verdict is final, and with the self-check off every contract found runs.
        if (epoch > 0 && state is not null && (verdict == IlSelfCheck.Compiled || !IlEvmSettings.SelfCheck))
            Decide(state, contract, 0, epoch);
        // Without its own self-check the block runner ignores the query runners' verdicts: those time calls, not blocks. A
        // run-time compiled contract that was given back has no code left, and its frames hand back at once.
        if (!IlEvmDiagnostics.Enabled)
            return contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);

        int ops = vm.OpCodeCount;
        long start = Stopwatch.GetTimestamp();
        EvmExceptionType result = contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
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

    /// <summary>Keeps in <paramref name="state"/> what the entry does with the code's next parts under <paramref name="epoch"/> (<see cref="IlEvm.IlCodeState.Decided"/>).</summary>
    /// <remarks>Unchanged decisions are not written again: code that many threads run keeps its state's line shared.</remarks>
    private static void Decide(IlEvm.IlCodeState state, IlEvm.IlContract? contract, long until, int epoch)
    {
        if (Volatile.Read(ref state.DecidedEpoch) == epoch && ReferenceEquals(state.Decided, contract) && state.DecidedUntil == until)
            return;
        state.Decided = contract;
        state.DecidedUntil = until;
        Volatile.Write(ref state.DecidedEpoch, epoch);
    }

    /// <summary>Runs a frame of a contract whose self-check has no verdict yet: now and then on the interpreter, timed either way.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static EvmExceptionType RunChecked(CodeInfo codeInfo, IlEvm.IlContract contract, VirtualMachine<EthereumGasPolicy> vm,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, bool cancelable)
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

        // Compiled frames are timed one in TimeCompiledEvery: two timestamps and the shared buckets cost more than many frames run.
        if ((random >> 16) % IlSelfCheck.TimeCompiledEvery != 0)
        {
            if (!IlEvmSettings.SelfCheckColdRestart)
                return contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
            IlEvm.IlContract.HitColdChunk = false;
            EvmExceptionType untimed = contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
            if (untimed == Declined && IlEvm.IlContract.HitColdChunk)
                check.Restart();
            return untimed;
        }

        int chunkEvents = Volatile.Read(ref contract.ChunkEvents);
        int ops = vm.OpCodeCount;
        IlEvm.IlContract.HitColdChunk = false;
        long start = Stopwatch.GetTimestamp();
        EvmExceptionType result = contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
        if (result == Declined)
        {
            // A chunk still waiting for the JIT handed it back: the interpreter's part says nothing of compiled code. With
            // the cold restart, neither do the pairs so far, which timed only frames that stayed within compiled chunks.
            if (IlEvm.IlContract.HitColdChunk)
            {
                if (IlEvmSettings.SelfCheckColdRestart)
                    check.Restart();
                return Declined;
            }

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
        OnInterpretedLegacy(codeInfo, opCodes);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void OnInterpretedLegacy(CodeInfo codeInfo, int opCodes)
    {
        // A temperature sample's interpreted opcodes; a part declined only to be measured counts nowhere else.
        if (IlTemperature.Finish(codeInfo, opCodes))
            return;
        if (IlEvmSettings.Profile)
        {
            IlEvmProfile.Ops(codeInfo, opCodes);
            return;
        }

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
/// Whether compiled code wins depends on the code's shape and the CPU: a long straight run of body calls can outgrow the
/// branch predictor and decoded-instruction cache, while the interpreter's few handlers stay hot. So, until a verdict,
/// about one frame in <see cref="SampleEvery"/> runs on the interpreter, and frames are pooled per path into samples of at
/// least <see cref="BucketOps"/> opcodes, so that short frames measure past the timer's resolution. Each interpreted sample
/// is paired with the compiled one completed just before it, so both meet the same load and the compiled one ran warm; the
/// contract stays compiled only if the median pair ratio (compiled time per opcode over the interpreter's) is at most
/// <see cref="IlEvmSettings.SelfCheckKeepRatio"/>, a median so that a pair that met a garbage collection or a preemption
/// does not decide. Frames that waited on the JIT for a chunk are left out.
/// </remarks>
internal sealed class IlSelfCheck
{
    public const int Undecided = 0;
    public const int Compiled = 1;
    /// <summary>Slower twice: the interpreter runs the contract from now on, and its compiled code is released.</summary>
    public const int Interpreted = 2;
    /// <summary>Slower once: the interpreter runs the contract until <see cref="IlEvmSettings.SelfCheckRecheckMs"/> have passed, then it is checked again.</summary>
    public const int Probation = 3;

    public const int SampleEvery = 16;
    public const int TimeCompiledEvery = 4;
    public const int BucketOps = 1_024;
    private const int Pairs = 31;
    private const int EarlyPairs = 7;
    private const double EarlyCompiled = 0.85;
    private const double EarlyInterpreted = 1.18;

    public int Verdict;
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

    /// <summary>Print each verdict with its pairs to the console, with <c>NETHERMIND_ILEVM_SELF_CHECK_TRACE=1</c>.</summary>
    private static readonly bool Trace = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_TRACE") == "1";

    public string? Name;

    public static (int Kept, int Probation, int GivenBack) Verdicts => (Volatile.Read(ref _keptCount), Volatile.Read(ref _probationCount), Volatile.Read(ref _givenBackCount));

    /// <summary>Called once, when the verdict gives the contract back to the interpreter.</summary>
    public Action? OnGivenBack;
    /// <summary>Whether the check runs for the second time, after a probation.</summary>
    private bool _second;
    /// <summary>When a contract on probation is checked again, in <see cref="Environment.TickCount64"/> milliseconds.</summary>
    private long _recheckAt;

    /// <inheritdoc cref="_recheckAt"/>
    public long RecheckAt => Volatile.Read(ref _recheckAt);

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
            DropPairs();
            Volatile.Write(ref Verdict, Undecided);
            return true;
        }
    }

    /// <summary>Starts a check without a verdict over, as when a chunk JIT changed the code it was timing.</summary>
    public void Restart()
    {
        lock (this)
        {
            if (Verdict == Undecided)
                DropPairs();
        }
    }

    private void DropPairs()
    {
        InterpretedSamples = 0;
        _lastCompiled = 0;
        Interlocked.Exchange(ref _interpretedBucket.Ops, 0);
        Interlocked.Exchange(ref _interpretedBucket.Ticks, 0);
        Interlocked.Exchange(ref _compiledBucket.Ops, 0);
        Interlocked.Exchange(ref _compiledBucket.Ticks, 0);
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

        bool giveBack = false;
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
            if (Trace)
                Console.WriteLine($"il-evm self-check {Name}: {(interpreted ? "interpreted" : "compiled")} sample {perOp * 1e9 / Stopwatch.Frequency:F2} ns/op over {ops} ops, {InterpretedSamples} pairs so far");
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
                Ratio = Median(pairs);
                if (Trace)
                    Console.WriteLine($"il-evm self-check {Name}: ratio {Ratio:F2} after {pairs} pairs{(_second ? " (second check)" : "")}, interpreted/compiled ns per opcode: {Describe()}");
                if (Ratio <= IlEvmSettings.SelfCheckKeepRatio)
                {
                    Interlocked.Increment(ref _keptCount);
                    Volatile.Write(ref Verdict, Compiled);
                }
                else if (!_second || IlEvmSettings.InWarmUp)
                {
                    // While the process starts up a loss mostly measures start-up, so it is re-checked soon and never releases.
                    Interlocked.Increment(ref _probationCount);
                    int recheck = IlEvmSettings.InWarmUp ? IlEvmSettings.SelfCheckEarlyRecheckMs : IlEvmSettings.SelfCheckRecheckMs;
                    Volatile.Write(ref _recheckAt, Environment.TickCount64 + recheck);
                    Volatile.Write(ref Verdict, Probation);
                }
                else
                {
                    Interlocked.Increment(ref _givenBackCount);
                    Volatile.Write(ref Verdict, Interpreted);
                    giveBack = true;
                }
            }
        }

        // Outside this check's lock: releasing the code takes the contract's own lock.
        if (giveBack)
            OnGivenBack?.Invoke();
    }

    /// <summary>The median of the first <paramref name="pairs"/> ratios, sorted in a copy.</summary>
    private double Median(int pairs)
    {
        Span<double> sorted = stackalloc double[Pairs];
        sorted = sorted[..pairs];
        _ratios.AsSpan(0, pairs).CopyTo(sorted);
        sorted.Sort();
        return sorted[pairs / 2];
    }

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
