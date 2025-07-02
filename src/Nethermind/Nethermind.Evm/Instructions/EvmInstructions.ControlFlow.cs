// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Specs;
using Nethermind.Core;
using Nethermind.State;

namespace Nethermind.Evm;
using Int256;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Pushes the current program counter (minus one) onto the EVM stack.
    /// This is used to obtain the current execution point within the code.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack where the program counter is pushed.</param>
    /// <param name="gasAvailable">Reference to the remaining gas; reduced by the gas cost.</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionProgramCounter<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Deduct the base gas cost for reading the program counter.
        gasAvailable -= GasCostOf.Base;
        // The program counter pushed is adjusted by -1 to reflect the correct opcode location.
        stack.PushUInt32<TTracingInst>((uint)(programCounter - 1));

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Marks a valid jump destination.
    /// This instruction only deducts the jump destination gas cost without modifying the stack.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">Reference to the remaining gas; reduced by the jump destination cost.</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionJumpDest(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Deduct the gas cost specific for a jump destination marker.
        gasAvailable -= GasCostOf.JumpDest;

        return EvmExceptionType.None;
    }

    /// <summary>
    /// Executes an unconditional jump.
    /// Pops a jump destination from the stack and validates it.
    /// If the destination is valid, updates the program counter; otherwise, returns an error.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack from which the jump destination is popped.</param>
    /// <param name="gasAvailable">Reference to the remaining gas; reduced by the gas cost for jumping.</param>
    /// <param name="programCounter">Reference to the program counter that may be updated with the jump destination.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; <see cref="EvmExceptionType.StackUnderflow"/> or <see cref="EvmExceptionType.InvalidJumpDestination"/>
    /// on failure.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionJump(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Deduct the gas cost for performing a jump.
        gasAvailable -= GasCostOf.Jump;
        // Pop the jump destination from the stack.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;
        // Validate the jump destination and update the program counter if valid.
        if (!Jump(result, ref programCounter, in vm.EvmState.Env)) goto InvalidJumpDestination;

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    InvalidJumpDestination:
        return EvmExceptionType.InvalidJumpDestination;
    }

    /// <summary>
    /// Executes a conditional jump.
    /// Pops a jump destination and a condition from the stack. If the condition is non-zero,
    /// attempts to jump to the specified destination.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack from which the jump destination and condition are popped.</param>
    /// <param name="gasAvailable">Reference to the remaining gas; reduced by the cost for conditional jump.</param>
    /// <param name="programCounter">Reference to the program counter that may be updated on a jump.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; returns <see cref="EvmExceptionType.StackUnderflow"/>
    /// or <see cref="EvmExceptionType.InvalidJumpDestination"/> on error.
    /// </returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static EvmExceptionType InstructionJumpIf(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Deduct the high gas cost for a conditional jump.
        gasAvailable -= GasCostOf.JumpI;
        // Pop the jump destination.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;

        bool shouldJump = TestJumpCondition(ref stack, out bool isOverflow);
        if (isOverflow) goto StackUnderflow;
        if (shouldJump)
        {
            if (!Jump(result, ref programCounter, in vm.EvmState.Env)) goto InvalidJumpDestination;
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    InvalidJumpDestination:
        return EvmExceptionType.InvalidJumpDestination;
    }

    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool TestJumpCondition(ref EvmStack stack, out bool isOverflow)
    {
        isOverflow = false;
        // Pop the condition as a byte reference.
        ref byte condition = ref stack.PopBytesByRef();
        if (Unsafe.IsNullRef(in condition))
        {
            isOverflow = true;
            return false;
        }
        // If the condition is non-zero (i.e., true), attempt to perform the jump.
        return (Unsafe.As<byte, Vector256<byte>>(ref condition) != default);
    }

    /// <summary>
    /// Stops the execution of the EVM.
    /// In EOFCREATE or TXCREATE executions, the STOP opcode is considered illegal.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionStop(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // In contract creation contexts, a STOP is not permitted.
        if (vm.EvmState.ExecutionType is ExecutionType.EOFCREATE or ExecutionType.TXCREATE)
        {
            return EvmExceptionType.BadInstruction;
        }

        return EvmExceptionType.Stop;
    }

    /// <summary>
    /// Implements the REVERT opcode.
    /// Pops a memory offset and length from the stack, updates memory gas cost, loads the return data,
    /// and returns a revert exception.
    /// </summary>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionRevert(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Attempt to pop memory offset and length; if either fails, signal a stack underflow.
        if (!stack.PopUInt256(out UInt256 position) ||
            !stack.PopUInt256(out UInt256 length))
        {
            goto StackUnderflow;
        }

        // Ensure sufficient gas for any required memory expansion.
        if (!UpdateMemoryCost(vm.EvmState, ref gasAvailable, in position, in length))
        {
            goto OutOfGas;
        }

        // Copy the specified memory region as return data.
        vm.ReturnData = vm.EvmState.Memory.Load(in position, in length).ToArray();

        return EvmExceptionType.Revert;
    // Jump forward to be unpredicted by the branch predictor.
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Executes the SELFDESTRUCT opcode.
    /// This method handles gas adjustments, account balance transfers,
    /// and marks the executing account for destruction.
    /// </summary>
    [SkipLocalsInit]
    private static EvmExceptionType InstructionSelfDestruct(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Increment metrics for self-destruct operations.
        Metrics.IncrementSelfDestructs();

        EvmState vmState = vm.EvmState;
        IReleaseSpec spec = vm.Spec;
        IWorldState state = vm.WorldState;

        // SELFDESTRUCT is forbidden during static calls.
        if (vmState.IsStatic)
            goto StaticCallViolation;

        // If Shanghai DDoS protection is active, charge the appropriate gas cost.
        if (spec.UseShanghaiDDosProtection)
        {
            gasAvailable -= GasCostOf.SelfDestructEip150;
        }

        // Pop the inheritor address from the stack; signal underflow if missing.
        Address inheritor = stack.PopAddress();
        if (inheritor is null)
            goto StackUnderflow;

        // Charge gas for account access; if insufficient, signal out-of-gas.
        if (!ChargeAccountAccessGas(ref gasAvailable, vm, inheritor, chargeForWarm: false))
            goto OutOfGas;

        Address executingAccount = vmState.Env.ExecutingAccount;
        bool createInSameTx = vmState.AccessTracker.CreateList.Contains(executingAccount);
        // Mark the executing account for destruction if allowed.
        if (!spec.SelfdestructOnlyOnSameTransaction || createInSameTx)
            vmState.AccessTracker.ToBeDestroyed(executingAccount);

        // Retrieve the current balance for transfer.
        UInt256 result = state.GetBalance(executingAccount);
        if (vm.TxTracer.IsTracingActions)
            vm.TxTracer.ReportSelfDestruct(executingAccount, result, inheritor);

        // For certain specs, charge gas if transferring to a dead account.
        if (spec.ClearEmptyAccountWhenTouched && !result.IsZero && state.IsDeadAccount(inheritor))
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                goto OutOfGas;
        }

        // If account creation rules apply, ensure gas is charged for new accounts.
        bool inheritorAccountExists = state.AccountExists(inheritor);
        if (!spec.ClearEmptyAccountWhenTouched && !inheritorAccountExists && spec.UseShanghaiDDosProtection)
        {
            if (!UpdateGas(GasCostOf.NewAccount, ref gasAvailable))
                goto OutOfGas;
        }

        // Create or update the inheritor account with the transferred balance.
        if (!inheritorAccountExists)
        {
            state.CreateAccount(inheritor, result);
        }
        else if (!inheritor.Equals(executingAccount))
        {
            state.AddToBalance(inheritor, result, spec);
        }

        // Special handling when SELFDESTRUCT is limited to the same transaction.
        if (spec.SelfdestructOnlyOnSameTransaction && !createInSameTx && inheritor.Equals(executingAccount))
            goto Stop; // Avoid burning ETH if contract is not destroyed per EIP clarification

        // Subtract the balance from the executing account.
        state.SubtractFromBalance(executingAccount, result, spec);

    // Jump forward to be unpredicted by the branch predictor.
    Stop:
        return EvmExceptionType.Stop;
    OutOfGas:
        return EvmExceptionType.OutOfGas;
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    StaticCallViolation:
        return EvmExceptionType.StaticCallViolation;
    }

    /// <summary>
    /// Handles invalid opcodes by deducting a high gas cost and returning a BadInstruction error.
    /// </summary>
    public static EvmExceptionType InstructionInvalid(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        gasAvailable -= GasCostOf.High;
        return EvmExceptionType.BadInstruction;
    }

    /// <summary>
    /// Default handler for undefined opcodes, always returning a BadInstruction error.
    /// </summary>
    public static EvmExceptionType InstructionBadInstruction(VirtualMachine _, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        => EvmExceptionType.BadInstruction;

    /// <summary>
    /// Validates a jump destination and, if valid, updates the program counter.
    /// A valid jump destination must be within the bounds of the code and pass validation rules.
    /// </summary>
    /// <param name="jumpDestination">The jump destination as a 256-bit unsigned integer.</param>
    /// <param name="programCounter">Reference to the program counter that will be updated if the destination is valid.</param>
    /// <param name="env">The current execution environment containing code information.</param>
    /// <returns>
    /// <c>true</c> if the destination is valid and the program counter is updated; otherwise, <c>false</c>.
    /// </returns>
    [SkipLocalsInit]
    private static bool Jump(in UInt256 jumpDestination, ref int programCounter, in ExecutionEnvironment env)
    {
        // Check if the jump destination exceeds the maximum allowed integer value.
        if (jumpDestination > int.MaxValue)
        {
            return false;
        }

        // Extract the jump destination from the lowest limb of the UInt256.
        return Jump((int)jumpDestination.u0, ref programCounter, in env);
    }

    private static bool Jump(int jumpDestination, ref int programCounter, in ExecutionEnvironment env)
    {
        // Validate that the jump destination corresponds to a valid jump marker in the code.
        if (!env.CodeInfo.ValidateJump(jumpDestination))
        {
            return false;
        }
        else
        {
            // Update the program counter to the valid jump destination.
            programCounter = jumpDestination;
        }

        return true;
    }
}
