// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] _opcodeMethods;

    // Select and lazily build the opcode dispatch table for the active tracing mode, caching each
    // mode separately on the spec. Mirrors the std build minus its periodic PGO-driven cache refresh,
    // which is moot for the AOT-compiled guest.
    private partial void PrepareOpcodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag
    {
        if (!TTracingInst.IsActive)
        {
            _opcodeMethods =
                (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[])
                (spec.EvmInstructionsNoTrace ??= GenerateOpCodes<TTracingInst>(spec));
        }
        else
        {
            _opcodeMethods =
                (delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[])
                (spec.EvmInstructionsTraced ??= GenerateOpCodes<TTracingInst>(spec));
        }
    }

    protected delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[] GenerateOpCodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag =>
        EvmInstructions.GenerateOpCodes<TGasPolicy, TTracingInst>(spec);

    /// <summary>
    /// Inline handling of a CALL whose target is a precompile. Precompiles run
    /// no bytecode, so instead of handing a child frame to the ExecuteTransaction
    /// dispatch loop we run the precompile and resume the calling frame here.
    /// Mirrors the loop's frame-finished handling for the (non-create) case.
    /// </summary>
    /// <remarks>Omits the action-end/revert tracing the mainline return does; the guest never action-traces.</remarks>
    internal EvmExceptionType InlinePrecompileCall<TTracingInst>(
        ExecutionEnvironment callEnv,
        TGasPolicy childGas,
        long outputDestination,
        long outputLength,
        ExecutionType executionType,
        bool isStatic,
        scoped in Snapshot snapshot,
        scoped ref EvmStack stack)
        where TTracingInst : struct, IFlag
    {
        VmState<TGasPolicy> parent = _currentState;
        VmState<TGasPolicy> child = VmState<TGasPolicy>.RentFrame(
            gas: childGas,
            outputDestination: outputDestination,
            outputLength: outputLength,
            executionType: executionType,
            isStatic: isStatic,
            isCreateOnPreExistingAccount: false,
            env: callEnv,
            stateForAccessLists: in parent.AccessTracker,
            snapshot: in snapshot);

        CallResult callResult = ExecutePrecompile(child, _isTracingActionsCached, out Exception? failure, out _);

        if (failure is not null)
        {
            // Precompile hard failure (out of gas): mirror HandleFailure + PopAndRestoreParentState.
            _worldState.Restore(child.Snapshot);
            RevertParityTouchBugAccount();
            RemoveAdvancedStateGasRefund(child, ref child.Gas);
            TGasPolicy.RestoreChildStateGasOnHalt(ref parent.Gas, in child.Gas);
            child.Dispose();
            ReturnDataBuffer = Array.Empty<byte>();
            return stack.PushZero<TTracingInst>();
        }

        bool reverted = callResult.ShouldRevert;
        if (!reverted)
        {
            IncorporateChildStateGasRefunds(child);
            TGasPolicy.Refund(ref parent.Gas, in child.Gas);
        }
        else
        {
            TGasPolicy.UpdateGasUp(ref parent.Gas, TGasPolicy.GetRemainingGas(in child.Gas));
            RemoveAdvancedStateGasRefund(child, ref child.Gas);
            TGasPolicy.RestoreChildStateGas(ref parent.Gas, in child.Gas);
        }

        ReturnDataBuffer = callResult.Output;
        EvmExceptionType push = stack.PushBytes<TTracingInst>(
            (reverted ? StatusCode.FailureBytes : StatusCode.SuccessBytes).Span);

        if (push == EvmExceptionType.None && outputLength > 0 && callResult.Output.Length > 0)
        {
            ZeroPaddedSpan outSlice = callResult.Output.Span
                .SliceWithZeroPadding(0, Math.Min(callResult.Output.Length, (int)outputLength));
            UInt256 dest = (ulong)outputDestination;
            if (!TGasPolicy.UpdateMemoryCost(ref parent.Gas, in dest, (ulong)outSlice.Length, parent))
            {
                push = EvmExceptionType.OutOfGas;
            }
            else
            {
                parent.Memory.TrySave(in dest, outSlice);
            }
        }

        if (reverted)
        {
            _worldState.Restore(child.Snapshot);
        }
        else
        {
            // The precompile succeeded, so its state is committed even when `push` was just set to
            // OutOfGas by the output-copy memory expansion above. Per EIP-2929 the warm/cold access
            // set is not reverted on call failure, and any account-state changes are still guarded by
            // the enclosing transaction snapshot, which unwinds them if the parent frame later halts.
            child.CommitToParent(parent);
        }
        child.Dispose();
        return push;
    }
}
