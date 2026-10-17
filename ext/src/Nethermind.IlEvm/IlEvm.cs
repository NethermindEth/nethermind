// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.IlEvm;

/// <summary>The EVM bytecode-to-IL compiler: compiled contracts, their runtime helpers and the code generator.</summary>
public static unsafe partial class IlEvm
{
    /// <summary>Returned by compiled code that hands the frame back at <c>programCounter</c>, with gas, stack and memory exactly the interpreter's before that instruction.</summary>
    private const EvmExceptionType IlDeopt = FrameCodeRunner<EthereumGasPolicy>.Declined;

    private const int IlPollInterval = 1024;

    /// <summary>The poll mark of a non-cancelable run: past any opcode count a frame's gas allows, with room for taken JUMPIs moving it on by one each (<c>Poll</c>); at 11 gas a JUMPI, no overflow below 5 billion gas.</summary>
    private const int IlNoPoll = int.MaxValue / 4 * 3;

    /// <summary>Returned by a chunk of a compiled contract to continue at <c>programCounter</c> in another chunk.</summary>
    private const EvmExceptionType IlContinue = (EvmExceptionType)(int.MinValue + 1);

    /// <summary>The dispatch loop's per-chain counters, carried from chunk to chunk of a compiled contract.</summary>
    internal struct IlRunState
    {
        /// <summary>Opcodes run since the frame was entered or resumed.</summary>
#pragma warning disable CS0649 // Written by the compiled chunks.
        public int OpCodes;
#pragma warning restore CS0649
        /// <summary>The opcode count at which a taken jump polls, plus the JUMPDESTs taken JUMPIs landed on since the last poll, which the loop's poll count leaves out; <see cref="IlNoPoll"/> when not cancelable.</summary>
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
        /// <summary>Adaptive: which kinds of thread may run the contract (<see cref="IlRuntime.RunMain"/>, <see cref="IlRuntime.RunWorkers"/>, <see cref="IlRuntime.RunQuery"/>); a check that finds compiled code slower clears its runner's bits.</summary>
        /// <remarks>Written racily, a byte at a time: a frame that reads a stale mask runs one part either way.</remarks>
        public byte RunMask;
        public IlOrigin Origin;
        /// <summary>Adaptive: when a code state first took the contract, in <see cref="Environment.TickCount64"/> milliseconds; 0 until then.</summary>
        public long AttachedAt;
        /// <summary>Adaptive: the switchback checks of block processing's runner and of the read-only queries', made at publishing for each whose check is on; null for one whose is off.</summary>
        public IlCheck? BlockCheck, QueryCheck;
        /// <summary>Adaptive: the IL and tables live profiling's budget counts for a contract it promoted, while it counts them; 0 for any other, and once released.</summary>
        public long BudgetBytes;
        /// <summary>Adaptive: a copy laid out against the helpers' ahead twins (<see cref="PrecompileAhead"/>), which the queries' runner runs until the helpers tier up; null for none, and once dropped (<see cref="IlWarmUp.SwitchFromAhead"/>).</summary>
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

        internal void SetCallees(int chunk, HashSet<MethodBase> callees)
        {
            if (_callees is { } all)
                all[chunk] = [.. callees];
        }

        internal MethodBase[]? CalleesOf(int chunk) => _callees?[chunk];
        private readonly int[] _entries = new int[chunks];
        private readonly int[] _states = new int[chunks];
        public readonly string Name = name;
        public int ChunkCount => Prepared.Length;
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
        public int IlBytes;
        public TimeSpan JitTime;
        /// <summary>Whether compiled code has proved faster than the interpreter on this contract's frames.</summary>
        public readonly IlSelfCheck Check = new();
        /// <summary>Chunks queued or compiled so far: a frame during which this moved waited on the JIT, and its time says nothing of the code.</summary>
        public int ChunkEvents;
        /// <summary>The opcodes a part of the code ran on the interpreter on average before its layout: under the hot order, what each frame handed back at a cold chunk weighs.</summary>
        public long OpsPerPart = 1;
        /// <summary>When the contract was laid out, in <see cref="Environment.TickCount64"/> milliseconds; its self-check waits <see cref="IlEvmSettings.SelfCheckDelayMs"/> from then.</summary>
        public long CompiledAt = Environment.TickCount64;

        public const int PartsLong = 1;
        public const int PartsShort = 2;

        /// <summary>The short-part rule's verdict (<see cref="IlEvmSettings.ShortParts"/>): 0 while it measures, then <see cref="PartsLong"/> or <see cref="PartsShort"/>, final.</summary>
        public int PartsVerdict;

        private int _sampledParts;
        private long _sampledOps;

