// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Nethermind.Evm;
using Int256;

internal sealed partial class EvmInstructions
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
    public static EvmExceptionType InstructionProgramCounter(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
    {
        // Deduct the base gas cost for reading the program counter.
        gasAvailable -= GasCostOf.Base;
        // The program counter pushed is adjusted by -1 to reflect the correct opcode location.
        stack.PushUInt32((uint)(programCounter - 1));

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
        gasAvailable -= GasCostOf.Mid;
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
        gasAvailable -= GasCostOf.High;
        // Pop the jump destination.
        if (!stack.PopUInt256(out UInt256 result)) goto StackUnderflow;
        // Pop the condition as a byte reference.
        ref byte condition = ref stack.PopBytesByRef();
        if (Unsafe.IsNullRef(in condition)) goto StackUnderflow;
        // If the condition is non-zero (i.e., true), attempt to perform the jump.
        if (Unsafe.As<byte, Vector256<byte>>(ref condition) != default)
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
        bool isJumpDestination = true;
        // Check if the jump destination exceeds the maximum allowed integer value.
        if (jumpDestination > int.MaxValue)
        {
            isJumpDestination = false;
        }
        else
        {
            // Extract the jump destination from the lowest limb of the UInt256.
            int jumpDestinationInt = (int)jumpDestination.u0;
            // Validate that the jump destination corresponds to a valid jump marker in the code.
            if (!env.CodeInfo.ValidateJump(jumpDestinationInt))
            {
                isJumpDestination = false;
            }
            else
            {
                // Update the program counter to the valid jump destination.
                programCounter = jumpDestinationInt;
            }
        }

        return isJumpDestination;
    }
}
