// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Crypto;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.State;

using static Nethermind.Evm.VirtualMachine;

namespace Nethermind.Evm;
using Int256;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Defines an environment introspection operation that returns a byte span.
    /// Implementations should provide a static gas cost and a static Operation method.
    /// </summary>
    public interface IOpEnvAddress
    {
        /// <summary>
        /// The gas cost for the operation.
        /// </summary>
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as address.
        /// </summary>
        /// <param name="vmState">The current virtual machine state.</param>
        abstract static Address Operation(EvmState vmState);
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
        abstract static ref readonly UInt256 Operation(EvmState vmState);
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
    /// Executes an environment introspection opcode that returns an Address.
    /// Generic parameter TOpEnv defines the concrete operation.
    /// </summary>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvAddress<TOpEnv, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvAddress
        where TTracingInst : struct, IFlag
    {
        // Deduct the gas cost as defined by the operation implementation.
        gasAvailable -= TOpEnv.GasCost;

        // Execute the operation and retrieve the result.
        Address result = TOpEnv.Operation(vm.EvmState);

        // Push the resulting bytes onto the EVM stack.
        stack.PushAddress<TTracingInst>(result);

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
    public static EvmExceptionType InstructionEnvUInt256<TOpEnv, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvUInt256
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= TOpEnv.GasCost;

        ref readonly UInt256 result = ref TOpEnv.Operation(vm.EvmState);

        stack.PushUInt256<TTracingInst>(in result);

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
    public static EvmExceptionType InstructionEnvUInt32<TOpEnv, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvUInt32
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= TOpEnv.GasCost;

        uint result = TOpEnv.Operation(vm.EvmState);

        stack.PushUInt32<TTracingInst>(result);

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
    public static EvmExceptionType InstructionEnvUInt64<TOpEnv, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpEnv : struct, IOpEnvUInt64
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= TOpEnv.GasCost;

        ulong result = TOpEnv.Operation(vm.EvmState);

        stack.PushUInt64<TTracingInst>(result);

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
            => vmState.Env.TxExecutionContext.BlockExecutionContext.Number;
    }

    /// <summary>
    /// Returns the gas limit of the current block.
    /// </summary>
    public struct OpGasLimit : IOpEnvUInt64
    {
        public static ulong Operation(EvmState vmState)
            => vmState.Env.TxExecutionContext.BlockExecutionContext.GasLimit;
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
        public static ref readonly UInt256 Operation(EvmState vmState)
            => ref vmState.Env.TxExecutionContext.BlockExecutionContext.Header.BaseFeePerGas;
    }

    /// <summary>
    /// Implements the BLOBBASEFEE opcode.
    /// Returns the blob base fee from the block header.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/>, or <see cref="EvmExceptionType.BadInstruction"/> if blob base fee not set.
    /// </returns>
    public static EvmExceptionType InstructionBlobBaseFee<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        ref readonly BlockExecutionContext context = ref vm.EvmState.Env.TxExecutionContext.BlockExecutionContext;
        // If the blob base fee is missing (no ExcessBlobGas set), this opcode is invalid.
        if (!context.Header.ExcessBlobGas.HasValue) goto BadInstruction;

        // Charge the base gas cost for this opcode.
        gasAvailable -= GasCostOf.Base;
        stack.Push32Bytes<TTracingInst>(in context.BlobBaseFee);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Returns the gas price for the transaction.
    /// </summary>
    public struct OpGasPrice : IOpEnvUInt256
    {
        public static ref readonly UInt256 Operation(EvmState vmState)
            => ref vmState.Env.TxExecutionContext.GasPrice;
    }

    /// <summary>
    /// Returns the value transferred with the current call.
    /// </summary>
    public struct OpCallValue : IOpEnvUInt256
    {
        public static ref readonly UInt256 Operation(EvmState vmState)
            => ref vmState.Env.Value;
    }

    /// <summary>
    /// Returns the address of the currently executing account.
    /// </summary>
    public struct OpAddress : IOpEnvAddress
    {
        public static Address Operation(EvmState vmState)
            => vmState.Env.ExecutingAccount;
    }

    /// <summary>
    /// Returns the address of the caller of the current execution context.
    /// </summary>
    public struct OpCaller : IOpEnvAddress
    {
        public static Address Operation(EvmState vmState)
            => vmState.Env.Caller;
    }

    /// <summary>
    /// Returns the origin address of the transaction.
    /// </summary>
    public struct OpOrigin : IOpEnvAddress
    {
        public static Address Operation(EvmState vmState)
            => vmState.Env.TxExecutionContext.Origin;
    }

    /// <summary>
    /// Returns the coinbase (beneficiary) address for the current block.
    /// </summary>
    public struct OpCoinbase : IOpEnvAddress
    {
        public static Address Operation(EvmState vmState)
            => vmState.Env.TxExecutionContext.BlockExecutionContext.Coinbase;
    }

    /// <summary>
    /// Pushes the chain identifier onto the stack.
    /// </summary>
    /// <returns>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <see cref="EvmExceptionType.None"/>
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionChainId<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= GasCostOf.Base;
        // The chain ID is stored as a byte array in the VM
        stack.Push32Bytes<TTracingInst>(in vm.ChainId);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Retrieves and pushes the balance of an account.
    /// The address is popped from the stack.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if gas is available,
    /// <see cref="EvmExceptionType.OutOfGas"/> if the gas becomes negative
    /// or <see cref="EvmExceptionType.StackUnderflow"/> if not enough items on stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBalance<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;
        // Deduct gas cost for balance operation as per specification.
        gasAvailable -= spec.GetBalanceCost();

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;

        // Charge gas for account access. If insufficient gas remains, abort.
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) goto OutOfGas;

        ref readonly UInt256 result = ref vm.WorldState.GetBalance(address);
        stack.PushUInt256<TTracingInst>(in result);

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
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/>
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSelfBalance<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        gasAvailable -= GasCostOf.SelfBalance;

        // Get balance for currently executing account.
        ref readonly UInt256 result = ref vm.WorldState.GetBalance(vm.EvmState.Env.ExecutingAccount);
        stack.PushUInt256<TTracingInst>(in result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Retrieves the code hash of an external account.
    /// Returns zero if the account does not exist or is considered dead.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if gas is available,
    /// <see cref="EvmExceptionType.OutOfGas"/> if the gas becomes negative
    /// or <see cref="EvmExceptionType.StackUnderflow"/> if not enough items on stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeHash<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
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
            stack.PushZero<TTracingInst>();
        }
        else
        {
            // Otherwise, push the account's code hash.
            ref readonly ValueHash256 hash = ref state.GetCodeHash(address);
            stack.Push32Bytes<TTracingInst>(in hash);
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
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack where the gas value will be pushed.</param>
    /// <param name="gasAvailable">Reference to the current available gas, which is modified by this operation.</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if gas is available,
    /// <see cref="EvmExceptionType.OutOfGas"/> if the gas becomes negative
    /// or <see cref="EvmExceptionType.StackUnderflow"/> if not enough items on stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeHashEof<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;
        gasAvailable -= spec.GetExtCodeHashCost();

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, address)) goto OutOfGas;

        IWorldState state = vm.WorldState;
        if (state.IsDeadAccount(address))
        {
            stack.PushZero<TTracingInst>();
        }
        else
        {
            Memory<byte> code = state.GetCode(address);
            // If the code passes EOF validation, push the EOF-specific hash.
            if (EofValidator.IsEof(code, out _))
            {
                stack.PushBytes<TTracingInst>(EofHash256);
            }
            else
            {
                // Otherwise, push the standard code hash.
                stack.PushBytes<TTracingInst>(state.GetCodeHash(address).Bytes);
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
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/>
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionPrevRandao<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Charge the base gas cost for this opcode.
        gasAvailable -= GasCostOf.Base;
        stack.Push32Bytes<TTracingInst>(in vm.EvmState.Env.TxExecutionContext.BlockExecutionContext.PrevRandao);
        return EvmExceptionType.None;
    }

    /// <summary>
    /// Pushes the remaining gas onto the stack.
    /// The gas available is decremented by the base cost, and if negative, an OutOfGas error is returned.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack where the gas value will be pushed.</param>
    /// <param name="gasAvailable">Reference to the current available gas, which is modified by this operation.</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if gas is available, or <see cref="EvmExceptionType.OutOfGas"/> if the gas becomes negative.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionGas<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Deduct the base gas cost for reading gas.
        gasAvailable -= GasCostOf.Base;

        // If gas falls below zero after cost deduction, signal out-of-gas error.
        if (gasAvailable < 0) goto OutOfGas;

        // Push the remaining gas (as unsigned 64-bit) onto the stack.
        stack.PushUInt64<TTracingInst>((ulong)gasAvailable);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    }

    /// <summary>
    /// Computes the blob hash from the provided blob versioned hashes.
    /// Pops an index from the stack and uses it to select a blob hash from the versioned hashes array.
    /// If the index is invalid, pushes zero.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack from which the index is popped and where the blob hash is pushed.</param>
    /// <param name="gasAvailable">Reference to the available gas; reduced by the blob hash cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; otherwise, <see cref="EvmExceptionType.StackUnderflow"/>
    /// if there are insufficient elements on the stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlobHash<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;

        // Deduct the gas cost for blob hash operation.
        gasAvailable -= GasCostOf.BlobHash;

        // Pop the blob index from the stack.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        // Retrieve the array of versioned blob hashes from the execution context.
        byte[][] versionedHashes = vm.EvmState.Env.TxExecutionContext.BlobVersionedHashes;

        // If versioned hashes are available and the index is within range, push the corresponding blob hash.
        // Otherwise, push zero.
        if (versionedHashes is not null && result < versionedHashes.Length)
        {
            stack.PushBytes<TTracingInst>(versionedHashes[result.u0]);
        }
        else
        {
            stack.PushZero<TTracingInst>();
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Retrieves a block hash for a given block number.
    /// Pops a block number from the stack, validates it, and then pushes the corresponding block hash.
    /// If no valid block hash exists, pushes a zero value.
    /// Additionally, reports the block hash if block hash tracing is enabled.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack from which the block number is popped and where the block hash is pushed.</param>
    /// <param name="gasAvailable">Reference to the available gas; reduced by the block hash operation cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully;
    /// otherwise, <see cref="EvmExceptionType.StackUnderflow"/> if there are insufficient stack elements.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlockHash<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Deduct the gas cost for block hash operation.
        gasAvailable -= GasCostOf.BlockHash;

        // Pop the block number from the stack.
        if (!stack.PopUInt256(out UInt256 a)) goto StackUnderflow;

        // Convert the block number to a long. Clamp the value to long.MaxValue if it exceeds it.
        long number = a > long.MaxValue ? long.MaxValue : (long)a.u0;

        // Retrieve the block hash for the given block number.
        Hash256? blockHash = vm.BlockHashProvider.GetBlockhash(vm.EvmState.Env.TxExecutionContext.BlockExecutionContext.Header, number);

        // Push the block hash bytes if available; otherwise, push a 32-byte zero value.
        stack.PushBytes<TTracingInst>(blockHash is not null ? blockHash.Bytes : BytesZero32);

        // If block hash tracing is enabled and a valid block hash was obtained, report it.
        if (vm.TxTracer.IsTracingBlockHash && blockHash is not null)
        {
            vm.TxTracer.ReportBlockHash(blockHash);
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
