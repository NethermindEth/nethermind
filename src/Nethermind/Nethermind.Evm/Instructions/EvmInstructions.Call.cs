// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Evm.State;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

/// <summary>
/// Groups memory offset/length pairs for CALL input and output to reduce parameter count.
/// </summary>
internal readonly struct CallMemoryParams(UInt256 dataOffset, UInt256 dataLength, UInt256 outputOffset, UInt256 outputLength)
{
    public readonly UInt256 DataOffset = dataOffset;
    public readonly UInt256 DataLength = dataLength;
    public readonly UInt256 OutputOffset = outputOffset;
    public readonly UInt256 OutputLength = outputLength;
}

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
    public static OpcodeResult InstructionCall<TGasPolicy, TOpCall, TTracingInst, EIP150, EIP158, EIP7907, EIP2929>(VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCall : struct, IOpCall
        where TTracingInst : struct, IFlag
        where EIP150 : struct, IFlag
        where EIP158 : struct, IFlag
        where EIP7907 : struct, IFlag
        where EIP2929 : struct, IFlag
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

        // Cache IsZero check to avoid repeated vptest instructions
        bool outputIsEmpty = outputLength.IsZero;

        IReleaseSpec spec = vm.Spec;
        // Charge gas for accessing the account's code (including delegation logic if applicable).
        bool _ = vm.TxExecutionContext.CodeInfoRepository
            .TryGetDelegation(codeSource, spec, out CodeInfo codeInfo, out Address delegated);
        // Use EIP2929 generic type parameter to eliminate call at compile-time when disabled.
        // When EIP2929.IsActive is false (pre-Berlin), no cold/warm storage gas is charged.
        if (EIP2929.IsActive && !TGasPolicy.ConsumeAccountAccessGasWithDelegation(ref gas, spec, in vm.VmState.AccessTracker,
                vm.TxTracer.IsTracingAccess, codeSource, delegated)) goto OutOfGas;

        // For non-delegate calls, the transfer value is the call value.
        // Use TOpCall.IsStatic first - it's a compile-time constant that branch-eliminates for STATICCALL.
        UInt256 transferValue;
        bool isTransferZero;
        if (TOpCall.IsStatic || TOpCall.ExecutionType == ExecutionType.DELEGATECALL || callValue.IsZero)
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
            !isTransferZero &&
            !TGasPolicy.ConsumeCallValueTransfer(ref gas))
        {
            goto OutOfGas;
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
            !isTransferZero && state.IsDeadAccount(target) &&
            !TGasPolicy.ConsumeNewAccountCreation(ref gas))
        {
            goto OutOfGas;
        }

        // Update gas: call cost and memory expansion for input and output.
        // Inline GetCallCost() using generic type parameters to avoid interface dispatch.
        // When EIP-2929 is enabled (Berlin+), call cost is 0 (included in cold/warm access).
        // When EIP-2929 is disabled but EIP-150 is enabled, cost is GasCostOf.CallEip150.
        // When both are disabled, cost is GasCostOf.Call.
        // Note: Skip the UpdateGas call entirely when cost is 0 (common case for modern chains).
        if ((!EIP2929.IsActive && !TGasPolicy.UpdateGas(ref gas, EIP150.IsActive ? GasCostOf.CallEip150 : GasCostOf.Call)) ||
            !TGasPolicy.UpdateMemoryCost(ref gas, in dataOffset, dataLength, vm.VmState) ||
            (!outputIsEmpty && !TGasPolicy.UpdateMemoryCost(ref gas, in outputOffset, outputLength, vm.VmState)))
            goto OutOfGas;


        bool useFastPrecompile = codeInfo.IsPrecompile
            && codeInfo.Precompile!.SupportsFastPath
            && !TTracingInst.IsActive
            && !vm.TxTracer.IsTracingActions;

        CallMemoryParams mem = new(dataOffset, dataLength, outputOffset, outputLength);

        // Dispatch to ExecuteCallCore with fast-precompile flag for JIT branch elimination.
        return useFastPrecompile
            ? new(programCounter, ExecuteCallCore<OnFlag>(vm, state, ref gas, ref stack, spec, env, codeInfo, codeInfo.Precompile,
                in gasLimit, in transferValue, in callValue, caller, target, codeSource, in mem))
            : new(programCounter, ExecuteCallCore<OffFlag>(vm, state, ref gas, ref stack, spec, env, codeInfo, null,
                in gasLimit, in transferValue, in callValue, caller, target, codeSource, in mem));

        static EvmExceptionType ExecuteCallCore<TFastPrecompile>(
            VirtualMachine<TGasPolicy> vm,
            IWorldState state,
            ref TGasPolicy gas,
            ref EvmStack stack,
            IReleaseSpec spec,
            in ExecutionEnvironment env,
            CodeInfo codeInfo,
            IPrecompile? precompile,
            in UInt256 gasLimit,
            in UInt256 transferValue,
            in UInt256 callValue,
            Address caller,
            Address target,
            Address codeSource,
            in CallMemoryParams mem)
            where TFastPrecompile : struct, IFlag
        {
            // If contract is large, charge for access.
            if (!TFastPrecompile.IsActive && spec.IsEip7907Enabled)
            {
                uint excessContractSize = (uint)Math.Max(0, codeInfo.CodeSpan.Length - CodeSizeConstants.MaxCodeSizeEip170);
                if (excessContractSize > 0 && !ChargeForLargeContractAccess(excessContractSize, codeSource, in vm.VmState.AccessTracker, ref gas))
                    return EvmExceptionType.OutOfGas;
            }

            // Get remaining gas for 63/64 calculation.
            long gasAvailable = TGasPolicy.GetRemainingGas(in gas);
            UInt256 effectiveGasLimit = gasLimit;

            // Apply the 63/64 gas rule if enabled.
            if (spec.Use63Over64Rule)
            {
                effectiveGasLimit = UInt256.Min((UInt256)(gasAvailable - gasAvailable / 64), effectiveGasLimit);
            }

            // If gas limit exceeds the host's representable range, treat as out-of-gas.
            if (effectiveGasLimit >= long.MaxValue) return EvmExceptionType.OutOfGas;

            long gasLimitUl = (long)effectiveGasLimit;
            if (!TGasPolicy.UpdateGas(ref gas, gasLimitUl)) return EvmExceptionType.OutOfGas;

            // Add call stipend if value is being transferred.
            if (!transferValue.IsZero)
            {
                if (vm.TxTracer.IsTracingRefunds)
                    vm.TxTracer.ReportExtraGasPressure(GasCostOf.CallStipend);
                gasLimitUl += GasCostOf.CallStipend;
            }

            // Check call depth and balance of the caller.
            if (env.CallDepth >= MaxCallDepth ||
                (!transferValue.IsZero && state.GetBalance(env.ExecutingAccount) < transferValue))
            {
                // If the call cannot proceed, return an empty response and push zero on the stack.
                vm.ReturnDataBuffer = Array.Empty<byte>();
                stack.PushZero<TTracingInst>();

                // Optionally report memory changes for refund tracing.
                if (vm.TxTracer.IsTracingRefunds)
                {
                    // Specific to Parity tracing: inspect 32 bytes from data offset.
                    ReadOnlyMemory<byte>? memoryTrace = vm.VmState.Memory.Inspect(in mem.DataOffset, 32);
                    vm.TxTracer.ReportMemoryChange(mem.DataOffset, memoryTrace is null ? default : memoryTrace.Value.Span);
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
                return EvmExceptionType.None;
            }

            if (TFastPrecompile.IsActive)
            {
                return FastPrecompileCall(vm, state, ref gas, ref stack, gasLimitUl,
                    in transferValue, caller, target, precompile!,
                    in mem, spec);
            }

            // Take a snapshot of the state for potential rollback.
            Snapshot snapshot = state.TakeSnapshot();
            // Subtract the transfer value from the caller's balance.
            state.SubtractFromBalance(caller, in transferValue, spec);

            // Fast-path for calls to externally owned accounts (non-contracts).
            if (!TTracingInst.IsActive && codeInfo.IsEmpty && !vm.TxTracer.IsTracingActions)
            {
                vm.ReturnDataBuffer = default;
                EvmExceptionType pushResult = stack.PushOne<TTracingInst>();
                if (pushResult != EvmExceptionType.None) return pushResult;
                TGasPolicy.UpdateGasUp(ref gas, gasLimitUl);
                return FastCall(vm, spec, in transferValue, transferValue.IsZero, target);
            }

            // Load call data from memory.
            if (!vm.VmState.Memory.TryLoad(in mem.DataOffset, in mem.DataLength, out ReadOnlyMemory<byte> callData))
                return EvmExceptionType.OutOfGas;

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

            // Output offset is inconsequential when output length is 0.
            UInt256 outputDestination = mem.OutputLength.IsZero ? UInt256.Zero : mem.OutputOffset;

            // Rent a new call frame for executing the call.
            vm.ReturnData = VmState<TGasPolicy>.Rent(
                gas: TGasPolicy.FromLong(gasLimitUl),
                outputDestination: (long)outputDestination.u0,
                outputLength: (long)mem.OutputLength.u0,
                executionType: TOpCall.ExecutionType,
                isStatic: TOpCall.IsStatic || vm.VmState.IsStatic,
                isCreateOnPreExistingAccount: false,
                env: callEnv,
                stateForAccessLists: in vm.VmState.AccessTracker,
                snapshot: in snapshot);

            return EvmExceptionType.Return;
        }

        // Fast-call path for non-contract calls:
        // Directly credit the target account and avoid constructing a full call frame.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static EvmExceptionType FastCall(VirtualMachine<TGasPolicy> vm, IReleaseSpec spec, in UInt256 transferValue, bool isTransferZero, Address target)
        {
            IWorldState state = vm.WorldState;
            state.AddToBalanceAndCreateIfNotExists(target, transferValue, spec);
            Metrics.IncrementEmptyCalls();

            vm.ReturnData = null;
            return EvmExceptionType.None;
        }

        // Precompile inline execution: avoids frame allocation and state snapshot.
        [MethodImpl(MethodImplOptions.NoInlining)]
        static EvmExceptionType FastPrecompileCall(
            VirtualMachine<TGasPolicy> vm,
            IWorldState state,
            ref TGasPolicy gas,
            ref EvmStack stack,
            long gasLimitUl,
            in UInt256 transferValue,
            Address caller,
            Address target,
            IPrecompile precompile,
            in CallMemoryParams mem,
            IReleaseSpec spec)
        {
            // Load input from caller's memory (not a state change — safe before snapshot decision).
            if (!vm.VmState.Memory.TryLoad(in mem.DataOffset, in mem.DataLength, out ReadOnlyMemory<byte> inputData))
                return EvmExceptionType.OutOfGas;

            // Calculate precompile gas cost; clamp to MaxValue on overflow so the OOG check below is clean.
            long baseGasCost = precompile.BaseGasCost(spec);
            long dataGasCost = precompile.DataGasCost(inputData, spec);
            bool gasOverflow = (ulong)baseGasCost + (ulong)dataGasCost > (ulong)long.MaxValue;
            long precompileGasCost = gasOverflow ? long.MaxValue : baseGasCost + dataGasCost;

            Snapshot snapshot = default;
            bool hasSnapshot = false;
            if (!transferValue.IsZero)
            {
                hasSnapshot = true;
                snapshot = state.TakeSnapshot();
                state.SubtractFromBalance(caller, in transferValue, spec);
                state.AddToBalanceAndCreateIfNotExists(target, in transferValue, spec);
            }

            // On OOG all forwarded gas is consumed (no refund), matching normal CALL semantics.
            if (gasOverflow || precompileGasCost > gasLimitUl)
            {
                return ReturnFailedPrecompileCallAndRestore(vm, ref stack, state, hasSnapshot, in snapshot);
            }

            Result<byte[]> output;
            if (!TryRunPrecompile(vm, precompile, inputData, spec, out output) || !output)
            {
                return ReturnFailedPrecompileCallAndRestore(vm, ref stack, state, hasSnapshot, in snapshot);
            }

            EvmExceptionType precompileResult = HandlePrecompileSuccess(vm, ref gas, ref stack, output.Data,
                in mem.OutputOffset, in mem.OutputLength, gasLimitUl, precompileGasCost);

            // Mirror the non-fast path touch behavior for zero-value precompile calls
            // (see RunPrecompile in VirtualMachine.cs — keep in sync).
            if (precompileResult == EvmExceptionType.None && transferValue.IsZero)
            {
                state.AddToBalanceAndCreateIfNotExists(target, in transferValue, spec);
            }

            return precompileResult;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static bool TryRunPrecompile(
            VirtualMachine<TGasPolicy> vm,
            IPrecompile precompile,
            ReadOnlyMemory<byte> inputData,
            IReleaseSpec spec,
            out Result<byte[]> output)
        {
            try
            {
                output = precompile.Run(inputData, spec);
                return true;
            }
            catch (Exception exception) when (exception is DllNotFoundException or { InnerException: DllNotFoundException })
            {
                if (vm.Logger.IsError)
                {
                    vm.Logger.Error(
                        $"Failed to load one of the dependencies of {precompile.GetType()} precompile",
                        exception as DllNotFoundException ?? exception.InnerException as DllNotFoundException);
                }
                Environment.Exit(ExitCodes.MissingPrecompile);
                throw; // Unreachable
            }
            catch (Exception exception)
            {
                if (vm.Logger.IsError)
                {
                    vm.Logger.Error($"Precompiled contract ({precompile.GetType()}) execution exception", exception);
                }

                output = default;
                return false;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static EvmExceptionType ReturnFailedPrecompileCall(VirtualMachine<TGasPolicy> vm, ref EvmStack stack)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushBytes<TTracingInst>(StatusCode.FailureBytes.Span);
            vm.ReturnData = null;
            return EvmExceptionType.None;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static EvmExceptionType ReturnFailedPrecompileCallAndRestore(
            VirtualMachine<TGasPolicy> vm,
            ref EvmStack stack,
            IWorldState state,
            bool hasSnapshot,
            in Snapshot snapshot)
        {
            if (hasSnapshot)
            {
                state.Restore(snapshot);
            }

            return ReturnFailedPrecompileCall(vm, ref stack);
        }

        static EvmExceptionType HandlePrecompileSuccess(
            VirtualMachine<TGasPolicy> vm,
            ref TGasPolicy gas,
            ref EvmStack stack,
            byte[]? outputData,
            in UInt256 outputOffset,
            in UInt256 outputLength,
            long gasLimitUl,
            long precompileGasCost)
        {
            byte[] returnBytes = outputData ?? Array.Empty<byte>();
            vm.ReturnDataBuffer = returnBytes;

            if (!outputLength.IsZero)
            {
                // outputLength fits in int: UpdateMemoryCost would have OOG'd otherwise.
                int bytesToCopy = Math.Min(returnBytes.Length, (int)outputLength);
                if (bytesToCopy > 0 &&
                    !vm.VmState.Memory.TrySave(in outputOffset, returnBytes.AsSpan(0, bytesToCopy)))
                    return EvmExceptionType.OutOfGas;
            }

            stack.PushBytes<TTracingInst>(StatusCode.SuccessBytes.Span);
            TGasPolicy.UpdateGasUp(ref gas, gasLimitUl - precompileGasCost);
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
