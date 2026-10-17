// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Threading;
using Nethermind.Evm;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

/// <summary>Runs the frame parts it is offered on compiled code where the rules say, and leaves the rest to the interpreter.</summary>
/// <remarks>
/// <para>
/// Adaptive (<see cref="IlMode.Adaptive"/>): a part runs compiled when its code's state names a contract this runner,
/// this kind of thread and the frame's body set may run, as <see cref="IlRuntime"/> resolved it, and the visit gate
/// (<see cref="IlGate"/>) expects the part's visit to gain from it; code with none is declined, most of it from the
/// slot's mark alone. Once the contract's switchback check for this runner runs (<see cref="IlCheck"/>), the parts the
/// gate admits run on the interpreter in the check's windows, and are timed now and then on either path.
/// </para>
/// <para>
/// Simple (<see cref="IlMode.Simple"/>): a part runs compiled when its code is in the compiled set for this runner and
/// body set (<see cref="IlSimple"/>), every part of it; any other is declined, and the queries' runner counts a sample of
/// them for live promotion.
/// </para>
/// <para>
/// Legacy and the measurement modes: a frame of code that is not compiled yet is declined with a request for the
/// interpreter's opcode count, which is what makes the code hot. A compiled run that hands back declines from the
/// instruction where it stopped. With <see cref="IlEvmSettings.LegacyEntry"/> the Legacy rules attach a runner of their
/// own instead (<see cref="ForQueries"/>, <see cref="ForBlocks"/>), whose entry runs the same path with nothing else on it.
/// </para>
/// <para>
/// One instance per surface (<see cref="IlEvmSurface"/>): the read-only query environments of each kind take their own,
/// which differ only in the surface they count their parts under (<see cref="IlSurfaceCounts"/>); block processing's
/// takes <see cref="Block"/>.
/// </para>
/// </remarks>
public sealed class IlEvmFrameRunner : FrameCodeRunner<EthereumGasPolicy>
{
    /// <summary>
    /// The runner for the read-only call environments (<see cref="IlEvmSurface.Call"/>): runs the shipped and live
    /// contracts, or under Legacy compiles hot code at run time and self-checks it.
    /// </summary>
    public static IlEvmFrameRunner Instance { get; } = new(IlEvmSurface.Call);

    /// <summary>As <see cref="Instance"/>, for the replay and traced-call environments (<see cref="IlEvmSurface.Trace"/>).</summary>
    public static IlEvmFrameRunner TraceInstance { get; } = new(IlEvmSurface.Trace);

    /// <summary>As <see cref="Instance"/>, for the simulation environments (<see cref="IlEvmSurface.Simulate"/>).</summary>
    public static IlEvmFrameRunner SimulateInstance { get; } = new(IlEvmSurface.Simulate);

    /// <summary>
    /// The runner for block processing (Scope=All): runs the shipped contracts, and under Legacy compiles or self-checks
    /// at run time only as <see cref="IlEvmSettings.BlockRuntimeCompile"/> and <see cref="IlEvmSettings.BlockSelfCheck"/> say.
    /// </summary>
    public static IlEvmFrameRunner Block { get; } = new(IlEvmSurface.Blocks);

    /// <summary>The instance for <paramref name="surface"/>.</summary>
    public static IlEvmFrameRunner For(IlEvmSurface surface) => surface switch
    {
        IlEvmSurface.Call => Instance,
        IlEvmSurface.Trace => TraceInstance,
        IlEvmSurface.Simulate => SimulateInstance,
        _ => Block,
    };

    /// <summary>
    /// The runner to attach to the read-only call environments' machines under the rules set now: the Legacy rules' own
    /// (<see cref="IlEvmLegacyFrameRunner{TBlock}"/>) when <see cref="IlEvmSettings.LegacyEntry"/> asks for it and no
    /// diagnostics run, else <see cref="Instance"/>.
    /// </summary>
    public static FrameCodeRunner<EthereumGasPolicy> ForQueries => ForQuery(IlEvmSurface.Call);

    /// <summary>As <see cref="ForQueries"/>, for the query environments of <paramref name="surface"/>.</summary>
    public static FrameCodeRunner<EthereumGasPolicy> ForQuery(IlEvmSurface surface) =>
        UseLegacyEntry ? IlEvmLegacyFrameRunner<OffFlag>.For(surface) : For(surface);