        /// <summary>Adds a compiled part of <paramref name="opCodes"/> opcodes to the short-part rule's sample, and gives the verdict once <paramref name="sample"/> parts are in.</summary>
        /// <param name="threshold">The fewest opcodes a part must run on average for the contract to stay compiled.</param>
        public int SamplePart(int opCodes, int threshold, int sample)
        {
            long ops = Interlocked.Add(ref _sampledOps, opCodes);
            int parts = Interlocked.Increment(ref _sampledParts);
            if (parts < sample)
                return Volatile.Read(ref PartsVerdict);
            int verdict = ops < (long)threshold * parts ? PartsShort : PartsLong;
            if (Interlocked.CompareExchange(ref PartsVerdict, verdict, 0) == 0)
                IlEvmSettings.CountShortPartVerdict(verdict == PartsShort);
            return Volatile.Read(ref PartsVerdict);
        }

        /// <summary>Set once a precompiled contract first meets a frame.</summary>
        public int Attached;
        /// <summary>Whether <see cref="IlBytes"/> counts toward <see cref="IlEvmSettings.LiveIlBytes"/>; cleared once by release or collection.</summary>
        private int _live;

        private volatile bool _released;

        public bool IsReleased => _released;

        public long MemoryBytes => IlBytes + TableBytes;

        public void MarkLive()
        {
            if (Interlocked.Exchange(ref _live, 1) == 0)
                IlEvmSettings.AddLiveIl(MemoryBytes);
        }

        /// <summary>Counts the contract's IL as alive only if it fits the live-IL cap once <paramref name="freed"/> bytes are let go of (<see cref="IlEvmSettings.TryReserveLiveIl"/>); true if counted already.</summary>
        public bool TryMarkLive(long freed = 0)
        {
            if (Volatile.Read(ref _live) == 1)
                return true;
            if (!IlEvmSettings.TryReserveLiveIl(MemoryBytes, freed))
                return false;
            // Marked meanwhile by another thread, which counted the bytes itself.
            if (Interlocked.Exchange(ref _live, 1) == 1)
                IlEvmSettings.AddLiveIl(-MemoryBytes);
            return true;
        }

        /// <summary>Drops the compiled code; its frames run on the interpreter from now on.</summary>
        /// <remarks>An Adaptive contract, or one released with <paramref name="dropTables"/>, drops its tables too, as code states may hold it a while: frames then hand back on entry, a running chunk keeps its own tables, and a walk hands over.</remarks>
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
        /// <param name="opCodesSincePoll">On a hand-back in a cancelable run, the opcodes since the offer or the last poll, so the interpreter polls where it alone would; zero otherwise.</param>
        /// <returns>The halt reason, or <see cref="IlDeopt"/> to have the interpreter go on from <paramref name="programCounter"/>.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public EvmExceptionType Run(VirtualMachine<EthereumGasPolicy> vm, scoped ref EvmStack stack, scoped ref EthereumGasPolicy gas, scoped ref nint programCounter,
            ref int opCodesSincePoll, bool cancelable)
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
            IlRunState state = new() { PollAt = cancelable ? IlPollInterval : IlNoPoll };

            EvmExceptionType result;
            do
            {
                pc = programCounter;
                entryChunk = EntryChunk;
                int chunk = (nuint)pc < (nuint)entryChunk.Length ? entryChunk[pc] : -1;
                if (chunk < 0)
                    return HandBack(vm, ref gas, ref programCounter, ref state, ref opCodesSincePoll, cancelable);
                IlRunner? runner = Runners[chunk] ?? Cold(chunk);
                // A chunk not compiled yet: the interpreter goes on with the gas, stack and counter the last chunk left.
                if (runner is null)
                    return HandBack(vm, ref gas, ref programCounter, ref state, ref opCodesSincePoll, cancelable);
                if (IlEvmDiagnostics.Enabled)
                    Interlocked.Increment(ref IlEvmDiagnostics.ChunkCalls);
                result = runner(vm, ref stack, ref gas, ref programCounter, ref state);
            } while (result == IlContinue);

