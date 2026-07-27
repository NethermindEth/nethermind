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

    // Cache the dispatch tables in plain per-TGasPolicy statics: the guest executes a single fork, and
    // ConditionalWeakTable (used by the std build) relies on GC dependent-handles the zkEVM guest can't map.
    private static delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[]? _opcodesNoTrace;
    private static delegate*<VirtualMachine<TGasPolicy>, ref EvmStack, ref TGasPolicy, ref int, EvmExceptionType>[]? _opcodesTraced;

    private partial void PrepareOpcodes<TTracingInst>(IReleaseSpec spec) where TTracingInst : struct, IFlag =>
        _opcodeMethods = !TTracingInst.IsActive
            ? _opcodesNoTrace ??= GenerateOpCodes<TTracingInst>(spec)
            : _opcodesTraced ??= GenerateOpCodes<TTracingInst>(spec);

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
        scoped ref EvmStack stack,
        bool newAccountCharged)
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
            snapshot: in snapshot,
            newAccountCharged: newAccountCharged);

        CallResult callResult = ExecutePrecompile(child, _isTracingActionsCached, out Exception? failure, out _);

        if (failure is not null)
        {
            // Precompile hard failure (out of gas): mirror HandleFailure + PopAndRestoreParentState.
            _worldState.Restore(child.Snapshot);
            RevertParityTouchBugAccount();
            RemoveAdvancedStateGasRefund(child, ref child.Gas);
            TGasPolicy.RestoreChildStateGasOnHalt(ref parent.Gas, in child.Gas);
            // EIP-8037: the failed call did not create its (dead) recipient; refund NEW_ACCOUNT.
            if (child.NewAccountCharged)
                CreditStateGasRefund(ref parent.Gas, TGasPolicy.GetNewAccountStateCost());
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
            // EIP-8037: the reverted call did not create its (dead) recipient; refund NEW_ACCOUNT.
            if (child.NewAccountCharged)
                CreditStateGasRefund(ref parent.Gas, TGasPolicy.GetNewAccountStateCost());
        }

        ReturnDataBuffer = callResult.Output;
        EvmExceptionType push = stack.PushBytes<TTracingInst>(
            (reverted ? StatusCode.FailureBytes : StatusCode.SuccessBytes).Span);

        if (push == EvmExceptionType.None && outputLength > 0 && callResult.Output.Length > 0)
        {
            ReadOnlySpan<byte> output = callResult.Output.Span[..Math.Min(callResult.Output.Length, (int)outputLength)];
            UInt256 dest = (ulong)outputDestination;
            if (!TGasPolicy.UpdateMemoryCost(ref parent.Gas, in dest, (ulong)output.Length, ref parent.Memory))
            {
                push = EvmExceptionType.OutOfGas;
            }
            else
            {
                parent.Memory.SaveAfterGas(in dest, output);
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