    /// <summary>As <see cref="ForQueries"/>, for block processing: the Legacy rules' own runner, or <see cref="Block"/>.</summary>
    public static FrameCodeRunner<EthereumGasPolicy> ForBlocks => UseLegacyEntry ? IlEvmLegacyFrameRunner<OnFlag>.For(IlEvmSurface.Blocks) : Block;

    private static bool UseLegacyEntry => IlEvmSettings.LegacyEntry && IlEvmSettings.Mode == IlMode.Legacy && !IlEvmDiagnostics.Enabled;

    private readonly bool _block;

    /// <summary>The environments the instance serves, which its parts are counted under.</summary>
    public IlEvmSurface Surface { get; }

    private IlEvmFrameRunner(IlEvmSurface surface)
    {
        Surface = surface;
        _block = surface == IlEvmSurface.Blocks;
    }

    /// <summary>The general entry, for the Legacy runner's parts under other rules than it was attached for; that runner counts the part itself.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal EvmExceptionType RunGeneral(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags) =>
        RunPart(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags);

    /// <summary>The general report, for the Legacy runner's parts under other rules than it was attached for.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal void InterpretedGeneral(CodeInfo codeInfo, int opCodes) => OnInterpreted(codeInfo, opCodes);

    /// <summary>
    /// The Legacy rules' part of a contract whose self-check has not kept it compiled: given back or on probation, it goes
    /// to the interpreter; undecided past <see cref="IlEvmSettings.SelfCheckDelayMs"/>, it is checked; else it runs compiled.
    /// </summary>
    /// <remarks>As <see cref="RunOther"/>'s self-check with no diagnostics, out of the Legacy runner's entry.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static EvmExceptionType RunUnsettled(CodeInfo codeInfo, IlEvm.IlContract contract, VirtualMachine<EthereumGasPolicy> vm,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, bool cancelable)
    {
        int verdict = Volatile.Read(ref contract.Check.Verdict);
        if (verdict >= IlSelfCheck.Interpreted && (verdict == IlSelfCheck.Interpreted || !contract.Check.TryRecheck()))
            return Declined;
        if (verdict == IlSelfCheck.Undecided && Environment.TickCount64 - contract.CompiledAt >= IlEvmSettings.SelfCheckDelayMs)
            return RunChecked(codeInfo, contract, vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
        return contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
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
    /// The part counts as compiled when the machine's opcode count moved during the run: only compiled code moves it
    /// before the interpreter takes a part over, and a run that hands back before its first opcode moves nothing. A part
    /// whose run throws is counted as offered only.
    /// </remarks>
    // Optimized from the first call, as are the helpers compiled code calls: a tier-0 copy would slow the frames run
    // before tiering catches up, and the self-check would judge compiled code by them.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    protected override EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        long[] counts = IlSurfaceCounts.OfThisThread();
        int surface = (int)Surface;
        counts[IlSurfaceCounts.Offered + surface]++;
        int before = vm.OpCodeCount;
        EvmExceptionType result = RunPart(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags);
        if (vm.OpCodeCount != before)
            counts[IlSurfaceCounts.Compiled + surface]++;
        return result;
    }

    /// <summary>A frame part as the rules run it: under the Adaptive rules most parts end at their code slot's mark.</summary>
    /// <remarks>
    /// <para>
    /// A transaction's top-level entry also ends the visits this thread's gate keeps of the transaction before, whatever
    /// then runs the part: the gate only sees the parts it is offered, and a top-level frame declined from its mark would
    /// otherwise leave two transactions' visits of the code it calls one visit. The Legacy, measurement and profiling rules
    /// go to their own run at once.
    /// </para>
    /// <para>
    /// The machine's hook inlines all of it. A part past the mark run out of line would pay a second prologue, which costs
    /// the state, settled and admitted parts more than the hook's larger frame costs a Quiet one.
    /// </para>
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EvmExceptionType RunPart(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        IlMode mode = IlEvmSettings.Mode;
        if (mode == IlMode.Simple)
            return RunSimple(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags);
        if (mode != IlMode.Adaptive)
            return RunOther(vm, codeInfo, bodies, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, flags);

        if (programCounter == 0 && vm.VmState.IsTopLevel)
            IlThread.TopLevelEntry(_block);
        // Most code is decided by its slot alone: this runner's mark, or the one for both runners, declines with no read
        // of what the slot points at, unless the runner samples this transaction for live profiling. Marks exist only
        // where the Adaptive rules put them, so a mark declines whatever else is switched. A runner that counts samples
        // one transaction in a few, all of its visits: the others decline from the marks and count nothing, as a runner
        // that does not count, while a compiled contract's visits are counted only in the transactions sampled.
        object? slot = CodeState(codeInfo);
        if ((ReferenceEquals(slot, IlRuntime.QuietAll) || ReferenceEquals(slot, _block ? IlRuntime.QuietBlock : IlRuntime.QuietQuery)) && !Sampling())
            return Declined;

        // The kill switch; the diagnostics' counters exist only on the Legacy path, which they force.
        if (!IlEvmSettings.Enabled)
            return Declined;

        bool counting = _block ? IlRuntime.BlocksCounting : IlRuntime.QueryCounting;
        bool sampling = counting && IlThread.SamplingTransaction;
        IlEvm.IlCodeState? state = slot as IlEvm.IlCodeState;
        if (state is null || state.Version != IlRuntime.Version)
        {
            state = state is null
                ? IlRuntime.Resolve(vm, codeInfo, bodies, slot, _block, counting, sampling)
                : IlRuntime.Refresh(vm, state, codeInfo, bodies, _block, counting, sampling);
            if (state is null)
                return Declined;
        }

        IlEvm.IlContract? contract = _block ? state.BlockRun : state.QueryRun;
        // A part of a body set the state's contract was not compiled for: the profile's contract for it, if any; a body
        // set the state found none for once goes to the interpreter without looking again, until the state resolves again.
        if (contract is not null && !ReferenceEquals(contract.Bodies, bodies))
        {
            contract = ReferenceEquals(_block ? state.BlockMissed : state.QueryMissed, bodies) && state.MissEpoch == IlRuntime.MissEpoch
                ? null
                : IlRuntime.ForkMiss(state, codeInfo, bodies, _block);
        }
        // Nothing compiled for the code on this body set, or nothing this kind of thread may run: a sampled transaction
        // counts the part for the live profile, asking the interpreter for its opcodes.
        if (contract is null || !MayRun(contract.RunMask))
        {
            // A check that has just taken this runner off the contract: a visit under way ran its earlier parts, and
            // learns nothing of a length it no longer sees whole.
            if (contract is not null && (contract.RunMask & (_block ? IlRuntime.CheckOffBlock : IlRuntime.CheckOffQuery)) != 0)
                IlThread.Current.Unmeasure(vm, state, programCounter, _block);
            return sampling && state.Profile is not null && IlThread.Current.Sample(vm, state, codeInfo, bodies, programCounter, _block) ? DeclinedReportingOpcodes : Declined;
        }

        // The visit gate leaves to the interpreter the parts of visits too short or too cold to gain from compiled code;
        // with the gate off it admits every part, keeping its bookkeeping.
        IlThread thread = IlThread.Current;
        int decision = thread.Offer(vm, state, codeInfo, programCounter, _block, sampling);
        if (decision != IlGate.Compile)
            return decision == IlGate.InterpretReport ? DeclinedReportingOpcodes : Declined;

        // The switchback check, once it runs, has the parts admitted take turns with the interpreter, and times some on
        // either path; the others run compiled untimed, as with no check.
        bool cancelable = (flags & FrameRunFlags.Cancelable) != 0;
        IlCheck? check = _block ? contract.BlockCheck : contract.QueryCheck;
        if (check is not null && check.Active)
        {
            int tick = Volatile.Read(ref IlCheckClock.Tick);
            if (check.TakesPart(thread, tick, out long word))
                return RunSwitchback(thread, check, word, tick, contract, codeInfo, vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
        }

        int before = vm.OpCodeCount;
        // The list's RPC contracts run their ahead copies, whose helpers are optimized from the start, until their own
        // helpers have tiered up; one part in IlWarmUp.AheadOwnShare runs the contract's own code meanwhile, which gives
        // those helpers their first calls, and the profile they are optimized with, from real calls.
        IlEvm.IlContract run = contract.Ahead is { } ahead && (thread.Next() & IlWarmUp.AheadOwnMask) != 0 ? ahead : contract;
        EvmExceptionType result = run.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
        thread.AfterCompiled(vm, before, codeInfo, handedBack: result == Declined);
        // A part handed back mid-way is reported, so its visit counts the interpreter's rest of it too.
        return result == Declined ? DeclinedReportingOpcodes : result;
    }

    /// <summary>The Simple rules' part (<see cref="IlSimple"/>): the code state's contract for this runner runs every part of the code it is compiled for; anything else is declined.</summary>
    /// <remarks>
    /// A part reads the slot, its state's generation and body set, and one contract; code with nothing compiled declines
    /// there, and the queries' runner counts one such part in <see cref="IlSimple.SampleEvery"/>, drawn at random, for live
    /// promotion, with its opcodes. A code info whose state is of an older generation or another body set, or that has
    /// none, resolves once, out of line.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EvmExceptionType RunSimple(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        // The kill switch first: the replay harness's interpreter passes turn it off.
        if (!IlEvmSettings.Enabled)
            return Declined;
        object? slot = CodeState(codeInfo);
        IlSimpleState? state = slot as IlSimpleState;
        if (state is null || state.Generation != IlSimple.Generation || !ReferenceEquals(state.Bodies, bodies))
        {
            // A mark of the current generation: nothing to run, nothing to count.
            if (ReferenceEquals(slot, IlSimple.Nothing) || (_block && ReferenceEquals(slot, IlSimple.NothingBlock)))
            {
                CountInterpreted();
                return Declined;
            }

            state = IlSimple.Resolve(vm, codeInfo, bodies, slot, _block);
            if (state is null)
            {
                CountInterpreted();
                return Declined;
            }
        }

        // The state's contracts are compiled for its body set, which is this part's.
        IlEvm.IlContract? contract = _block ? state.Block : state.Query;
        IlSimpleThread thread = IlSimpleThread.Current;
        if (contract is not null)
        {
            if (_block)
            {
                thread.BlockCompiled++;
            }
            else
            {
                thread.QueryCompiled++;
                // A promoted contract's use, sampled, which eviction ranks by.
                if (state.Uses is { } uses && IlSimple.Draw(thread))
                    uses.Counts.AddCompiled(thread.Stripe);
            }

            // A part handed back mid-way is the interpreter's from there, unreported.
            return contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, (flags & FrameRunFlags.Cancelable) != 0);
        }

        if (_block)
        {
            thread.BlockInterpreted++;
            return Declined;
        }

        thread.QueryInterpreted++;
        if (state.Count is { } profile && IlSimple.Draw(thread))
        {
            IlSimple.Sample(profile, bodies, thread);
            return DeclinedReportingOpcodes;
        }

        return Declined;
    }