            if (result == IlDeopt)
                HandBack(vm, ref gas, ref programCounter, ref state, ref opCodesSincePoll, cancelable);
            return result;
        }

        /// <summary>Reports, for a hand-back, the opcodes run since the offer or the last poll (<see cref="IlRunState.PollAt"/> less an interval).</summary>
        /// <remarks>
        /// The interpreter counts a JUMPDEST it is handed back on. After a JUMP, or a JUMPI run with its PUSH2, the loop counted
        /// it already, and the report leaves it out; after a JUMPI run alone the mark it moved on (<c>Poll</c>) leaves the report
        /// one short, as it must be. Right after a poll that is -1: the JUMPDEST is stepped here, as the loop's jump steps it, so
        /// nothing of the new interval is spent; a jump polls only with the gas for it left (<c>Poll</c>).
        /// </remarks>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private EvmExceptionType HandBack(VirtualMachine<EthereumGasPolicy> vm, scoped ref EthereumGasPolicy gas, scoped ref nint programCounter,
            scoped ref IlRunState state, ref int opCodesSincePoll, bool cancelable)
        {
            if (!cancelable)
                return IlDeopt;
            int since = state.OpCodes - (state.PollAt - IlPollInterval);
            if (since < 0)
            {
                System.Diagnostics.Debug.Assert(since == -1 && (nuint)programCounter < (nuint)Code.Length && Code.Span[(int)programCounter] == (byte)Instruction.JUMPDEST,
                    "Only a hand-back on a JUMPDEST a poll has just counted reports less than nothing.");
                if (EthereumGasPolicy.GetRemainingGas(in gas) >= GasCostOf.JumpDest)
                {
                    EthereumGasPolicy.UpdateGas(ref gas, GasCostOf.JumpDest);
                    vm.AddOpCodes(1);
                    programCounter++;
                }
                since = 0;
            }

            opCodesSincePoll = since;
            return IlDeopt;
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
            // The Simple rules compile contracts whole: a chunk still cold failed its JIT, or a test left it so, and its frames hand back.
            if (mode == IlMode.Simple)
                return null;
            // Queued already: frames do not write the shared counter again, except under the hot order, which counts each hand-back here, racily.
            int chunkState = Volatile.Read(ref _states[chunk]);
            if (chunkState != ChunkCold)
            {
                if (chunkState == ChunkQueued && IlEvmSettings.CompileOrderHot)
                    _entries[chunk]++;
                return null;
            }

            if (Interlocked.Increment(ref _entries[chunk]) >= IlEvmSettings.ChunkThreshold &&
                Interlocked.CompareExchange(ref _states[chunk], ChunkQueued, ChunkCold) == ChunkCold)
            {
                Interlocked.Increment(ref ChunkEvents);
                // Adaptive: JIT between blocks (only a live contract has cold chunks); Legacy: the background compiler. If turned away, frames ask again.
                bool queued = IlEvmSettings.Mode == IlMode.Adaptive
                    ? IlScheduler.TryEnqueue(new ChunkJob(this, chunk))
                    : IlCompileQueue.Enqueue((Contract: this, Chunk: chunk), static s => s.Contract.CompileChunk(s.Chunk),
                        static s => Volatile.Read(ref s.Contract._entries[s.Chunk]) * s.Contract.OpsPerPart);
                if (!queued)
                {
                    _entries[chunk] = 0;
                    Volatile.Write(ref _states[chunk], ChunkCold);
                }
            }

            return null;
        }

        /// <summary>JIT-compiles <paramref name="chunk"/> and publishes its runner under the contract's lock; several threads may JIT chunks of one contract at once.</summary>
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

            // The JIT runs outside the lock, which a release takes: a frame's thread must never wait on a JIT.
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
            // Adaptive: what the chunk calls is compiled here too, off the frames' threads.
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

        public void CompileAllChunks()
        {
            for (int chunk = 0; chunk < Prepared.Length; chunk++)
                CompileChunk(chunk);
        }

        /// <summary>Finds how a segment whose precondition failed fails, checking each opcode as the dispatch loop does.</summary>
        /// <remarks>Each opcode charges fixed gas and has fixed stack bounds, checked gas first, so the walk reproduces the loop's fault, gas left and opcode count; past an opcode with dynamic gas the interpreter runs the segment from its start.</remarks>
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
            // Read once: a release swaps in empty tables, on which a walk hands over at the segment's start, where the interpreter faults the same way.
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

        internal bool TablesReleased => EntryChunk.Length == 0 && Volatile.Read(ref _segments) == SegmentTables.Empty;
    }

    /// <summary>One compiled chunk: what its dynamic method is bound to, with the tables its code reads.</summary>
    internal sealed class IlChunk(IlContract contract, short[] jumpCases, int jumpBase, IlSegmentInfo[] segments, IlSite[] sites)
    {
        public readonly IlContract Contract = contract;
        public readonly int[] EntryIndex = contract.EntryIndex;
        /// <summary>The contract's JUMPDEST ordinals, and this chunk's jump case for each, read by its dynamic jumps.</summary>
        public readonly int[] JumpOrdinal = contract.JumpOrdinal;
        public readonly short[] JumpCases = jumpCases;
        public readonly int JumpBase = jumpBase;
        /// <summary>Per segment with a precondition, where its instructions sit in the contract's tables and where it starts.</summary>
        public readonly IlSegmentInfo[] Segments = segments;
        /// <summary>Per call of an embedded body, what <c>IlEmbedded</c> needs to finish or leave its segment.</summary>
        public readonly IlSite[] Sites = sites;

        /// <summary>The chunk's shared fault path: finds how <paramref name="segment"/> fails and points <paramref name="programCounter"/> at its start for a hand-over.</summary>
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
    /// <param name="NextPc">The offset past it, where the segment goes on.</param>
    /// <param name="GasAfter">The static gas of the instructions after it, charged again once it has run.</param>
    /// <param name="CountedAfter">The instructions after it, counted with the segment.</param>
    internal readonly record struct IlSite(int Pc, int NextPc, ulong GasAfter, int FirstAfter, int CountedAfter, nint Execute, int HeadDelta, ulong GasBack);
}
