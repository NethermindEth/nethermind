// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Core.Crypto;
using Nethermind.Evm.EvmObjectFormat;
using Nethermind.Evm.GasPolicy;
using Nethermind.Evm.State;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

using Int256;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Defines an environment introspection operation that returns a byte span.
    /// Implementations should provide a static gas cost and a static Operation method.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type parameter.</typeparam>
    public interface IOpBlkAddress<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        /// <summary>
        /// The gas cost for the operation.
        /// </summary>
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as address.
        /// </summary>
        /// <param name="vm">The current virtual machine instance.</param>
        abstract static Address Operation(VirtualMachine<TGasPolicy> vm);
    }

    /// <summary>
    /// Defines an environment introspection operation that returns a big endian word.
    /// Implementations should provide a static gas cost and a static Operation method.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type parameter.</typeparam>
    public interface IOpEnv32Bytes<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        /// <summary>
        /// The gas cost for the operation.
        /// </summary>
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as ref to big endian word.
        /// </summary>
        /// <param name="vm">The current virtual machine instance.</param>
        abstract static ref readonly ValueHash256 Operation(VirtualMachine<TGasPolicy> vm);
    }

    /// <summary>
    /// Defines an environment introspection operation that returns an Address.
    /// Implementations should provide a static gas cost and a static Operation method.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type parameter.</typeparam>
    public interface IOpEnvAddress<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        /// <summary>
        /// The gas cost for the operation.
        /// </summary>
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as address.
        /// </summary>
        /// <param name="vmState">The current virtual machine state.</param>
        abstract static Address Operation(VmState<TGasPolicy> vmState);
    }

    /// <summary>
    /// Defines an environment introspection operation that returns a 256-bit unsigned integer.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type parameter.</typeparam>
    public interface IOpEnvUInt256<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as a UInt256.
        /// </summary>
        /// <param name="vmState">The current virtual machine state.</param>
        /// <param name="result">The resulting 256-bit unsigned integer.</param>
        abstract static ref readonly UInt256 Operation(VmState<TGasPolicy> vmState);
    }

    /// <summary>
    /// Defines an environment introspection operation that returns a 256-bit unsigned integer.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type parameter.</typeparam>
    public interface IOpBlkUInt256<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as a UInt256.
        /// </summary>
        /// <param name="vm">The current virtual machine instance.</param>
        /// <param name="result">The resulting 256-bit unsigned integer.</param>
        abstract static ref readonly UInt256 Operation(VirtualMachine<TGasPolicy> vm);
    }

    /// <summary>
    /// Defines an environment introspection operation that returns a 32-bit unsigned integer.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type parameter.</typeparam>
    public interface IOpEnvUInt32<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as a UInt32.
        /// </summary>
        /// <param name="vmState">The current virtual machine state.</param>
        abstract static uint Operation(VmState<TGasPolicy> vmState);
    }

    /// <summary>
    /// Defines an environment introspection operation that returns a 64-bit unsigned integer.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type parameter.</typeparam>
    public interface IOpEnvUInt64<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as a UInt64.
        /// </summary>
        /// <param name="vmState">The current virtual machine state.</param>
        abstract static ulong Operation(VmState<TGasPolicy> vmState);
    }

    /// <summary>
    /// Defines an environment introspection operation that returns a 64-bit unsigned integer.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy type parameter.</typeparam>
    public interface IOpBlkUInt64<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        virtual static long GasCost => GasCostOf.Base;
        /// <summary>
        /// Executes the operation and returns the result as a UInt64.
        /// </summary>
        /// <param name="vm">The current virtual machine instance.</param>
        abstract static ulong Operation(VirtualMachine<TGasPolicy> vm);
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns an Address.
    /// Generic parameter TOpEnv defines the concrete operation.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvAddress<TGasPolicy, TOpEnv, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpEnv : struct, IOpEnvAddress<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Deduct the gas cost as defined by the operation implementation.
        TGasPolicy.Consume(ref gas, TOpEnv.GasCost);

        // Execute the operation and retrieve the result.
        Address result = TOpEnv.Operation(vm.VmState);

        // Push the resulting bytes onto the EVM stack.
        return stack.PushAddress<TTracingInst>(result);
    }

    /// <summary>
    /// Executes an block introspection opcode that returns an Address.
    /// Generic parameter TOpEnv defines the concrete operation.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlkAddress<TGasPolicy, TOpEnv, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpEnv : struct, IOpBlkAddress<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Deduct the gas cost as defined by the operation implementation.
        TGasPolicy.Consume(ref gas, TOpEnv.GasCost);

        // Execute the operation and retrieve the result.
        Address result = TOpEnv.Operation(vm);

        // Push the resulting bytes onto the EVM stack.
        return stack.PushAddress<TTracingInst>(result);
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns a UInt256 value.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt256<TGasPolicy, TOpEnv, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpEnv : struct, IOpEnvUInt256<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, TOpEnv.GasCost);

        ref readonly UInt256 result = ref TOpEnv.Operation(vm.VmState);

        stack.PushUInt256<TTracingInst>(in result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns a UInt256 value.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlkUInt256<TGasPolicy, TOpEnv, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpEnv : struct, IOpBlkUInt256<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, TOpEnv.GasCost);

        ref readonly UInt256 result = ref TOpEnv.Operation(vm);

        stack.PushUInt256<TTracingInst>(in result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns a UInt32 value.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt32<TGasPolicy, TOpEnv, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpEnv : struct, IOpEnvUInt32<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, TOpEnv.GasCost);

        uint result = TOpEnv.Operation(vm.VmState);

        stack.PushUInt32<TTracingInst>(result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns a UInt64 value.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnvUInt64<TGasPolicy, TOpEnv, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpEnv : struct, IOpEnvUInt64<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, TOpEnv.GasCost);

        ulong result = TOpEnv.Operation(vm.VmState);

        stack.PushUInt64<TTracingInst>(result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns a UInt64 value.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlkUInt64<TGasPolicy, TOpEnv, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpEnv : struct, IOpBlkUInt64<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, TOpEnv.GasCost);

        ulong result = TOpEnv.Operation(vm);

        stack.PushUInt64<TTracingInst>(result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an environment introspection opcode that returns a UInt64 value.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpEnv">The specific operation implementation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>An EVM exception type if an error occurs.</returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionEnv32Bytes<TGasPolicy, TOpEnv, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpEnv : struct, IOpEnv32Bytes<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, TOpEnv.GasCost);

        ref readonly ValueHash256 result = ref TOpEnv.Operation(vm);

        return stack.Push32Bytes<TTracingInst>(in result);
    }

    /// <summary>
    /// Returns the size of the transaction call data.
    /// </summary>
    public struct OpCallDataSize<TGasPolicy> : IOpEnvUInt32<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Operation(VmState<TGasPolicy> vmState)
            => (uint)vmState.Env.InputData.Length;
    }

    /// <summary>
    /// Returns the size of the executing code.
    /// </summary>
    public struct OpCodeSize<TGasPolicy> : IOpEnvUInt32<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static uint Operation(VmState<TGasPolicy> vmState)
            => (uint)vmState.Env.CodeInfo.CodeSpan.Length;
    }

    /// <summary>
    /// Returns the timestamp of the current block.
    /// </summary>
    public struct OpTimestamp<TGasPolicy> : IOpBlkUInt64<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Operation(VirtualMachine<TGasPolicy> vm)
            => vm.BlockExecutionContext.Header.Timestamp;
    }

    /// <summary>
    /// Returns the block number of the current block.
    /// </summary>
    public struct OpNumber<TGasPolicy> : IOpBlkUInt64<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Operation(VirtualMachine<TGasPolicy> vm)
            => vm.BlockExecutionContext.Number;
    }

    /// <summary>
    /// Returns the gas limit of the current block.
    /// </summary>
    public struct OpGasLimit<TGasPolicy> : IOpBlkUInt64<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Operation(VirtualMachine<TGasPolicy> vm)
            => vm.BlockExecutionContext.GasLimit;
    }

    /// <summary>
    /// Returns the current size of the EVM memory.
    /// </summary>
    public struct OpMSize<TGasPolicy> : IOpEnvUInt64<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ulong Operation(VmState<TGasPolicy> vmState)
            => vmState.Memory.Size;
    }

    /// <summary>
    /// Returns the base fee per gas for the current block.
    /// </summary>
    public struct OpBaseFee<TGasPolicy> : IOpBlkUInt256<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly UInt256 Operation(VirtualMachine<TGasPolicy> vm)
            => ref vm.BlockExecutionContext.Header.BaseFeePerGas;
    }

    /// <summary>
    /// Implements the BLOBBASEFEE opcode.
    /// Returns the blob base fee from the block header.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/>, or <see cref="EvmExceptionType.BadInstruction"/> if blob base fee not set.
    /// </returns>
    public static EvmExceptionType InstructionBlobBaseFee<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        ref readonly BlockExecutionContext context = ref vm.BlockExecutionContext;
        // If the blob base fee is missing (no ExcessBlobGas set), this opcode is invalid.
        if (!context.Header.ExcessBlobGas.HasValue) goto BadInstruction;

        // Charge the base gas cost for this opcode.
        TGasPolicy.Consume(ref gas, GasCostOf.Base);
        return stack.Push32Bytes<TTracingInst>(in context.BlobBaseFee);

    // Jump forward to be unpredicted by the branch predictor.
    BadInstruction:
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Returns the gas price for the transaction.
    /// </summary>
    public struct OpGasPrice<TGasPolicy> : IOpBlkUInt256<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly UInt256 Operation(VirtualMachine<TGasPolicy> vm)
            => ref vm.TxExecutionContext.GasPrice;
    }

    /// <summary>
    /// Returns the value transferred with the current call.
    /// </summary>
    public struct OpCallValue<TGasPolicy> : IOpEnvUInt256<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly UInt256 Operation(VmState<TGasPolicy> vmState)
            => ref vmState.Env.Value;
    }

    /// <summary>
    /// Returns the address of the currently executing account.
    /// </summary>
    public struct OpAddress<TGasPolicy> : IOpEnvAddress<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Address Operation(VmState<TGasPolicy> vmState)
            => vmState.Env.ExecutingAccount;
    }

    /// <summary>
    /// Returns the address of the caller of the current execution context.
    /// </summary>
    public struct OpCaller<TGasPolicy> : IOpEnvAddress<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Address Operation(VmState<TGasPolicy> vmState)
            => vmState.Env.Caller;
    }

    /// <summary>
    /// Returns the origin address of the transaction.
    /// </summary>
    public struct OpOrigin<TGasPolicy> : IOpEnv32Bytes<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly ValueHash256 Operation(VirtualMachine<TGasPolicy> vm)
            => ref vm.TxExecutionContext.Origin;
    }

    /// <summary>
    /// Returns the coinbase (beneficiary) address for the current block.
    /// </summary>
    public struct OpCoinbase<TGasPolicy> : IOpBlkAddress<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Address Operation(VirtualMachine<TGasPolicy> vm)
            => vm.BlockExecutionContext.Coinbase;
    }

    /// <summary>
    /// Returns the chain identifier.
    /// </summary>
    public struct OpChainId<TGasPolicy> : IOpEnv32Bytes<TGasPolicy>
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref readonly ValueHash256 Operation(VirtualMachine<TGasPolicy> vm)
            => ref vm.ChainId;
    }

    /// <summary>
    /// Retrieves and pushes the balance of an account.
    /// The address is popped from the stack.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if gas is available,
    /// <see cref="EvmExceptionType.OutOfGas"/> if the gas becomes negative
    /// or <see cref="EvmExceptionType.StackUnderflow"/> if not enough items on stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBalance<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;
        // Deduct gas cost for balance operation as per specification.
        TGasPolicy.Consume(ref gas, spec.GetBalanceCost());

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;

        // Charge gas for account access. If insufficient gas remains, abort.
        if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, spec, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, address)) goto OutOfGas;

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
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/>
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionSelfBalance<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        TGasPolicy.Consume(ref gas, GasCostOf.SelfBalance);

        // Get balance for currently executing account.
        ref readonly UInt256 result = ref vm.WorldState.GetBalance(vm.VmState.Env.ExecutingAccount);
        stack.PushUInt256<TTracingInst>(in result);

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Retrieves the code hash of an external account.
    /// Returns zero if the account does not exist or is considered dead.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if gas is available,
    /// <see cref="EvmExceptionType.OutOfGas"/> if the gas becomes negative
    /// or <see cref="EvmExceptionType.StackUnderflow"/> if not enough items on stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeHash<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;
        TGasPolicy.Consume(ref gas, spec.GetExtCodeHashCost());

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;
        // Check if enough gas for account access and charge accordingly.
        if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, spec, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, address)) goto OutOfGas;

        IWorldState state = vm.WorldState;
        // For dead accounts, the specification requires pushing zero.
        if (state.IsDeadAccount(address))
        {
            return stack.PushZero<TTracingInst>();
        }
        else
        {
            // Otherwise, push the account's code hash.
            ref readonly ValueHash256 hash = ref state.GetCodeHash(address);
            return stack.Push32Bytes<TTracingInst>(in hash);
        }

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
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack where the gas value will be pushed.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if gas is available,
    /// <see cref="EvmExceptionType.OutOfGas"/> if the gas becomes negative
    /// or <see cref="EvmExceptionType.StackUnderflow"/> if not enough items on stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExtCodeHashEof<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        IReleaseSpec spec = vm.Spec;
        TGasPolicy.Consume(ref gas, spec.GetExtCodeHashCost());

        Address address = stack.PopAddress();
        if (address is null) goto StackUnderflow;
        if (!TGasPolicy.ConsumeAccountAccessGas(ref gas, spec, in vm.VmState.AccessTracker, vm.TxTracer.IsTracingAccess, address)) goto OutOfGas;

        IWorldState state = vm.WorldState;
        if (state.IsDeadAccount(address))
        {
            return stack.PushZero<TTracingInst>();
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
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/>
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionPrevRandao<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Charge the base gas cost for this opcode.
        TGasPolicy.Consume(ref gas, GasCostOf.Base);
        return stack.Push32Bytes<TTracingInst>(in vm.BlockExecutionContext.PrevRandao);
    }

    /// <summary>
    /// Pushes the remaining gas onto the stack.
    /// The gas available is decremented by the base cost, and if negative, an OutOfGas error is returned.
    /// </summary>
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack where the gas value will be pushed.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if gas is available, or <see cref="EvmExceptionType.OutOfGas"/> if the gas becomes negative.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionGas<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Deduct the base gas cost for reading gas.
        TGasPolicy.Consume(ref gas, GasCostOf.Base);

        // If gas falls below zero after cost deduction, signal out-of-gas error.
        if (TGasPolicy.GetRemainingGas(in gas) < 0) goto OutOfGas;

        // Push the remaining gas (as unsigned 64-bit) onto the stack.
        stack.PushUInt64<TTracingInst>((ulong)TGasPolicy.GetRemainingGas(in gas));

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
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack from which the index is popped and where the blob hash is pushed.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; otherwise, <see cref="EvmExceptionType.StackUnderflow"/>
    /// if there are insufficient elements on the stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlobHash<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Deduct the gas cost for blob hash operation.
        TGasPolicy.Consume(ref gas, GasCostOf.BlobHash);

        // Pop the blob index from the stack.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        // Retrieve the array of versioned blob hashes from the execution context.
        byte[][] versionedHashes = vm.TxExecutionContext.BlobVersionedHashes;

        // If versioned hashes are available and the index is within range, push the corresponding blob hash.
        // Otherwise, push zero.
        if (versionedHashes is not null && result < versionedHashes.Length)
        {
            stack.PushBytes<TTracingInst>(versionedHashes[result.u0]);
        }
        else
        {
            return stack.PushZero<TTracingInst>();
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
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack from which the block number is popped and where the block hash is pushed.</param>
    /// <param name="gas">Reference to the gas state, updated by the operation's cost.</param>
    /// <param name="programCounter">The program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully;
    /// otherwise, <see cref="EvmExceptionType.StackUnderflow"/> if there are insufficient stack elements.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionBlockHash<TGasPolicy, TTracingInst>(VirtualMachine<TGasPolicy> vm, ref EvmStack stack, ref TGasPolicy gas, ref int programCounter)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Deduct the gas cost for block hash operation.
        TGasPolicy.Consume(ref gas, GasCostOf.BlockHash);

        // Pop the block number from the stack.
        if (!stack.PopUInt256(out UInt256 a)) goto StackUnderflow;

        // Convert the block number to a long. Clamp the value to long.MaxValue if it exceeds it.
        long number = a > long.MaxValue ? long.MaxValue : (long)a.u0;

        // Retrieve the block hash for the given block number.
        BlockHeader header = vm.BlockExecutionContext.Header;
        Hash256? blockHash = number >= header.Number ?
            null : // Current block or higher is null, don't bother looking up
            vm.BlockHashProvider.GetBlockhash(header, number, vm.Spec);

        // Push the block hash bytes if available; otherwise, push a 32-byte zero value.
        if (blockHash is not null)
        {
            // If block hash tracing is enabled and a valid block hash was obtained, report it.
            if (vm.TxTracer.IsTracingBlockHash)
            {
                vm.TxTracer.ReportBlockHash(blockHash);
            }
            return stack.Push32Bytes<TTracingInst>(in blockHash.ValueHash256);
        }
        else
        {
            return stack.PushZero<TTracingInst>();
        }

    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
