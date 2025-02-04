// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.Precompiles;
using Nethermind.State;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
{
    /// <summary>
    /// Defines an environment introspection operation that returns a byte span.
    /// Implementations should provide a static gas cost and a static Operation method.
    /// </summary>
    public interface IOpEnvBytes
    {
        /// <summary>
        /// The gas cost for the operation.
        /// </summary>
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as a byte span.
        /// </summary>
        /// <param name="vmState">The current virtual machine state.</param>
        /// <param name="result">The resulting bytes.</param>
        abstract static void Operation(EvmState vmState, out Span<byte> result);
    }

    /// <summary>
    /// Defines an environment introspection operation that returns a 256-bit unsigned integer.
    /// </summary>
    public interface IOpEnvUInt256
    {
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as a UInt256.
        /// </summary>
        /// <param name="vmState">The current virtual machine state.</param>
        /// <param name="result">The resulting 256-bit unsigned integer.</param>
        abstract static void Operation(EvmState vmState, out UInt256 result);
    }

    /// <summary>
    /// Defines an environment introspection operation that returns a 32-bit unsigned integer.
    /// </summary>
    public interface IOpEnvUInt32
    {
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as a UInt32.
        /// </summary>
        /// <param name="vmState">The current virtual machine state.</param>
        abstract static uint Operation(EvmState vmState);
    }

    /// <summary>
    /// Defines an environment introspection operation that returns a 64-bit unsigned integer.
    /// </summary>
    public interface IOpEnvUInt64
    {
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as a UInt64.
        /// </summary>
        /// <param name="vmState">The current virtual machine state.</param>
        abstract static ulong Operation(EvmState vmState);
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns a byte span.
    /// Generic parameter TOpEnv defines the concrete operation.
    /// </summary>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvBytes<TOpEnv>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvBytes
    {
        // Deduct the gas cost as defined by the operation implementation.
        gasAvailable -= TOpEnv.GasCost;

        // Execute the operation and retrieve the result.
        TOpEnv.Operation(vm.EvmState, out Span<byte> result);

        // Push the resulting bytes onto the EVM stack.
        stack.PushBytes(result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns a UInt256 value.
    /// </summary>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt256<TOpEnv>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvUInt256
    {
        gasAvailable -= TOpEnv.GasCost;

        TOpEnv.Operation(vm.EvmState, out UInt256 result);

        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns a UInt32 value.
    /// </summary>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt32<TOpEnv>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvUInt32
    {
        gasAvailable -= TOpEnv.GasCost;

        uint result = TOpEnv.Operation(vm.EvmState);

        stack.PushUInt32(result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns a UInt64 value.
    /// </summary>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt64<TOpEnv>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvUInt64
    {
        gasAvailable -= TOpEnv.GasCost;

        ulong result = TOpEnv.Operation(vm.EvmState);

        stack.PushUInt64(result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Returns the size of the transaction call data.
    /// </summary>
    public struct OpCallDataSize : IOpEnvUInt32
    {
        public static uint Operation(EvmState vmState)
            => (uint)vmState.Env.InputData.Length;
    }

    /// <summary>
    /// Returns the size of the executing code.
    /// </summary>
    public struct OpCodeSize : IOpEnvUInt32
    {
        public static uint Operation(EvmState vmState)
            => (uint)vmState.Env.CodeInfo.MachineCode.Length;
    }

    /// <summary>
    /// Returns the timestamp of the current block.
    /// </summary>
    public struct OpTimestamp : IOpEnvUInt64
    {
        public static ulong Operation(EvmState vmState)
            => vmState.Env.TxExecutionContext.BlockExecutionContext.Header.Timestamp;
    }

    /// <summary>
    /// Returns the block number of the current block.
    /// </summary>
    public struct OpNumber : IOpEnvUInt64
    {
        public static ulong Operation(EvmState vmState)
            => (ulong)vmState.Env.TxExecutionContext.BlockExecutionContext.Header.Number;
    }

    /// <summary>
    /// Returns the gas limit of the current block.
    /// </summary>
    public struct OpGasLimit : IOpEnvUInt64
    {
        public static ulong Operation(EvmState vmState)
            => (ulong)vmState.Env.TxExecutionContext.BlockExecutionContext.Header.GasLimit;
    }

    /// <summary>
    /// Returns the current size of the EVM memory.
    /// </summary>
    public struct OpMSize : IOpEnvUInt64
    {
        public static ulong Operation(EvmState vmState)
            => vmState.Memory.Size;
    }

    /// <summary>
    /// Returns the base fee per gas for the current block.
    /// </summary>
    public struct OpBaseFee : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.Header.BaseFeePerGas;
    }

    /// <summary>
    /// Returns the blob base fee from the block header.
    /// Throws an exception if the blob base fee is not set.
    /// </summary>
    public struct OpBlobBaseFee : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
        {
            // If the blob base fee is missing, this opcode is invalid.
            UInt256? blobBaseFee = vmState.Env.TxExecutionContext.BlockExecutionContext.BlobBaseFee;
            if (!blobBaseFee.HasValue) ThrowBadInstruction();

            result = blobBaseFee.Value;

            [DoesNotReturn]
            [StackTraceHidden]
            static void ThrowBadInstruction() => throw new BadInstructionException();
        }
    }

    /// <summary>
    /// Returns the gas price for the transaction.
    /// </summary>
    public struct OpGasPrice : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.TxExecutionContext.GasPrice;
    }

    /// <summary>
    /// Returns the value transferred with the current call.
    /// </summary>
    public struct OpCallValue : IOpEnvUInt256
    {
        public static void Operation(EvmState vmState, out UInt256 result)
            => result = vmState.Env.Value;
    }

    /// <summary>
    /// Returns the address of the currently executing account.
    /// </summary>
    public struct OpAddress : IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.ExecutingAccount.Bytes;
    }

    /// <summary>
    /// Returns the address of the caller of the current execution context.
    /// </summary>
    public struct OpCaller : IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.Caller.Bytes;
    }

    /// <summary>
    /// Returns the origin address of the transaction.
    /// </summary>
    public struct OpOrigin : IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.TxExecutionContext.Origin.Bytes;
    }

    /// <summary>
    /// Returns the coinbase (beneficiary) address for the current block.
    /// </summary>
    public struct OpCoinbase : IOpEnvBytes
    {
        public static void Operation(EvmState vmState, out Span<byte> result)
            => result = vmState.Env.TxExecutionContext.BlockExecutionContext.Header.GasBeneficiary.Bytes;
    }

    /// <summary>
    /// Pushes the chain identifier onto the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionChainId(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.Base;
        // The chain ID is stored as a byte array in the VM
        stack.PushBytes(vm.ChainId);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Retrieves and pushes the balance of an account.
    /// The address is popped from the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBalance(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        // Deduct gas cost for balance operation as per specification.
        gasAvailable -= spec.GetBalanceCost();

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;

        // Charge gas for account access. If insufficient gas remains, abort.
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) goto OutOfGas;

        UInt256 result = vm.WorldState.GetBalance(address);
        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Pushes the balance of the executing account onto the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSelfBalance(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.SelfBalance;

        // Get balance for currently executing account.
        UInt256 result = vm.WorldState.GetBalance(vm.EvmState.Env.ExecutingAccount);
        stack.PushUInt256(in result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Retrieves the code hash of an external account.
    /// Returns zero if the account does not exist or is considered dead.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeHash(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        gasAvailable -= spec.GetExtCodeHashCost();

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;
        // Check if enough gas for account access and charge accordingly.
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) goto OutOfGas;

        IWorldState state = vm.WorldState;
        // For dead accounts, the specification requires pushing zero.
        if (state.IsDeadAccount(address))
        {
            stack.PushZero();
        }
        else
        {
            // Otherwise, push the account's code hash.
            stack.PushBytes(state.GetCodeHash(address).Bytes);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Retrieves the code hash of an external account, considering the possibility of an EOF-validated contract.
    /// If the code is an EOF contract, a predefined EOF hash is pushed.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeHashEof(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        IReleaseSpec spec = vm.Spec;
        gasAvailable -= spec.GetExtCodeHashCost();

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) goto OutOfGas;

        IWorldState state = vm.WorldState;
        if (state.IsDeadAccount(address))
        {
            stack.PushZero();
        }
        else
        {
            Memory<byte> code = state.GetCode(address);
            // If the code passes EOF validation, push the EOF-specific hash.
            if (EofValidator.IsEof(code, out _))
            {
                stack.PushBytes(EofHash256);
            }
            else
            {
                // Otherwise, push the standard code hash.
                stack.PushBytes(state.GetCodeHash(address).Bytes);
            }
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Implements the PREVRANDAO opcode.
    /// Pushes the previous random value (post-merge) or block difficulty (pre-merge) onto the stack.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionPrevRandao(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Charge the base gas cost for this opcode.
        gasAvailable -= GasCostOf.Base;
        BlockHeader header = vm.EvmState.Env.TxExecutionContext.BlockExecutionContext.Header;

        // Use the random value if post-merge; otherwise, use block difficulty.
        if (header.IsPostMerge)
        {
            stack.PushBytes(header.Random.Bytes);
        }
        else
        {
            UInt256 result = header.Difficulty;
            stack.PushUInt256(in result);
        }

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Charges gas for accessing an account, including potential delegation lookups.
    /// This method ensures that both the requested account and its delegated account (if any) are properly charged.
    /// </summary>
    /// <param name="gasAvailable">Reference to the available gas which will be updated.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="chargeForWarm">If true, charge even if the account is already warm.</param>
    /// <returns>True if gas was successfully charged; otherwise false.</returns>
    private static bool ChargeAccountAccessGasWithDelegation(ref long gasAvailable, VirtualMachine vm, Address address, bool chargeForWarm = true)
    {
        IReleaseSpec spec = vm.Spec;
        if (!spec.UseHotAndColdStorage)
        {
            // No extra cost if hot/cold storage is not used.
            return true;
        }
        bool notOutOfGas = ChargeAccountAccessGas(ref gasAvailable, vm, address, chargeForWarm);
        return notOutOfGas
               && (!vm.EvmState.Env.TxExecutionContext.CodeInfoRepository.TryGetDelegation(vm.WorldState, address, spec, out Address delegated)
                   // Charge additional gas for the delegated account if it exists.
                   || ChargeAccountAccessGas(ref gasAvailable, vm, delegated, chargeForWarm));
    }

    /// <summary>
    /// Charges gas for accessing an account based on its storage state (cold vs. warm).
    /// Precompiles are treated as exceptions to the cold/warm gas charge.
    /// </summary>
    /// <param name="gasAvailable">Reference to the available gas which will be updated.</param>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="address">The target account address.</param>
    /// <param name="chargeForWarm">If true, applies the warm read gas cost even if the account is warm.</param>
    /// <returns>True if the gas charge was successful; otherwise false.</returns>
    public static bool ChargeAccountAccessGas(ref long gasAvailable, VirtualMachine vm, Address address, bool chargeForWarm = true)
    {
        bool result = true;
        IReleaseSpec spec = vm.Spec;
        if (spec.UseHotAndColdStorage)
        {
            EvmState vmState = vm.EvmState;
            if (vm.TxTracer.IsTracingAccess)
            {
                // Ensure that tracing simulates access-list behavior.
                vmState.AccessTracker.WarmUp(address);
            }

            // If the account is cold (and not a precompile), charge the cold access cost.
            if (vmState.AccessTracker.IsCold(address) && !address.IsPrecompile(spec))
            {
                result = UpdateGas(GasCostOf.ColdAccountAccess, ref gasAvailable);
                vmState.AccessTracker.WarmUp(address);
            }
            else if (chargeForWarm)
            {
                // Otherwise, if warm access should be charged, apply the warm read cost.
                result = UpdateGas(GasCostOf.WarmStateRead, ref gasAvailable);
            }
        }

        return result;
    }
}
