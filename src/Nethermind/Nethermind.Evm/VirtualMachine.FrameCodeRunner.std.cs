// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy>
{
    /// <summary>What the dispatch table of the running transaction's spec runs, resolved while a runner is attached.</summary>
    private OpcodeBodySet? _frameCodeBodies;

    /// <summary>Runs the frame on the attached runner when there is one and no instruction tracer, otherwise on the dispatch loop.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private EvmExceptionType RunFrameCode<TTracingInst, TCancelable>(
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas,
        ref nint programCounter)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
    {
        // The dispatch loop's entry poll, taken here so that a runner is offered the frame after it, as the loop's
        // first opcode would run, and polls the same number of times however the frame then runs. A frame resumed at
        // the end of its code takes none, with or without a runner: the loop's end-of-code return comes before its poll.
        if (TCancelable.IsActive && (nuint)programCounter < (nuint)stack.CodeLength && _txTracer.IsCancelled)
            ThrowOperationCanceledException();

        if (TTracingInst.IsActive || _frameCodeRunner is null)
            return RunDispatchLoop<TTracingInst, TCancelable>(ref stack, ref gas, ref programCounter);

        // The out-of-line call takes a copy by reference, so the caller's own counter stays in a register on the
        // dispatch-loop path instead of living in its frame.
        nint runnerProgramCounter = programCounter;
        EvmExceptionType result = RunFrameCodeOnRunner<TCancelable>(ref stack, ref gas, ref runnerProgramCounter);
        programCounter = runnerProgramCounter;
        return result;
    }

    /// <remarks>Out of line, so the runner path stays out of <c>RunByteCode</c>'s frame.</remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private EvmExceptionType RunFrameCodeOnRunner<TCancelable>(
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas,
        ref nint programCounter)
        where TCancelable : struct, IFlag
    {
        // A frame resumed at the end of its code stops in the loop's entry check, which took no poll, so it is not offered.
        if ((nuint)programCounter >= (nuint)stack.CodeLength)
            return RunDispatchLoop<OffFlag, TCancelable>(ref stack, ref gas, ref programCounter);

        FrameCodeRunner<TGasPolicy> runner = _frameCodeRunner!;
        CodeInfo codeInfo = VmState.Env.CodeInfo;
        // The machine polled just before the offer, so the runner starts with none of the interval spent.
        int opCodesSincePoll = 0;
        EvmExceptionType result = runner.Run(this, codeInfo, _frameCodeBodies ??= GetOpcodeBodySet(Spec), ref stack, ref gas,
            ref programCounter, ref opCodesSincePoll, TCancelable.IsActive ? FrameRunFlags.Cancelable : FrameRunFlags.None);
        if (result == FrameCodeRunner<TGasPolicy>.Declined)
            return RunDispatchLoop<OffFlag, TCancelable>(ref stack, ref gas, ref programCounter, HandedBackSincePoll<TCancelable>(opCodesSincePoll));
        if (result != FrameCodeRunner<TGasPolicy>.DeclinedReportingOpcodes)
            return result;

        int before = OpCodeCount;
        result = RunDispatchLoop<OffFlag, TCancelable>(ref stack, ref gas, ref programCounter, HandedBackSincePoll<TCancelable>(opCodesSincePoll));
        runner.OnInterpreted(codeInfo, OpCodeCount - before);
        return result;
    }

    /// <summary>The poll budget a runner handed back with; only a cancelable run reads it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static nint HandedBackSincePoll<TCancelable>(int opCodesSincePoll) where TCancelable : struct, IFlag
    {
        if (!TCancelable.IsActive)
            return 0;

        Debug.Assert(opCodesSincePoll >= 0, "A runner cannot have run a negative number of opcodes since the last poll.");
        // A negative report would only postpone the next poll; clamp it so a faulty runner cannot.
        return Math.Max(opCodesSincePoll, 0);
    }

    partial void PrepareFrameCodeRunner<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag
    {
        if (!TTracingInst.IsActive && _frameCodeRunner is not null)
            _frameCodeBodies ??= GetOpcodeBodySet(spec);
    }

    // Weak keys, as for the dispatch tables: transient override and simulation specs must stay collectable.
    private static readonly ConditionalWeakTable<IReleaseSpec, OpcodeBodySet> _bodySetsBySpec = [];
    private static readonly ConcurrentDictionary<BodySetKey, OpcodeBodySet> _internedBodySets = new();

    /// <summary>Set only while <see cref="DescribeBodies"/> builds its table, which then records each entry's body here.</summary>
    [ThreadStatic]
    private static Dictionary<nint, OpcodeBody>? _bodyRecorder;

    /// <summary>What each entry of <paramref name="spec"/>'s untraced dispatch table runs; equal sets are one instance.</summary>
    /// <remarks>The first call for a spec instance builds a table, which takes tens of microseconds.</remarks>
    [Experimental("NMEVM0001")]
    public static OpcodeBodySet GetOpcodeBodySet(IReleaseSpec spec) => _bodySetsBySpec.GetValue(spec, static s => DescribeBodies(s));

    /// <summary>Builds an untraced table of its own for <paramref name="spec"/>, recording the body behind every entry.</summary>
    /// <remarks>
    /// The table factories record their own handler as they build it, so the entries map back to the exact bodies the
    /// interpreter runs for the fork, fast-path variants included. The cached dispatch tables are not touched.
    /// </remarks>
    private static OpcodeBodySet DescribeBodies(IReleaseSpec spec)
    {
        Dictionary<nint, OpcodeBody>? outer = _bodyRecorder;
        Dictionary<nint, OpcodeBody> recorder = new(2 * FallbackHandlersOffset);
        _bodyRecorder = recorder;
        delegate*<ref EvmStack, ref TGasPolicy, ref DispatchState, nint, nint, EvmExceptionType>[] table;
        try
        {
            table = GenerateOpcodeHandlers<OffFlag, OffFlag>(spec);
        }
        finally
        {
            _bodyRecorder = outer;
        }

        OpcodeBody[] bodies = new OpcodeBody[byte.MaxValue + 1];
        for (int opcode = 0; opcode < bodies.Length; opcode++)
            bodies[opcode] = recorder.TryGetValue((nint)table[opcode], out OpcodeBody? body) ? body : OpcodeBody.Unrecorded;

        return _internedBodySets.GetOrAdd(new BodySetKey(bodies, spec.IsEip7979Enabled), static key => new OpcodeBodySet(key.Bodies, key.UsesCallDestinations));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordBody<TOpcode>(nint handler, OpcodeFlow flow) where TOpcode : struct, IOpcodeBody =>
        _bodyRecorder?.TryAdd(handler, flow == OpcodeFlow.Terminates ? BodyDescriptions<TOpcode>.Terminating : BodyDescriptions<TOpcode>.Continuing);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RecordJumpIf(nint handler) => _bodyRecorder?.TryAdd(handler, _jumpIfBody);

    private static readonly OpcodeBody _jumpIfBody = new(null, 0, OpcodeFlow.ConditionalJump, isDefined: true, hasCheckedBody: false, usesVm: true,
        chargesFixedGas: true, mayJump: true, hasUntracedFastPath: false, stackInputs: 2, stackGrowth: -2, fixedGas: JumpIGasCost.GasCost);

    /// <summary>A body's own static declarations, read once per body type and flow.</summary>
    private static class BodyDescriptions<TOpcode> where TOpcode : struct, IOpcodeBody
    {
        public static readonly OpcodeBody Continuing = Describe(OpcodeFlow.Continues);
        public static readonly OpcodeBody Terminating = Describe(OpcodeFlow.Terminates);

        private static OpcodeBody Describe(OpcodeFlow flow) => new(typeof(TOpcode),
            (nint)(delegate*<ref EvmStack, ref TGasPolicy, VirtualMachine<TGasPolicy>, ref nint, EvmExceptionType>)&TOpcode.Execute, flow, typeof(TOpcode) != typeof(BadInstructionOpcode),
            TOpcode.HasCheckedBody, TOpcode.UsesVm, TOpcode.ChargesFixedGas, TOpcode.MayJump, TOpcode.HasUntracedFastPath,
            TOpcode.StackInputs, TOpcode.StackGrowth, TOpcode.HasCheckedBody ? MeasureFixedGas() : 0);

        /// <summary>What the body's gas check charges, from a probe with ample gas.</summary>
        private static ulong MeasureFixedGas()
        {
            const ulong Probe = 1_000_000;
            TGasPolicy gas = TGasPolicy.FromULong(Probe);
            return TOpcode.TryConsumeGas(ref gas) ? Probe - TGasPolicy.GetRemainingGas(in gas) : ulong.MaxValue;
        }
    }

    /// <summary>A body set by the identity of its 256 entries and its EIP-7979 flag.</summary>
    private readonly struct BodySetKey : IEquatable<BodySetKey>
    {
        private readonly int _hash;

        public BodySetKey(OpcodeBody[] bodies, bool usesCallDestinations)
        {
            Bodies = bodies;
            UsesCallDestinations = usesCallDestinations;
            HashCode hash = new();
            foreach (OpcodeBody body in bodies)
                hash.Add(RuntimeHelpers.GetHashCode(body));
            hash.Add(usesCallDestinations);
            _hash = hash.ToHashCode();
        }

        public OpcodeBody[] Bodies { get; }
        public bool UsesCallDestinations { get; }

        public bool Equals(BodySetKey other)
        {
            if (_hash != other._hash || UsesCallDestinations != other.UsesCallDestinations)
                return false;
            for (int i = 0; i < Bodies.Length; i++)
            {
                if (!ReferenceEquals(Bodies[i], other.Bodies[i]))
                    return false;
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is BodySetKey other && Equals(other);

        public override int GetHashCode() => _hash;
    }
}
