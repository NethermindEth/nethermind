// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using Nethermind.Evm.State;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

public static partial class EvmInstructions
{
    /// <summary>
    /// Interface defining the execution type for a call-like opcode.
    /// </summary>
    public interface IOpCall
    {
        /// <summary>
        /// Returns the specific execution type of the call.
        /// </summary>
        abstract static ExecutionType ExecutionType { get; }
    }

    /// <summary>
    /// Represents a normal CALL opcode.
    /// </summary>
    public struct OpCall : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.CALL;
    }

    /// <summary>
    /// Represents a CALLCODE opcode.
    /// </summary>
    public struct OpCallCode : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.CALLCODE;
    }

    /// <summary>
    /// Represents a DELEGATECALL opcode.
    /// </summary>
    public struct OpDelegateCall : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.DELEGATECALL;
    }

    /// <summary>
    /// Represents a STATICCALL opcode.
    /// </summary>
    public struct OpStaticCall : IOpCall
    {
        public static ExecutionType ExecutionType => ExecutionType.STATICCALL;
    }

    /// <summary>
    /// Executes a call-like operation.
    /// This method handles various call types (CALL, CALLCODE, DELEGATECALL, STATICCALL) by:
    /// - Popping call parameters from the stack,
    /// - Charging appropriate gas for the call and memory expansion,
    /// - Validating call conditions (such as static call restrictions and call depth),
    /// - Performing balance transfers,
    /// - Creating a new execution frame for the call.
    /// </summary>
    /// <typeparam name="TOpCall">
    /// The call opcode type (e.g. <see cref="OpCall"/>, <see cref="OpStaticCall"/>).
    /// </typeparam>
    /// <typeparam name="TTracingInst">
    /// A type implementing <see cref="IFlag"/> that indicates whether instruction tracing is active.
    /// </typeparam>
    /// <param name="vm">The current virtual machine instance containing execution state.</param>
    /// <param name="stack">The EVM stack for retrieving call parameters and pushing results.</param>
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the current program counter (not modified by this method).</param>
    /// <returns>
    /// An <see cref="EvmExceptionType"/> value indicating success or the type of error encountered.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionCall<TGasPolicy, TOpCall, TTracingInst, TEip8037, TEip7708>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCall : struct, IOpCall
        where TTracingInst : struct, IFlag
        where TEip8037 : struct, IFlag
        where TEip7708 : struct, IFlag
    {
        // Increment global call metrics.
        Metrics.IncrementCalls();

        // Clear previous return data.
        vm.ReturnData = null;

        // Pop the gas limit for the call.
        if (!stack.PopUInt256(out UInt256 gasLimit)) goto StackUnderflow;
        // Pop the code source address from the stack.
        Address codeSource = stack.PopAddress(vm.AddressCache);
        if (codeSource is null) goto StackUnderflow;

        ExecutionEnvironment env = vm.VmState.Env;
        // Determine the call value based on the call type.
        UInt256 callValue;
        if (TOpCall.ExecutionType == ExecutionType.STATICCALL)
        {
            // Static calls cannot transfer value.
            callValue = default;
        }
        else if (TOpCall.ExecutionType == ExecutionType.DELEGATECALL)
        {
            // Delegate calls use the value from the current execution context.
            callValue = env.Value;
        }
        else if (!stack.PopUInt256(out callValue))
        {
            goto StackUnderflow;
        }

        // Pop additional parameters: data offset, data length, output offset, and output length.
        if (!stack.PopUInt256(out UInt256 dataOffset, out UInt256 dataLength, out UInt256 outputOffset, out UInt256 outputLength))
        {
            goto StackUnderflow;
        }

        bool hasValueTransfer = TOpCall.ExecutionType != ExecutionType.DELEGATECALL && !callValue.IsZero;
        // Enforce static call restrictions: no value transfer allowed unless it's a CALLCODE.
        if (vm.VmState.IsStatic && hasValueTransfer && TOpCall.ExecutionType != ExecutionType.CALLCODE)
            return EvmExceptionType.StaticCallViolation;

        // Determine caller and target based on the call type.
        Address caller = TOpCall.ExecutionType == ExecutionType.DELEGATECALL ? env.Caller : env.ExecutingAccount;
        Address target = TOpCall.ExecutionType != ExecutionType.DELEGATECALL && TOpCall.ExecutionType != ExecutionType.CALLCODE
            ? codeSource
            : env.ExecutingAccount;

        IReleaseSpec spec = vm.Spec;
        IWorldState state = vm.WorldState;

        // Add extra gas cost if value is transferred. EIP-2780 reprices this into a three-tier
        // charge keyed on self-call and recipient existence, subsuming the new-account surcharge.
        if (hasValueTransfer)
        {
            bool valueOutOfGas;
            if (spec.IsEip2780Enabled)
            {
                // EIP-8038 prices the value transfer as a flat charge independent of the recipient's
                // liveness, so do not read the target here: the spec performs the static gas check
                // before any state access, and a CALL that runs out of gas before the target access
                // must not record the target in the block access list. The recipient-empty surcharge
                // only applies to the older EIP-2780 tiered model (no BAL), so read lazily for it.
                bool recipientEmpty = !spec.IsEip8038Enabled && state.IsDeadAccount(target);
                valueOutOfGas = !TGasPolicy.ConsumeCallValueTransferEip2780(ref gas, caller == target, recipientEmpty, spec);
            }
            else
            {
                valueOutOfGas = !TGasPolicy.ConsumeCallValueTransfer(ref gas);
            }
            if (valueOutOfGas) goto OutOfGas;
        }

        // Update gas: call cost and memory expansion for input and output.
        if (!TGasPolicy.ConsumeCallBaseGas(ref gas, spec) ||
            !TGasPolicy.UpdateMemoryCost(ref gas, in dataOffset, dataLength, ref vm.VmState.Memory) ||
            !TGasPolicy.UpdateMemoryCost(ref gas, in outputOffset, outputLength, ref vm.VmState.Memory))
            goto OutOfGas;

        // Charge gas for accessing the account's code (including delegation logic if applicable).
        // EIP-2780 charges a cold code-less account less; delegated accounts always carry code.
        if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, vm.Spec, in vm.VmState.AccessTracker,
                vm.TxTracer.IsTracingAccess, codeSource,
                hasCode: !spec.IsEip2780Enabled || spec.IsEip8038Enabled || state.IsContract(codeSource))) goto OutOfGas;

        CodeInfo codeInfo = vm.CodeInfoRepository.GetCachedCodeInfo(codeSource, followDelegation: false, vmSpec: spec, delegationAddress: out Address? delegated);

        if (spec.UseHotAndColdStorage &&
            delegated is not null &&
            !TGasPolicy.ConsumeAccountAccessGas(ref gas, vm.Spec, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, delegated))
            goto OutOfGas;

        // Charge additional gas if the target account is new or considered empty.
        // EIP-8038 charges a value transfer to a dead recipient the NEW_ACCOUNT state cost (separate
        // from the flat CALL_VALUE above). The earlier EIP-2780 draft folded creation into the
        // value-transfer tier, so it charges nothing extra here.
        bool chargesNewAccount = spec.IsEip8038Enabled
            ? hasValueTransfer && state.IsDeadAccount(target)
            : !spec.IsEip2780Enabled && (spec.ClearEmptyAccountWhenTouched switch
            {
                false => !state.AccountExists(target),
                true => hasValueTransfer && state.IsDeadAccount(target),
            });

        bool newAccountOutOfGas = chargesNewAccount && !TGasPolicy.ConsumeNewAccountCreation<TEip8037>(ref gas);

        if (newAccountOutOfGas) goto OutOfGas;

        // EIP-7702: load delegated code after cold-access charge above.
        if (delegated is not null)
        {
            // EIP-7928: decorator fast-path skips world-state reads; record explicitly.
            state.AddAccountRead(delegated);

            // EIP-7702: precompile MUST NOT execute via delegation; the decorator would route to the precompile CodeInfo.
            codeInfo = spec.IsPrecompile(delegated)
                ? CodeInfo.Empty
                : vm.CodeInfoRepository.GetCachedCodeInfoNoDelegation(delegated, spec);
        }

        // EIP-150: forward the requested gas to the child frame, capped at 63/64 of remaining.
        if (!TGasPolicy.TryReserveChildGas(ref gas, in gasLimit, spec, out ulong gasLimitUl)) goto OutOfGas;

        // Add call stipend if value is being transferred.
        if (hasValueTransfer)
        {
            if (vm.TxTracer.IsTracingRefunds)
                vm.TxTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
            gasLimitUl += GasCostOf.CallStipend;
        }

        // Check call depth and balance of the caller.
        if (env.CallDepth >= MaxCallDepth ||
            (hasValueTransfer && state.GetBalance(env.ExecutingAccount) < callValue))
        {
            // If the call cannot proceed, return an empty response and push zero on the stack.
            vm.ReturnDataBuffer = Array.Empty<byte>();
            EvmExceptionType pushResult = stack.PushZero<TTracingInst>();

            // Optionally report memory changes for refund tracing.
            if (vm.TxTracer.IsTracingRefunds)
            {
                // Specific to Parity tracing: inspect 32 bytes from data offset.
                ReadOnlyMemory<byte>? memoryTrace = vm.VmState.Memory.Inspect(in dataOffset, 32);
                vm.TxTracer.ReportMemoryChange(dataOffset, memoryTrace is null ? default : memoryTrace.Value.Span);
            }

            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportOperationRemainingGas(TGasPolicy.GetRemainingGas(in gas));
                vm.TxTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);
            }

            // Refund the remaining gas to the caller.
            TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
            // EIP-8037: a value transfer to a new account charges NEW_ACCOUNT state gas up-front; when the
            // call cannot proceed (call depth exceeded or caller balance too low) no account is created, so
            // refund it. No-op pre-EIP-8037 (CreditStateGasRefund self-gates), matching legacy semantics.
            if (chargesNewAccount)
                vm.CreditStateGasRefund(ref gas, TGasPolicy.GetNewAccountStateCost(in gas));
            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportGasUpdateForVmTrace(gasLimitUl, TGasPolicy.GetRemainingGas(in gas));
            }
            return pushResult;
        }

        // Fast-path for calls to externally owned accounts (non-contracts)
        if (codeInfo.IsEmpty && !TTracingInst.IsActive && !vm.TxTracer.IsTracingActions)
        {
            vm.ReturnDataBuffer = default;
            // Mutate balances only after the success byte is on the stack; this fast path has no snapshot to roll back a failed push.
            EvmExceptionType pushResult = stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);
            if (pushResult != EvmExceptionType.None) return pushResult;
            TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
            // Self-call (always true for CALLCODE; runtime for CALL/STATICCALL when target == executing account):
            // the +/- value balance ops cancel and target is the currently-executing account (alive),
            // so skip both writes. AddTransferLog already no-ops when from == to.
            bool isSelfCall = caller == target;
            if (!isSelfCall)
            {
                if (hasValueTransfer)
                {
                    state.SubtractFromBalance(caller, in callValue, spec);
                    vm.AddTransferLog<TEip7708>(caller, target, in callValue);
                }
                state.AddToBalanceAndCreateIfNotExists(target, TOpCall.ExecutionType, in callValue, spec);
            }
            Metrics.IncrementEmptyCalls();
            vm.ReturnData = null;
            return EvmExceptionType.None;
        }

        return CreateFullCallFrame(vm, ref stack, ref gas, in dataOffset, dataLength, outputOffset, outputLength, codeInfo, target, caller, codeSource, env, in callValue, gasLimitUl, chargesNewAccount);

        // Mainline keeps this out-of-line for icache locality on the common path. The zkVM guest
        // has no icache and counts instructions, so the NoInlining call and its wide argument
        // marshalling are pure overhead on every CALL; inline it, pulling the hot precompile path in too.
