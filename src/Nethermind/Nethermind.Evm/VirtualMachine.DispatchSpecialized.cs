// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.Core;
#if DEBUG
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.Debugger;
#endif

namespace Nethermind.Evm;

/// <summary>
/// The interpreter dispatch loop: a direct <c>switch</c> over the hot opcodes so the JIT can inline their
/// handlers, with the rest falling through to the per-fork table. Fork gates are compile-time
/// <see cref="IFlag"/> type args (<c>TShift</c>: EIP-145, <c>TPush0</c>: EIP-3855) that the JIT folds.
/// </summary>
public unsafe partial class VirtualMachine<TGasPolicy>
{
    // Poll cancellation every 1024 opcodes (low bits of the per-frame op counter).
    private const int CancellationCheckMask = 1023;

    /// <summary>Runs the current frame's bytecode until it halts, faults, or yields a child frame.</summary>
    /// <param name="programCounter">On entry the offset to resume from; on exit the offset reached.</param>
    /// <returns>The halting reason; <c>None</c>, <c>Stop</c> and <c>Revert</c> are normal halts.</returns>
    /// <remarks>
    /// Implemented per build, split at the loop rather than the differing <c>switch</c>: direct dispatch
    /// only beats the table while the JIT inlines the handlers into the switch.
    /// </remarks>
    private partial EvmExceptionType RunDispatchLoop<TTracingInst, TCancelable, TShift, TPush0>(
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas,
        ref nint programCounter)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where TShift : struct, IFlag
        where TPush0 : struct, IFlag;

    [SkipLocalsInit]
    private CallResult RunByteCodeCore<TTracingInst, TCancelable, TShift, TPush0>(
        scoped ref EvmStack stack,
        scoped ref TGasPolicy gas)
        where TTracingInst : struct, IFlag
        where TCancelable : struct, IFlag
        where TShift : struct, IFlag
        where TPush0 : struct, IFlag
    {
        ReturnData = null;
#if DEBUG
        DebugTracer<TGasPolicy>? debugger = _txTracer.GetTracer<DebugTracer<TGasPolicy>>();
#endif

        // May not be zero when resuming after a call.
        nint programCounter = VmState.ProgramCounter;
        EvmExceptionType exceptionType =
            RunDispatchLoop<TTracingInst, TCancelable, TShift, TPush0>(ref stack, ref gas, ref programCounter);

        if (exceptionType is EvmExceptionType.None or EvmExceptionType.Stop or EvmExceptionType.Revert)
        {
            if (TTracingInst.IsActive)
                EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));
            UpdateCurrentState((int)programCounter, in gas, stack.Head);
        }
        else
        {
            goto ReturnFailure;
        }

        if (exceptionType == EvmExceptionType.Revert)
            goto Revert;
        if (ReturnData is not null)
            goto DataReturn;

#if DEBUG
        debugger?.TryWait(ref _currentState, ref programCounter, ref gas, ref stack.Head);
#endif
        return CallResult.Empty();

    DataReturn:
#if DEBUG
        debugger?.TryWait(ref _currentState, ref programCounter, ref gas, ref stack.Head);
#endif
        // A nested frame is the common outcome here, and it is the cheaper test: an array `isinst` needs
        // the general helper, while a class one has a specialized fast path. Order them accordingly.
        if (ReturnData is VmState<TGasPolicy> state)
        {
            return new CallResult(state);
        }
        else if (ReturnData is byte[] data)
        {
            return new CallResult(data, null);
        }
        return new CallResult(ReturnDataBuffer, null);

    Revert:
        return new CallResult((byte[])ReturnData, null, shouldRevert: true, exceptionType);

    ReturnFailure:
        // EIP-8037: write gas back so RestoreChildStateGasOnHalt can read the child frame's state gas.
        _currentState.Gas = gas;
        return GetFailureReturn(TGasPolicy.GetRemainingGas(in gas), exceptionType);
    }
}
