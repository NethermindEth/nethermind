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

internal static partial class EvmInstructions
{
    /// <summary>
    /// Interface defining the properties for a call-like opcode.
    /// Each implementation specifies whether the call is static and what its execution type is.
    /// </summary>
    public interface IOpCall
    {
        /// <summary>
        /// Indicates if the call is static.
        /// Static calls cannot modify state.
        /// </summary>
        virtual static bool IsStatic => false;

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
        public static bool IsStatic => true;
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
    public static OpcodeResult InstructionCall<TGasPolicy, TOpCall, TTracingInst, EIP150, EIP158, EIP7907>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCall : struct, IOpCall
        where TTracingInst : struct, IFlag
        where EIP150 : struct, IFlag
        where EIP158 : struct, IFlag
        where EIP7907 : struct, IFlag
    {
        // Increment global call metrics.
        Metrics.IncrementCalls();

        // Clear previous return data.
        vm.ReturnData = null;

        // Pop the gas limit for the call.
        if (!stack.PopUInt256(out UInt256 gasLimit)) goto StackUnderflow;
        // Pop the code source address from the stack.
        Address codeSource = stack.PopAddress();
        if (codeSource is null) goto StackUnderflow;

        ExecutionEnvironment env = vm.VmState.Env;
        // Determine the call value based on the call type.
        UInt256 callValue;
        if (TOpCall.IsStatic)
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
        if (!stack.PopUInt256(
            out UInt256 dataOffset,
            out UInt256 dataLength,
            out UInt256 outputOffset,
            out UInt256 outputLength))
        {
            goto StackUnderflow;
        }

        IReleaseSpec spec = vm.Spec;
        // Charge gas for accessing the account's code (including delegation logic if applicable).
        bool _ = vm.TxExecutionContext.CodeInfoRepository
            .TryGetDelegation(codeSource, spec, out ICodeInfo codeInfo, out Address delegated);
        if (!TGasPolicy.ConsumeAccountAccessGasWithDelegation(ref gas, spec, in vm.VmState.AccessTracker,
                vm.TxTracer.IsTracingAccess, codeSource, delegated)) goto OutOfGas;

        // For non-delegate calls, the transfer value is the call value.
        UInt256 transferValue;
        bool isTransferZero;
        if (TOpCall.ExecutionType == ExecutionType.DELEGATECALL || callValue.IsZero)
        {
            transferValue = default;
            isTransferZero = true;
        }
        else
        {
            transferValue = callValue;
            isTransferZero = false;
        }
        // Enforce static call restrictions: no value transfer allowed unless it's a CALLCODE.
        if (vm.VmState.IsStatic && !isTransferZero && TOpCall.ExecutionType != ExecutionType.CALLCODE)
            return new(programCounter, EvmExceptionType.StaticCallViolation);

        // Determine caller and target based on the call type.
        Address caller = TOpCall.ExecutionType == ExecutionType.DELEGATECALL ? env.Caller : env.ExecutingAccount;
        Address target = (TOpCall.ExecutionType == ExecutionType.CALL || TOpCall.IsStatic)
            ? codeSource
            : env.ExecutingAccount;

        // Add extra gas cost if value is transferred.
        if (TOpCall.ExecutionType != ExecutionType.DELEGATECALL &&
            !TOpCall.IsStatic &&
            !isTransferZero)
        {
            if (!TGasPolicy.ConsumeCallValueTransfer(ref gas)) goto OutOfGas;
        }

        IWorldState state = vm.WorldState;
        // Charge additional gas if the target account is new or considered empty.
        if (!EIP158.IsActive && !state.AccountExists(target))
        {
            if (!TGasPolicy.ConsumeNewAccountCreation(ref gas)) goto OutOfGas;
        }
        else if (EIP158.IsActive &&
            TOpCall.ExecutionType != ExecutionType.DELEGATECALL &&
            !TOpCall.IsStatic &&
            !isTransferZero && state.IsDeadAccount(target))
        {
            if (!TGasPolicy.ConsumeNewAccountCreation(ref gas)) goto OutOfGas;
        }

        // Update gas: call cost and memory expansion for input and output.
        if (!TGasPolicy.UpdateGas(ref gas, spec.GetCallCost()) ||
            !TGasPolicy.UpdateMemoryCost(ref gas, in dataOffset, dataLength, vm.VmState) ||
            (!outputLength.IsZero && !TGasPolicy.UpdateMemoryCost(ref gas, in outputOffset, outputLength, vm.VmState)))
            goto OutOfGas;

        // If contract is large, charge for access
        if (EIP7907.IsActive)
        {
            uint excessContractSize = (uint)Math.Max(0, codeInfo.CodeSpan.Length - CodeSizeConstants.MaxCodeSizeEip170);
            if (excessContractSize > 0 && !ChargeForLargeContractAccess(excessContractSize, codeSource, in vm.VmState.AccessTracker, ref gas))
                goto OutOfGas;
        }

        long gasLimitUl;
        // Apply the 63/64 gas rule if enabled.
        if (EIP150.IsActive)
        {
            // Get remaining gas for 63/64 calculation
            long gasAvailable = TGasPolicy.GetRemainingGas(in gas);
            gasAvailable -= (long)((ulong)gasAvailable >> 6);
            if (!gasLimit.IsUint64)
            {
                gasLimitUl = gasAvailable;
            }
            else
            {
                gasLimitUl = (long)Math.Min((ulong)gasAvailable, gasLimit.u0);
            }
        }
        else
        {
            // If gasLimit exceeds the host's representable range, treat as out-of-gas.
            if (gasLimit >= long.MaxValue) goto OutOfGas;
            gasLimitUl = (long)gasLimit;
        }

        if (!TGasPolicy.UpdateGas(ref gas, gasLimitUl)) goto OutOfGas;

        // Add call stipend if value is being transferred.
        bool tracingRefunds = vm.TxTracer.IsTracingRefunds;
        if (TOpCall.ExecutionType != ExecutionType.DELEGATECALL &&
            !TOpCall.IsStatic &&
            !isTransferZero)
        {
            if (tracingRefunds)
            {
                TraceValueTransfer(vm);
            }
            gasLimitUl += GasCostOf.CallStipend;
        }

        // Check call depth and balance of the caller.
        if (env.CallDepth >= MaxCallDepth ||
            (TOpCall.ExecutionType != ExecutionType.DELEGATECALL &&
            !TOpCall.IsStatic && !isTransferZero &&
            state.GetBalance(env.ExecutingAccount) < transferValue))
        {
            // If the call cannot proceed, return an empty response and push zero on the stack.
            vm.ReturnDataBuffer = Array.Empty<byte>();

            // Optionally report memory changes for refund tracing.
            if (tracingRefunds)
            {
                TraceMemoryChange(vm, dataOffset);
            }

            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportOperationRemainingGas(TGasPolicy.GetRemainingGas(in gas));
                vm.TxTracer.ReportOperationError(EvmExceptionType.NotEnoughBalance);
            }

            // Refund the remaining gas to the caller.
            TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
            if (TTracingInst.IsActive)
            {
                vm.TxTracer.ReportGasUpdateForVmTrace(gasLimitUl, TGasPolicy.GetRemainingGas(in gas));
            }
            return new(programCounter, stack.PushZero<TTracingInst>());
        }

