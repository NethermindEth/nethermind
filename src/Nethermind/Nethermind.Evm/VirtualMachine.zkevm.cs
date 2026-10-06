// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using Nethermind.Int256;

namespace Nethermind.Evm;

public unsafe partial class VirtualMachine<TGasPolicy> where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    // Keeping this call boundary reduces guest execution cost.
    private const MethodImplOptions ExecutionHandlersInlining = MethodImplOptions.NoInlining;

    // Cache the dispatch tables in plain per-TGasPolicy statics: the guest executes a single fork, and
    // ConditionalWeakTable (used by the std build) relies on GC dependent-handles the zkEVM guest can't map.
    private static readonly OpcodeTable _opcodeTable = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static OpcodeTable GetOpcodeTable() => _opcodeTable;

    /// <inheritdoc/>
    /// <remarks>The guest is compiled ahead of time, so a rebuilt table has no promoted code to capture.</remarks>
    private partial bool ShouldRefreshOpcodes() => false;

    /// <summary>Resolves the untraced dispatch table and the current fork's own frame handlers, for tests that enter a frame directly.</summary>
    /// <remarks>The shared table keeps the frame handlers of the first fork it prepares, so they are rebuilt for the current spec.</remarks>
    internal void PrepareFrameHandlersForTests()
    {
        PrepareOpcodes<OffFlag>();
        _executionHandlers = new ExecutionHandlers(Spec);
    }

    /// <summary>Whether a fresh untraced table for <paramref name="spec"/> runs SLOAD on the guest handler.</summary>
    /// <remarks>Built apart from the shared table, which keeps the handlers of the first fork it prepares.</remarks>
    internal static bool LoadsStorageThroughGuestHandlerForTests(IReleaseSpec spec) =>
        (nint)GenerateOpcodeHandlers<OffFlag, OffFlag>(spec)[(int)Instruction.SLOAD] == (nint)AsTableEntry(&RawCalliHelper.ExecuteSLoad);

    public object? ReturnData;

    /// <summary>
    /// <see cref="InitializeFrameCore{Eip158}"/> under EIP-158, minus the zero credit to the executing account of a frame that runs code.
    /// </summary>
    /// <remarks>
    /// That account is never empty: under CALL, STATICCALL and a transaction it holds the code, or an EIP-7702 designator
    /// to it; under DELEGATECALL and CALLCODE it is the caller, itself running code or a creation whose nonce is already 1.
    /// Crediting it zero neither creates nor touches it, yet costs two account lookups on every value-less call.
    /// Not selected under EIP-7928, whose access-list tracking observes the credit.
    /// </remarks>
    private static bool InitializeFrameSkippingNoOpCredit(VirtualMachine<TGasPolicy> vm, VmState<TGasPolicy> state)
    {
        ExecutionEnvironment env = state.Env;
        ExecutionType executionType = state.ExecutionType;
        if (!executionType.IsAnyCreate() && env.CodeInfo.CodeLength != 0 && executionType.GetBalanceCredit(in env.Value).IsZero)
            return true;

        return InitializeFrameCore<OnFlag, OffFlag>(vm, state);
    }

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

        CallResult callResult = ExecutePrecompile(child, isTracingActions: false, out Exception? failure, out _);

        if (failure is not null)
        {
            // Precompile hard failure (out of gas): mirror HandleFailure + PopAndRestoreParentState.
            _worldState.Restore(child.Snapshot);
            VirtualMachineStatics.RestoreRipemdTouch(_worldState, BlockExecutionContext.Spec, _shouldRestoreRipemdTouch);
            RemoveAdvancedStateGasRefund(child, ref child.Gas);
            TGasPolicy.RestoreChildStateGasOnHalt(ref parent.Gas, in child.Gas);
            // EIP-8037: the failed call did not create its (dead) recipient; refund NEW_ACCOUNT.
            if (child.NewAccountCharged)
                CreditStateGasRefund(ref parent.Gas, TGasPolicy.GetNewAccountStateCost());
            child.Dispose();
            ReturnDataBuffer = default;
            return stack.PushZero<TTracingInst, OnFlag>();
        }

        bool reverted = callResult.ShouldRevert;
        if (!reverted)
        {
            IncorporateChildStateGasRefunds(child);
            TGasPolicy.Refund(ref parent.Gas, in child.Gas);
            TGasPolicy.RepayStateGasSpill(ref parent.Gas);
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
            VirtualMachineStatics.RestoreRipemdTouch(_worldState, BlockExecutionContext.Spec, _shouldRestoreRipemdTouch);
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