#if ZK_EVM
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#else
        [MethodImpl(MethodImplOptions.NoInlining)]
#endif
        static EvmExceptionType CreateFullCallFrame(
            VirtualMachine<TGasPolicy> vm,
            ref EvmStack stack,
            ref TGasPolicy gas,
            in UInt256 dataOffset,
            UInt256 dataLength,
            UInt256 outputOffset,
            UInt256 outputLength,
            CodeInfo codeInfo,
            Address target,
            Address caller,
            Address codeSource,
            ExecutionEnvironment env,
            in UInt256 callValue,
            ulong gasLimitUl,
            bool newAccountCharged)
        {
            IWorldState state = vm.WorldState;
            // Take a snapshot of the state for potential rollback.
            Snapshot snapshot = state.TakeSnapshot();
            // Subtract the transfer value from the caller's balance.
            if (TOpCall.ExecutionType != ExecutionType.DELEGATECALL && !callValue.IsZero) state.SubtractFromBalance(caller, in callValue, vm.Spec);

            // Load call data from memory.
            if (!vm.VmState.Memory.TryLoad(in dataOffset, dataLength, out ReadOnlyMemory<byte> callData))
                return EvmExceptionType.OutOfGas;
            // Construct the execution environment for the call.
            ExecutionEnvironment callEnv = ExecutionEnvironment.Rent(
                codeInfo: codeInfo,
                executingAccount: target,
                caller: caller,
                codeSource: codeSource,
                callDepth: env.CallDepth + 1,
                value: in callValue,
                inputData: in callData);

            // Normalize output offset if output length is zero.
            if (outputLength.IsZero)
            {
                // Output offset is inconsequential when output length is 0.
                outputOffset = default;
            }

            TGasPolicy childGas = TGasPolicy.CreateChildFrameGas(ref gas, gasLimitUl);

#if ZK_EVM
            // Precompiles run no bytecode: handle them inline, skipping the child
            // frame's round trip through the ExecuteTransaction dispatch loop.
            if (codeInfo.IsPrecompile)
            {
                return vm.InlinePrecompileCall<TTracingInst>(
                    callEnv,
                    childGas,
                    outputOffset.ToLong(),
                    outputLength.ToLong(),
                    TOpCall.ExecutionType,
                    TOpCall.ExecutionType == ExecutionType.STATICCALL || vm.VmState.IsStatic,
                    in snapshot,
                    ref stack);
            }
#endif

            // Rent a new call frame for executing the call.
            vm.ReturnData = VmState<TGasPolicy>.RentFrame(
                gas: childGas,
                outputDestination: outputOffset.ToLong(),
                outputLength: outputLength.ToLong(),
                executionType: TOpCall.ExecutionType,
                isStatic: TOpCall.ExecutionType == ExecutionType.STATICCALL || vm.VmState.IsStatic,
                isCreateOnPreExistingAccount: false,
                env: callEnv,
                stateForAccessLists: in vm.VmState.AccessTracker,
                snapshot: in snapshot,
                // EIP-8037/EIP-8038: a value transfer to a dead recipient charged NEW_ACCOUNT state gas up-front;
                // refunded on this frame's failure path (revert/halt) since the account is then not created.
                newAccountCharged: newAccountCharged);

            return EvmExceptionType.None;
        }

        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    }

    /// <summary>
    /// Executes the RETURN opcode.
    /// Pops a memory offset and a length from the stack, updates memory cost, and sets the return data.
    /// Returns an error if the opcode is executed in an invalid context.
    /// </summary>
    /// <param name="vm">The current virtual machine instance.</param>
    /// <param name="stack">The EVM stack from which the offset and length are popped.</param>
    /// <param name="gas">The gas which is updated by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter (unused in this operation).</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; otherwise, an error such as <see cref="EvmExceptionType.StackUnderflow"/>,
    /// <see cref="EvmExceptionType.OutOfGas"/>, or <see cref="EvmExceptionType.BadInstruction"/>.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionReturn<TGasPolicy>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        // Pop memory position and length for the return data.
        if (!stack.PopUInt256(out UInt256 position, out UInt256 length))
            goto StackUnderflow;

        // Update the memory cost for the region being returned.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in position, in length, ref vm.VmState.Memory) ||
            !vm.VmState.Memory.TryLoad(in position, in length, out ReadOnlyMemory<byte> returnData))
        {
            goto OutOfGas;
        }

        vm.ReturnData = returnData.ToArray();

        return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