        // Take a snapshot of the state for potential rollback.
        Snapshot snapshot = state.TakeSnapshot();

        if (TOpCall.ExecutionType != ExecutionType.DELEGATECALL &&
            !TOpCall.IsStatic &&
            !isTransferZero)
        {
            // Subtract the transfer value from the caller's balance.
            state.SubtractFromBalance(caller, in transferValue, spec);
        }

        // Fast-path for calls to externally owned accounts (non-contracts)
        if (!TTracingInst.IsActive && codeInfo.IsEmpty && !vm.TxTracer.IsTracingActions)
        {
            vm.ReturnDataBuffer = default;
            EvmExceptionType result = stack.PushOne<TTracingInst>();
            if (result != EvmExceptionType.None)
            {
                return new(programCounter, result);
            }
            // Refund the remaining gas to the caller.
            TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
            return new(programCounter, FastCall(vm, spec, in transferValue, isTransferZero, target));
        }

        // Load call data from memory.
        if (!vm.VmState.Memory.TryLoad(in dataOffset, dataLength, out ReadOnlyMemory<byte> callData))
            goto OutOfGas;

        bool overflowed = false;
        // Normalize output offset if output length is zero.
        if (outputLength.IsZero)
        {
            // Output offset is inconsequential when output length is 0.
            outputOffset = default;
        }
        else if (!outputLength.IsUint64 || outputLength.u0 > long.MaxValue)
        {
            overflowed = true;
        }
        else if (!outputOffset.IsUint64 || outputOffset.u0 > long.MaxValue)
        {
            overflowed = true;
        }

