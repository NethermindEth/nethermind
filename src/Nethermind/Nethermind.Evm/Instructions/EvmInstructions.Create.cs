// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using Nethermind.Evm.State;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

/// <summary>
/// Contains implementations for EVM instructions including contract creation (CREATE and CREATE2).
/// </summary>
public static partial class EvmInstructions
{
    private static readonly ReadOnlyMemory<byte> _emptyMemory = default;
    /// <summary>
    /// Interface for CREATE opcode types.
    /// Implementations must specify the <see cref="ExecutionType"/> to distinguish between CREATE and CREATE2.
    /// </summary>
    public interface IOpCreate
    {
        /// <summary>
        /// Gets the execution type corresponding to the create operation.
        /// </summary>
        abstract static ExecutionType ExecutionType { get; }
    }

    /// <summary>
    /// Implements the basic contract creation opcode.
    /// </summary>
    public struct OpCreate : IOpCreate
    {
        /// <summary>
        /// Gets the execution type for the CREATE opcode.
        /// </summary>
        public static ExecutionType ExecutionType => ExecutionType.CREATE;
    }

    /// <summary>
    /// Implements the CREATE2 opcode, which allows for deterministic contract address generation.
    /// </summary>
    public struct OpCreate2 : IOpCreate
    {
        /// <summary>
        /// Gets the execution type for the CREATE2 opcode.
        /// </summary>
        public static ExecutionType ExecutionType => ExecutionType.CREATE2;
    }

