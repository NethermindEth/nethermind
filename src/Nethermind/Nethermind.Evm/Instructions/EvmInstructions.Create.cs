// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;
using Nethermind.Evm.State;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

/// <summary>
/// Contains implementations for EVM instructions including contract creation (CREATE and CREATE2).
/// </summary>
internal static partial class EvmInstructions
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
    /// <param name="vm">The current virtual machine instance.</param>
    /// <param name="stack">Reference to the EVM stack.</param>
    /// <param name="gas">Reference to the gas state.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns>An <see cref="EvmExceptionType"/> indicating success or the type of exception encountered.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionCreate<TGasPolicy, TOpCreate, TTracingInst>(
        VirtualMachine<TGasPolicy> vm,
        ref EvmStack stack,
        ref TGasPolicy gas,
        ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpCreate : struct, IOpCreate
        where TTracingInst : struct, IFlag
    {
        // Increment metrics counter for contract creation operations.
        Metrics.IncrementCreates();

        // Obtain the current EVM specification and check if the call is static (static calls cannot create contracts).
        IReleaseSpec spec = vm.Spec;
        if (vm.VmState.IsStatic)
            goto StaticCallViolation;

        // Reset the return data buffer as contract creation does not use previous return data.
        vm.ReturnData = null;
        ExecutionEnvironment env = vm.VmState.Env;
        IWorldState state = vm.WorldState;

        // Pop parameters off the stack: value to transfer, memory position for the initialization code,
        // and the length of the initialization code.
        if (!stack.PopUInt256(out UInt256 value) ||
            !stack.PopUInt256(out UInt256 memoryPositionOfInitCode) ||
            !stack.PopUInt256(out UInt256 initCodeLength))
            goto StackUnderflow;

        Span<byte> salt = default;
        // For CREATE2, an extra salt value is required. Use type check to differentiate.
        if (typeof(TOpCreate) == typeof(OpCreate2))
        {
            salt = stack.PopWord256();
        }

        // EIP-3860: Limit the maximum size of the initialization code.
        if (spec.IsEip3860Enabled)
        {
            if (initCodeLength > spec.MaxInitCodeSize)
                goto OutOfGas;
        }

        bool outOfGas = false;
        // Calculate the gas cost for the creation, including fixed cost and per-word cost for init code.
        // Also include an extra cost for CREATE2 if applicable.
        long gasCost = GasCostOf.Create +
                       (spec.IsEip3860Enabled ? GasCostOf.InitCodeWord * EvmCalculations.Div32Ceiling(in initCodeLength, out outOfGas) : 0) +
                       (typeof(TOpCreate) == typeof(OpCreate2)
                           ? GasCostOf.Sha3Word * EvmCalculations.Div32Ceiling(in initCodeLength, out outOfGas)
                           : 0);

        // Check gas sufficiency: if outOfGas flag was set during gas division or if gas update fails.
        if (outOfGas || !TGasPolicy.UpdateGas(ref gas, gasCost))
            goto OutOfGas;

        // Update memory gas cost based on the required memory expansion for the init code.
        if (!TGasPolicy.UpdateMemoryCost(ref gas, in memoryPositionOfInitCode, in initCodeLength, vm.VmState))
            goto OutOfGas;

        // Verify call depth does not exceed the maximum allowed. If exceeded, return early with empty data.
        // This guard ensures we do not create nested contract calls beyond EVM limits.
        if (env.CallDepth >= MaxCallDepth)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<TTracingInst>();
            goto None;
        }

        // Load the initialization code from memory based on the specified position and length.
        if (!vm.VmState.Memory.TryLoad(in memoryPositionOfInitCode, in initCodeLength, out ReadOnlyMemory<byte> initCode))
            goto OutOfGas;

        // Check that the executing account has sufficient balance to transfer the specified value.
        UInt256 balance = state.GetBalance(env.ExecutingAccount, vm.TxExecutionContext.BlockAccessIndex);
        if (value > balance)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<TTracingInst>();
            goto None;
        }

        // Retrieve the nonce of the executing account to ensure it hasn't reached the maximum.
        UInt256 accountNonce = state.GetNonce(env.ExecutingAccount, vm.TxExecutionContext.BlockAccessIndex);
        UInt256 maxNonce = ulong.MaxValue;
        if (accountNonce >= maxNonce)
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<TTracingInst>();
            goto None;
        }

        // Get remaining gas for the create operation.
        long gasAvailable = TGasPolicy.GetRemainingGas(in gas);

        // End tracing if enabled, prior to switching to the new call frame.
        if (TTracingInst.IsActive)
            vm.EndInstructionTrace(gasAvailable);

        // Calculate gas available for the contract creation call.
        // Use the 63/64 gas rule if specified in the current EVM specification.
        long callGas = spec.Use63Over64Rule ? gasAvailable - gasAvailable / 64L : gasAvailable;
        if (!TGasPolicy.UpdateGas(ref gas, callGas))
            goto OutOfGas;

        // Compute the contract address:
        // - For CREATE: based on the executing account and its current nonce.
        // - For CREATE2: based on the executing account, the provided salt, and the init code.
        Address contractAddress = typeof(TOpCreate) == typeof(OpCreate)
            ? ContractAddress.From(env.ExecutingAccount, state.GetNonce(env.ExecutingAccount, vm.TxExecutionContext.BlockAccessIndex))
            : ContractAddress.From(env.ExecutingAccount, salt, initCode.Span);

        // For EIP-2929 support, pre-warm the contract address in the access tracker to account for hot/cold storage costs.
        if (spec.UseHotAndColdStorage)
        {
            vm.VmState.AccessTracker.WarmUp(contractAddress);
        }

        // Special case: if EOF code format is enabled and the init code starts with the EOF marker,
        // the creation is not executed. This ensures that a special marker is not mistakenly executed as code.
        if (spec.IsEofEnabled && initCode.Span.StartsWith(EofValidator.MAGIC))
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<TTracingInst>();
            TGasPolicy.UpdateGasUp(ref gas, callGas);
            goto None;
        }

        // Increment the nonce of the executing account to reflect the contract creation.
        state.IncrementNonce(env.ExecutingAccount, vm.TxExecutionContext.BlockAccessIndex);

        // Analyze and compile the initialization code.
        CodeInfoFactory.CreateInitCodeInfo(initCode, spec, out ICodeInfo? codeInfo, out _);

        // Take a snapshot of the current state. This allows the state to be reverted if contract creation fails.
        Snapshot snapshot = state.TakeSnapshot(blockAccessIndex: vm.TxExecutionContext.BlockAccessIndex);

        // Check for contract address collision. If the contract already exists and contains code or non-zero state,
        // then the creation should be aborted.
        bool accountExists = state.AccountExists(contractAddress, vm.TxExecutionContext.BlockAccessIndex);
        if (accountExists && contractAddress.IsNonZeroAccount(spec, vm.CodeInfoRepository, state, vm.TxExecutionContext.BlockAccessIndex))
        {
            vm.ReturnDataBuffer = Array.Empty<byte>();
            stack.PushZero<TTracingInst>();
            goto None;
        }

        // If the contract address refers to a dead account, clear its storage before creation.
        if (state.IsDeadAccount(contractAddress, vm.TxExecutionContext.BlockAccessIndex))
        {
            // Note: Seems to be needed on block 21827914 for some reason
            state.ClearStorage(contractAddress, vm.TxExecutionContext.BlockAccessIndex);
        }

        // Deduct the transfer value from the executing account's balance.
        state.SubtractFromBalance(env.ExecutingAccount, value, spec, vm.TxExecutionContext.BlockAccessIndex);

        // Construct a new execution environment for the contract creation call.
        // This environment sets up the call frame for executing the contract's initialization code.
        ExecutionEnvironment callEnv = ExecutionEnvironment.Rent(
            codeInfo: codeInfo,
            executingAccount: contractAddress,
            caller: env.ExecutingAccount,
            codeSource: null,
            callDepth: env.CallDepth + 1,
            transferValue: in value,
            value: in value,
            inputData: in _emptyMemory);

        // Rent a new frame to run the initialization code in the new execution environment.
        vm.ReturnData = VmState<TGasPolicy>.RentFrame(
            gas: TGasPolicy.FromLong(callGas),
            outputDestination: 0,
            outputLength: 0,
            executionType: TOpCreate.ExecutionType,
            isStatic: vm.VmState.IsStatic,
            isCreateOnPreExistingAccount: accountExists,
            env: callEnv,
            stateForAccessLists: in vm.VmState.AccessTracker,
            snapshot: in snapshot);
    None:
        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    }
}