        if (overflowed) goto OutOfGas;

        // Construct the execution environment for the call.
        ExecutionEnvironment callEnv = ExecutionEnvironment.Rent(
            codeInfo: codeInfo,
            executingAccount: target,
            caller: caller,
            codeSource: codeSource,
            callDepth: env.CallDepth + 1,
            transferValue: in transferValue,
            value: in callValue,
            inputData: in callData);

        // Rent a new call frame for executing the call.
        vm.ReturnData = VmState<TGasPolicy>.RentFrame(
            gas: TGasPolicy.FromLong(gasLimitUl),
            outputDestination: (long)outputOffset.u0,
            outputLength: (long)outputLength.u0,
            executionType: TOpCall.ExecutionType,
            isStatic: TOpCall.IsStatic || vm.VmState.IsStatic,
            isCreateOnPreExistingAccount: false,
            env: callEnv,
            stateForAccessLists: in vm.VmState.AccessTracker,
            snapshot: in snapshot);

        return new(programCounter, EvmExceptionType.Return);

        // Fast-call path for non-contract calls:
        // Directly credit the target account and avoid constructing a full call frame.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static EvmExceptionType FastCall(VirtualMachine<TGasPolicy> vm, IReleaseSpec spec, in UInt256 transferValue, bool isTransferZero, Address target)
        {
            IWorldState state = vm.WorldState;
            if (!isTransferZero)
                state.AddToBalanceAndCreateIfNotExists(target, transferValue, spec);
            Metrics.IncrementEmptyCalls();

            vm.ReturnData = null;
            return EvmExceptionType.None;
        }

    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void TraceValueTransfer(VirtualMachine<TGasPolicy> vm)
        {
            vm.TxTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static void TraceMemoryChange(VirtualMachine<TGasPolicy> vm, UInt256 dataOffset)
        {
            // Specific to Parity tracing: inspect 32 bytes from data offset.
            ReadOnlyMemory<byte>? memoryTrace = vm.VmState.Memory.Inspect(in dataOffset, 32);
            vm.TxTracer.ReportMemoryChange(dataOffset, memoryTrace is null ? default : memoryTrace.Value.Span);
        }
    }

    private static bool ChargeForLargeContractAccess<TGasPolicy>(uint excessContractSize, Address codeAddress, in StackAccessTracker accessTracer, ref TGasPolicy gas)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        if (accessTracer.WarmUpLargeContract(codeAddress))
        {
            long largeContractCost = GasCostOf.InitCodeWord * EvmCalculations.Div32Ceiling(excessContractSize, out bool outOfGas);
            if (outOfGas || !TGasPolicy.UpdateGas(ref gas, largeContractCost)) return false;
        }

        return true;
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
    public static OpcodeResult InstructionReturn<TGasPolicy>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        // RETURN is not allowed during contract creation.
        if (vm.VmState.ExecutionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE)
        {
            goto BadInstruction;
        }

        // Pop memory position and length for the return data.
        if (!stack.PopUInt256(out UInt256 position, out UInt256 length))
            goto StackUnderflow;

        // Update the memory cost for the region being returned.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in position, in length, vm.VmState) ||
            !vm.VmState.Memory.TryLoad(in position, in length, out ReadOnlyMemory<byte> returnData))
        {
            goto OutOfGas;
        }

        vm.ReturnData = returnData.ToArray();

        return new(programCounter, EvmExceptionType.Return);
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return new(programCounter, EvmExceptionType.OutOfGas);
    StackUnderflow:
        return new(programCounter, EvmExceptionType.StackUnderflow);
    BadInstruction:
        return new(programCounter, EvmExceptionType.BadInstruction);
    }
}