    /// <summary>
    /// Implements the CREATE/CREATE2 opcode, handling new contract deployment.
    /// This method performs validation, gas and memory cost calculations, state updates,
    /// and delegates execution to a new call frame for the contract's initialization code.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy implementation.</typeparam>
    /// <typeparam name="TOpCreate">The type of create operation (either <see cref="OpCreate"/> or <see cref="OpCreate2"/>).</typeparam>
    /// <typeparam name="TTracingInst">Tracing instructions type used for instrumentation if active.</typeparam>
    /// <typeparam name="TSpec">The fork rules the opcode table specialized this handler on.</typeparam>
    /// <param name="vm">The current virtual machine instance.</param>
    /// <param name="stack">Reference to the EVM stack.</param>
    /// <param name="gas">Reference to the gas state.</param>
    /// <returns>An <see cref="EvmExceptionType"/> indicating success or the type of exception encountered.</returns>
    [SkipLocalsInit]
    internal static EvmExceptionType InstructionCreate<TGasPolicy, TOpCreate, TTracingInst, TEip8037, TSpec>(
        ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCreate : struct, IOpCreate
        where TTracingInst : struct, IFlag
        where TEip8037 : struct, IFlag
        where TSpec : struct, ICreateSpec
    {
        vm.MetricsCounters.IncrementCreates();

        if (TTracingInst.IsActive)
            TraceCreateGas<TGasPolicy, TOpCreate, TEip8037, TSpec>(stack, in gas, vm);

        // Obtain the current EVM specification and check if the call is static (static calls cannot create contracts).
        IReleaseSpec spec = vm.Spec;
        if (vm.VmState.IsStatic)
        {
            goto StaticCallViolation;
        }

        Debug.Assert(vm.ReturnData is null, "Dispatch clears staged output before entering an opcode chain.");
        ExecutionEnvironment env = vm.VmState.Env;
        IWorldState state = vm.WorldState;

        // Pop parameters off the stack: value to transfer, memory position for the initialization code,
        // and the length of the initialization code.
        if (!stack.PopUInt256(out UInt256 value, out UInt256 memoryPositionOfInitCode, out UInt256 initCodeLength))
            goto StackUnderflow;

        Span<byte> salt = default;
        // For CREATE2, an extra salt value is required. Use type check to differentiate.
        if (typeof(TOpCreate) == typeof(OpCreate2))
        {
            if (!stack.PopWord256(out salt))
                goto StackUnderflow;
        }

        // EIP-3860: Limit the maximum size of the initialization code.
        bool isEip3860 = TSpec.IsEip3860Enabled;
        if (isEip3860)
        {
            if (initCodeLength > spec.MaxInitCodeSize)
            {
                goto OutOfGas;
            }
        }

        ulong initCodeWords = EvmCalculations.Div32Ceiling(in initCodeLength, out bool outOfGas);
        if (outOfGas)
            goto OutOfGas;

        if (!TSpec.TryConsumeCreateGas<TGasPolicy, TEip8037, TOpCreate>(ref gas, spec, initCodeWords))
            goto OutOfGas;

        // Update memory gas cost based on the required memory expansion for the init code.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in memoryPositionOfInitCode, in initCodeLength, ref vm.VmState.Memory))
            goto OutOfGas;

        // Verify call depth does not exceed the maximum allowed. If exceeded, return early with empty data.
        // This guard ensures we do not create nested contract calls beyond EVM limits.
        if (env.CallDepth >= MaxCallDepth)
        {
            if (!TEip8037.IsActive && vm.IsTracingActions)
                TraceRejectedCreate<TGasPolicy, TOpCreate, TSpec>(vm, gas, in value, in memoryPositionOfInitCode, in initCodeLength, EvmExceptionType.CallDepthExceeded);

            vm.ReturnDataBuffer = default;
            return stack.PushZero<TTracingInst, OnFlag>();
        }

        // Load the initialization code from memory based on the specified position and length.
        if (!vm.VmState.Memory.TryLoadOwned(in memoryPositionOfInitCode, in initCodeLength, out ReadOnlyMemory<byte> initCode))
            goto OutOfGas;

        // Check that the executing account has sufficient balance to transfer the specified value.
        UInt256 balance = state.GetBalance(env.ExecutingAccount);
        if (value > balance)
        {
            if (!TEip8037.IsActive && vm.IsTracingActions)
                TraceRejectedCreate<TGasPolicy, TOpCreate, TSpec>(vm, gas, in value, in memoryPositionOfInitCode, in initCodeLength, EvmExceptionType.NotEnoughBalance);

            vm.ReturnDataBuffer = default;
            return stack.PushZero<TTracingInst, OnFlag>();
        }

        // Retrieve the nonce of the executing account to ensure it hasn't reached the maximum.
        ulong accountNonce = state.GetNonce(env.ExecutingAccount);
        if (accountNonce >= ulong.MaxValue)
        {
            vm.ReturnDataBuffer = default;
            return stack.PushZero<TTracingInst, OnFlag>();
        }

        // Compute the contract address:
        // - For CREATE: based on the executing account and its current nonce.
        // - For CREATE2: based on the executing account, the provided salt, and the init code.
        Address contractAddress = typeof(TOpCreate) == typeof(OpCreate)
            ? ContractAddress.From(env.ExecutingAccount, accountNonce)
            : ContractAddress.From(env.ExecutingAccount, salt, initCode.Span);

        // For EIP-2929 support, pre-warm the contract address in the access tracker to account for hot/cold storage costs.
        if (TSpec.UseHotAndColdStorage)
        {
            vm.VmState.AccessTracker.WarmUp(contractAddress);
        }

        bool isNonZeroAccount = state.IsNonZeroAccount(contractAddress, out bool accountExists);
        bool isAliveAccount = !state.IsDeadAccount(contractAddress);
        bool chargeCreateStateGas = TEip8037.IsActive && !isAliveAccount;

        if (chargeCreateStateGas && !TGasPolicy.TryConsumeCreateStateGas(ref gas))
            goto OutOfGas;

        if (TTracingInst.IsActive)
            vm.EndInstructionTrace(TGasPolicy.GetRemainingGas(in gas));

        // EIP-150: forward all remaining gas (capped at 63/64) to the creation frame.
        if (!TSpec.TryReserveChildGas<TGasPolicy>(ref gas, spec, out ulong callGas))
            goto OutOfGas;

        // Increment the nonce of the executing account to reflect the contract creation.
        state.IncrementNonce(env.ExecutingAccount);

        // Take a snapshot of the current state. This allows the state to be reverted if contract creation fails.
        Snapshot snapshot = state.TakeSnapshot();

        // EIP-684: if the account already exists with code or a non-zero nonce, the creation fails.
        // Collision behaves as an immediate exceptional halt - burned callGas counts as block_execution.
        if (isNonZeroAccount)
        {
            if (chargeCreateStateGas)
            {
                vm.CreditStateGasRefund<TEip8037>(ref gas, TGasPolicy.GetCreateStateCost());
            }

            if (vm.IsTracingActions)
                vm.TxTracer.ReportRejectedAction(callGas, 0, value, env.ExecutingAccount, contractAddress, initCode, TOpCreate.ExecutionType, EvmExceptionType.TransactionCollision);

            vm.ReturnDataBuffer = default;
            EvmExceptionType pushResult = stack.PushZero<TTracingInst, OnFlag>();

            // The instruction trace ended before the creation's gas was reserved and the 0 was pushed, and the
            // collision consumed that gas.
            if (TTracingInst.IsActive)
                vm.TxTracer.ReportGasUpdateForVmTrace(0, TGasPolicy.GetRemainingGas(in gas));

            return pushResult;
        }

        state.ClearStorage(contractAddress);

        // Deduct the transfer value from the executing account's balance.
        state.SubtractFromBalance(env.ExecutingAccount, value, spec);

        // Construct a new execution environment for the contract creation call.
        // This environment sets up the call frame for executing the contract's initialization code.
        ExecutionEnvironment callEnv = ExecutionEnvironment.Rent(
            codeInfo: new CodeInfo(initCode),
            executingAccount: contractAddress,
            caller: env.ExecutingAccount,
            codeSource: null,
            callDepth: env.CallDepth + 1,
            value: in value,
            inputData: in _emptyMemory);

        // Rent a new frame to run the initialization code in the new execution environment.
        vm.ReturnData = VmState<TGasPolicy>.RentFrame(
            gas: TGasPolicy.CreateChildFrameGas(ref gas, callGas),
            outputDestination: 0,
            outputLength: 0,
            executionType: TOpCreate.ExecutionType,
            isStatic: vm.VmState.IsStatic,
            isCreateOnPreExistingAccount: accountExists,
            env: callEnv,
            stateForAccessLists: in vm.VmState.AccessTracker,
            snapshot: in snapshot,
            isCreateStateGasCharged: chargeCreateStateGas,
            frameJournalCheckpoint: vm.TxExecutionContext.FrameTxContext?.FrameJournalCheckpoint ?? 0);

        return EvmExceptionType.Suspend;
        // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;

    }

    /// <summary>
    /// Reports a creation that failed its depth or balance precheck as an action that entered no frame, with the
    /// gas it would have forwarded, which returns at once. Only used before EIP-8037, which moves the precheck into
    /// the creating operation, so the creation has no frame of its own.
    /// </summary>
    /// <remarks>
    /// See the <c>CREATE</c>/<c>CREATE2</c> paragraph of
    /// <see href="https://eips.ethereum.org/EIPS/eip-8037#gas-accounting-for-new-accounts">EIP-8037, gas accounting for new accounts</see>.
    /// </remarks>
    /// <param name="gas">A copy of the caller's gas, before any is reserved for the creation.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TraceRejectedCreate<TGasPolicy, TOpCreate, TSpec>(
        VirtualMachine<TGasPolicy> vm,
        TGasPolicy gas,
        in UInt256 value,
        in UInt256 initCodePosition,
        in UInt256 initCodeLength,
        EvmExceptionType error)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCreate : struct, IOpCreate
        where TSpec : struct, ICreateSpec
    {
        TSpec.TryReserveChildGas<TGasPolicy>(ref gas, vm.Spec, out ulong callGas);
        // The creation already paid to expand memory over its init code.
        vm.VmState.Memory.TryLoad(in initCodePosition, in initCodeLength, out ReadOnlyMemory<byte> initCode);
        vm.TxTracer.ReportRejectedAction(callGas, callGas, value, vm.VmState.Env.ExecutingAccount, null, initCode, TOpCreate.ExecutionType, error);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TraceCreateGas<TGasPolicy, TOpCreate, TEip8037, TSpec>(EvmStack stack, in TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCreate : struct, IOpCreate
        where TEip8037 : struct, IFlag
        where TSpec : struct, ICreateSpec
    {
        // A stack copy preserves the pre-execution operands, including on static-call failures.
        if (!stack.PopUInt256(out _, out UInt256 position, out UInt256 length) ||
            typeof(TOpCreate) == typeof(OpCreate2) && !stack.PopUInt256(out _))
            return;

        TGasPolicy scratch = TGasPolicy.FromULong(ulong.MaxValue);
        TSpec.TryConsumeCreateGas<TGasPolicy, TEip8037, TOpCreate>(ref scratch, vm.Spec, 0);
        ulong constantCost = ulong.MaxValue - TGasPolicy.GetRemainingGas(in scratch);
        ulong initialGas = TGasPolicy.GetRemainingGas(in gas);
        string? error;
        if (initialGas < constantCost)
            error = "out of gas";
        else if (!TryGetTraceMemorySize(in position, in length, out ulong memorySize))
            error = "gas uint64 overflow";
        else if (vm.VmState.IsStatic)
            error = "out of gas: write protection";
        else if (memorySize > 0x1FFFFFFFE0UL)
            error = "out of gas: gas uint64 overflow";
        else if (TSpec.IsEip3860Enabled && length > vm.Spec.MaxInitCodeSize)
            error = $"out of gas: max initcode size exceeded: code size {length} limit {vm.Spec.MaxInitCodeSize}";
        else
        {
            ulong initCodeWords = EvmCalculations.Div32Ceiling(in length, out _);
            scratch = TGasPolicy.FromULong(ulong.MaxValue);
            TSpec.TryConsumeCreateGas<TGasPolicy, TEip8037, TOpCreate>(ref scratch, vm.Spec, initCodeWords);
            ulong baseCost = ulong.MaxValue - TGasPolicy.GetRemainingGas(in scratch);
            TraceDynamicMemoryGas(vm, initialGas, constantCost, baseCost, in position, in length);
            return;
        }

        vm.TraceOperationGasCost(constantCost);
        vm.TraceActionErrorDetails(error);
        vm.TraceOperationReady(constantCost, error);
    }
}