    /// <summary>Counts a part the Simple rules leave to the interpreter, for the exit line.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CountInterpreted()
    {
        IlSimpleThread thread = IlSimpleThread.Current;
        if (_block)
            thread.BlockInterpreted++;
        else
            thread.QueryInterpreted++;
    }

    /// <summary>Whether this runner samples the transaction under way on this thread for live profiling: it counts, and the transaction drew a sample (<see cref="IlThread.TopLevelEntry"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool Sampling() => (_block ? IlRuntime.BlocksCounting : IlRuntime.QueryCounting) && IlThread.SamplingTransaction;

    /// <summary>
    /// Runs a part the switchback check took (<see cref="IlCheck.TakesPart"/>): one of an interpreted window, which runs
    /// on the interpreter and is timed one in <see cref="IlCheck.TimeInterpretedEvery"/>, or a compiled one drawn to be timed.
    /// </summary>
    /// <remarks>
    /// A compiled part timed to its end goes to the check at once; one handed back is timed on through the interpreter's
    /// rest of it, which its report ends, as an interpreted window's part is. Nothing is timed in a window's first tick,
    /// nor for block processing outside its processing passes, nor a part that met a chunk not compiled yet.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private EvmExceptionType RunSwitchback(IlThread thread, IlCheck check, long word, int tick, IlEvm.IlContract contract, CodeInfo codeInfo,
        VirtualMachine<EthereumGasPolicy> vm, scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, bool cancelable)
    {
        int window = tick / IlCheckClock.WindowTicks;
        bool settling = tick % IlCheckClock.WindowTicks == 0;
        bool interpreted = (word & IlCheck.WordInterpreted) != 0, timed = true;
        uint interpretedMask = IlCheck.TimeInterpretedEvery - 1;
        if (IlGate.Chaos)
        {
            // For the differential runs: each part the check takes runs on a path at random, and one in two is timed, so
            // every path of the check, and the verdicts that take the runner off a contract mid-visit, meet the
            // interpreter's results.
            settling = false;
            interpreted = (thread.Next() & 1) == 0;
            timed = (thread.Next() & 1) == 0;
            interpretedMask = 1;
        }

        if (interpreted)
        {
            // A switchback window: the part runs on the interpreter, reported, so its visit counts it.
            thread.SwitchedBack(codeInfo);
            if (!settling && (thread.Next() & interpretedMask) == 0 && MaySample())
                thread.ArmTimed(check, window, interpreted: true, Stopwatch.GetTimestamp(), compiledOps: 0, events: 0);
            return DeclinedReportingOpcodes;
        }

        if (!timed || !MaySample())
        {
            int before = vm.OpCodeCount;
            EvmExceptionType untimed = contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
            thread.AfterCompiled(vm, before, codeInfo, handedBack: untimed == Declined);
            return untimed == Declined ? DeclinedReportingOpcodes : untimed;
        }

        int events = Volatile.Read(ref contract.ChunkEvents);
        int ops = vm.OpCodeCount;
        IlEvm.IlContract.HitColdChunk = false;
        long start = Stopwatch.GetTimestamp();
        EvmExceptionType result = contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
        long ticks = Stopwatch.GetTimestamp() - start;
        thread.AfterCompiled(vm, ops, codeInfo, handedBack: result == Declined);
        if (result == Declined)
        {
            // The interpreter's rest of the part counts as compiled code's time; one that waits for a chunk's JIT says
            // nothing of compiled code.
            if (!IlEvm.IlContract.HitColdChunk)
                thread.ArmTimed(check, window, interpreted: false, start, vm.OpCodeCount - ops, events);
            return DeclinedReportingOpcodes;
        }

        // A part during which a chunk was compiled waited on the JIT.
        if (Volatile.Read(ref contract.ChunkEvents) == events)
            thread.AddCompiledSample(check, ticks, vm.OpCodeCount - ops);
        return result;
    }

    /// <summary>
    /// Whether the part may be timed: always for the read-only queries; for block processing only during a processing
    /// pass, as its own thread knows from the core's mark and the threads it serves from the branch events.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MaySample() => !_block || ProcessingThread.IsBlockProcessingThread || IlRuntime.InBranch;

    /// <summary>Whether this runner may run a contract whose <see cref="IlEvm.IlContract.RunMask"/> is <paramref name="runMask"/> on this thread.</summary>
    /// <remarks>
    /// The core's mark of block processing's own thread is read only when the mask tells block processing's two kinds of
    /// thread apart, which most shipped contracts' does not: a second thread-static on every part costs as much as the rest.
    /// </remarks>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool MayRun(byte runMask)
    {
        if (!_block)
            return (runMask & IlRuntime.RunQuery) != 0;
        int kinds = runMask & (IlRuntime.RunMain | IlRuntime.RunWorkers);
        return kinds == (IlRuntime.RunMain | IlRuntime.RunWorkers) ||
               (kinds != 0 && (kinds & (ProcessingThread.IsBlockProcessingThread ? IlRuntime.RunMain : IlRuntime.RunWorkers)) != 0);
    }

    /// <summary>The Legacy, measurement and profiling rules' run, as before the Adaptive ones, out of the Adaptive path's way.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private EvmExceptionType RunOther(VirtualMachine<EthereumGasPolicy> vm, CodeInfo codeInfo, OpcodeBodySet bodies,
        scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, ref nint programCounter, ref int opCodesSincePoll, FrameRunFlags flags)
    {
        if (!IlEvmSettings.Enabled)
            return IlEvmDiagnostics.Enabled ? TimeInterpreted(handedBack: false) : Declined;
        if (IlEvmSettings.Profile)
        {
            // Initcode is never in the code database, so it could never be shipped. A frame is offered again on every
            // resume after a nested call; it is counted on entry, its opcodes on every part.
            if (vm.VmState.ExecutionType.IsAnyCreate())
                return Declined;
            IlEvmProfile.Frame(codeInfo, entry: programCounter == 0);
            return DeclinedReportingOpcodes;
        }

        IlEvm.IlContract? contract;
        bool count = false;
        if (_block && !IlEvmSettings.BlockRuntimeCompile)
            contract = IlEvm.FindPrecompiledOnly(codeInfo, bodies);
        else
            contract = IlEvm.Find(codeInfo, bodies, out _, out count);
        if (contract is null)
            return IlEvmDiagnostics.Enabled ? TimeInterpreted(handedBack: false) : count ? DeclinedReportingOpcodes : Declined;

        bool cancelable = (flags & FrameRunFlags.Cancelable) != 0;
        IlEvmSettings.SawFrame();
        int verdict = Volatile.Read(ref contract.Check.Verdict);
        if (IlEvmSettings.SelfCheck && (!_block || IlEvmSettings.BlockSelfCheck))
        {
            if (verdict >= IlSelfCheck.Interpreted && (verdict == IlSelfCheck.Interpreted || !contract.Check.TryRecheck()))
                return Declined;
            if (verdict == IlSelfCheck.Undecided && !IlEvmDiagnostics.Enabled &&
                Environment.TickCount64 - contract.CompiledAt >= IlEvmSettings.SelfCheckDelayMs)
                return RunChecked(codeInfo, contract, vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
        }
        // Without its own self-check the block runner ignores the query runner's verdicts: those time calls, not blocks. A
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

        // Compiled frames are timed one in TimeCompiledEvery: two timestamps and the shared buckets on every frame cost
        // more than many frames run, and more still with threads running the same contract.
        if ((random >> 16) % IlSelfCheck.TimeCompiledEvery != 0)
        {
            EvmExceptionType untimed = contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
            return untimed;
        }

        int chunkEvents = Volatile.Read(ref contract.ChunkEvents);
        int ops = vm.OpCodeCount;
        IlEvm.IlContract.HitColdChunk = false;
        long start = Stopwatch.GetTimestamp();
        EvmExceptionType result = contract.Run(vm, ref stack, ref gas, ref programCounter, ref opCodesSincePoll, cancelable);
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
        // A Simple part is reported only when sampled for live promotion.
        if (IlEvmSettings.Mode == IlMode.Simple && !IlEvmDiagnostics.Enabled)
        {
            IlSimple.CountOps(codeInfo, opCodes);
            return;
        }

        // The diagnostics time plain frames on the Legacy path, whichever rules are set.
        if (IlEvmSettings.Mode != IlMode.Adaptive || IlEvmDiagnostics.Enabled)
        {
            OnInterpretedOther(codeInfo, opCodes);
            return;
        }

        // An Adaptive part is reported only when the visit gate asked: its opcodes count toward its visit.
        IlThread.CurrentOrNull?.FinishReport(codeInfo, opCodes);
    }

    /// <summary>The Legacy, measurement and profiling rules' report, as before the Adaptive ones.</summary>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    internal static void OnInterpretedOther(CodeInfo codeInfo, int opCodes)
    {
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
    /// <summary>Of the frames compiled code runs until the verdict, one in this many is timed.</summary>
    public const int TimeCompiledEvery = 4;
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

    /// <summary>Print each verdict with its pairs to the console, with <c>NETHERMIND_ILEVM_SELF_CHECK_TRACE=1</c>.</summary>
    private static readonly bool Trace = Environment.GetEnvironmentVariable("NETHERMIND_ILEVM_SELF_CHECK_TRACE") == "1";

    /// <summary>The compiled contract's name, for the trace.</summary>
    public string? Name;

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
                double[] sorted = _ratios[..pairs];
                Array.Sort(sorted);
                Ratio = sorted[pairs / 2];
                if (Trace)
                    Console.WriteLine($"il-evm self-check {Name}: ratio {Ratio:F2} after {pairs} pairs{(_second ? " (second check)" : "")}, interpreted/compiled ns per opcode: {Describe()}");
                if (Ratio <= IlEvmSettings.SelfCheckKeepRatio)
                {
                    Interlocked.Increment(ref _keptCount);
                    Volatile.Write(ref Verdict, Compiled);
                }
                else if (!_second || IlEvmSettings.InWarmUp)
                {
                    // Slower once: interpreted for a while, then checked again before the code is let go. While the
                    // process starts up a loss mostly measures start-up, so it is re-checked soon and never releases.
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
