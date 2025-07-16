// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using static Nethermind.Evm.VirtualMachine;
using static System.Runtime.CompilerServices.Unsafe;

namespace Nethermind.Evm;
using Int256;

internal static partial class EvmInstructions
{
    /// <summary>
    /// Interface for two-parameter mathematical operations on 256-bit unsigned integers.
    /// Implementers define a specific binary math operation (e.g. addition, subtraction).
    /// </summary>
    public interface IOpMath2Param
    {
        /// <summary>
        /// The gas cost for executing this math operation.
        /// </summary>
        virtual static long GasCost => GasCostOf.VeryLow;
        /// <summary>
        /// Executes the math operation on two 256-bit operands.
        /// </summary>
        /// <param name="a">The first operand.</param>
        /// <param name="b">The second operand.</param>
        /// <param name="result">The result of the operation.</param>
        abstract static void Operation(in UInt256 a, in UInt256 b, out UInt256 result);
    }

    /// <summary>
    /// Executes a two-parameter mathematical operation.
    /// This method pops two UInt256 operands from the stack, applies the operation,
    /// and then pushes the result onto the stack.
    /// </summary>
    /// <typeparam name="TOpMath">A struct implementing <see cref="IOpMath2Param"/> that defines the specific operation.</typeparam>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gasAvailable">The available gas which is reduced by the operation's cost.</param>
    /// <param name="programCounter">Reference to the program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully;
    /// otherwise, <see cref="EvmExceptionType.StackUnderflow"/> if insufficient stack elements are available.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionMath2Param<TOpMath, TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TOpMath : struct, IOpMath2Param
        where TTracingInst : struct, IFlag
    {
        // Deduct the gas cost for the specific math operation.
        gasAvailable -= TOpMath.GasCost;

        // Pop two operands from the stack. If either pop fails, jump to the underflow handler.
        if (!stack.PopUInt256(out UInt256 a) || !stack.PopUInt256(out UInt256 b)) goto StackUnderflow;

        // Execute the math operation defined by TOpMath.
        TOpMath.Operation(in a, in b, out UInt256 result);

        // Push the computed result onto the stack.
        stack.PushUInt256<TTracingInst>(in result);

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    /// <summary>
    /// Implements addition of two 256-bit unsigned integers.
    /// </summary>
    public struct OpAdd : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
            => UInt256.Add(in a, in b, out result);
    }

    /// <summary>
    /// Implements subtraction of two 256-bit unsigned integers.
    /// </summary>
    public struct OpSub : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
            => UInt256.Subtract(in a, in b, out result);
    }

    /// <summary>
    /// Implements multiplication of two 256-bit unsigned integers.
    /// Uses a higher gas cost due to the increased computational complexity.
    /// </summary>
    public struct OpMul : IOpMath2Param
    {
        public static long GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
            => UInt256.Multiply(in a, in b, out result);
    }

    /// <summary>
    /// Implements division of two 256-bit unsigned integers.
    /// If the divisor is zero, returns zero per EVM semantics.
    /// </summary>
    public struct OpDiv : IOpMath2Param
    {
        public static long GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            if (b.IsZero)
            {
                // Division by zero yields a result of zero.
                result = default;
            }
            else
            {
                UInt256.Divide(in a, in b, out result);
            }
        }
    }

    /// <summary>
    /// Implements signed division of two 256-bit integers.
    /// Special cases:
    /// - Division by zero yields zero.
    /// - When dividing the minimum negative value by -1, returns the minimum negative value (to avoid overflow).
    /// Otherwise, performs a signed division.
    /// </summary>
    public struct OpSDiv : IOpMath2Param
    {
        public static long GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            if (b.IsZero)
            {
                // Division by zero: result is zero.
                result = default;
            }
            else if (As<UInt256, Int256>(ref AsRef(in b)) == Int256.MinusOne && a == P255)
            {
                // Special overflow case: when a equals P255 (a specific constant) and divisor is -1.
                result = P255;
            }
            else
            {
                // Prepare uninitialized result, so doesn't complain when passed by ref in As call.
                SkipInit(out result);
                // Convert operands to signed integers and perform division.
                Int256.Divide(
                    in As<UInt256, Int256>(ref AsRef(in a)),
                    in As<UInt256, Int256>(ref AsRef(in b)),
                    out As<UInt256, Int256>(ref result));
            }
        }
    }

    /// <summary>
    /// Implements the modulo operation for 256-bit unsigned integers.
    /// </summary>
    public struct OpMod : IOpMath2Param
    {
        public static long GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
            => UInt256.Mod(in a, in b, out result);
    }

    /// <summary>
    /// Implements the signed modulo operation.
    /// If the divisor is zero or one, the result is defined as zero.
    /// Otherwise, performs the modulo operation on the signed representations.
    /// </summary>
    public struct OpSMod : IOpMath2Param
    {
        public static long GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            if (b.IsZeroOrOne)
            {
                // Modulo with 0 or 1 yields zero.
                result = default;
            }
            else
            {
                // Prepare uninitialized result, so doesn't complain when passed by ref in As call.
                SkipInit(out result);
                // Convert operands to signed integers and perform the modulo operation.
                As<UInt256, Int256>(ref AsRef(in a))
                    .Mod(
                        in As<UInt256, Int256>(ref AsRef(in b)),
                        out As<UInt256, Int256>(ref result));
            }
        }
    }

    /// <summary>
    /// Implements the less-than comparison.
    /// Returns 1 if the first operand is less than the second; otherwise, returns 0.
    /// </summary>
    public struct OpLt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            result = a < b ? UInt256.One : default;
        }
    }

    /// <summary>
    /// Implements the greater-than comparison.
    /// Returns 1 if the first operand is greater than the second; otherwise, returns 0.
    /// </summary>
    public struct OpGt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            result = a > b ? UInt256.One : default;
        }
    }

    /// <summary>
    /// Implements the signed less-than comparison.
    /// Converts unsigned operands to signed representations and returns 1 if the first is less than the second.
    /// </summary>
    public struct OpSLt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            result = As<UInt256, Int256>(ref AsRef(in a))
                .CompareTo(As<UInt256, Int256>(ref AsRef(in b))) < 0 ?
                UInt256.One :
                default;
        }
    }

    /// <summary>
    /// Implements the signed greater-than comparison.
    /// Converts unsigned operands to signed representations and returns 1 if the first is greater than the second.
    /// </summary>
    public struct OpSGt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            result = As<UInt256, Int256>(ref AsRef(in a))
                .CompareTo(As<UInt256, Int256>(ref AsRef(in b))) > 0 ?
                UInt256.One :
                default;
        }
    }

    /// <summary>
    /// Implements the EXP opcode to perform exponentiation.
    /// The operation deducts gas based on the size of the exponent and computes the result.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack where the program counter is pushed.</param>
    /// <param name="gasAvailable">Reference to the remaining gas; reduced by the gas cost.</param>
    /// <param name="programCounter">The current program counter.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; or <see cref="EvmExceptionType.StackUnderflow"/> if not enough items on stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExp<TTracingInst>(VirtualMachine vm, ref EvmStack stack, ref long gasAvailable, ref int programCounter)
        where TTracingInst : struct, IFlag
    {
        // Charge the fixed gas cost for exponentiation.
        gasAvailable -= GasCostOf.Exp;

        // Pop the base value from the stack.
        if (!stack.PopUInt256(out UInt256 a))
            goto StackUnderflow;

        // Pop the exponent as a 256-bit word.
        Span<byte> bytes = stack.PopWord256();

        // Determine the effective byte-length of the exponent.
        int leadingZeros = bytes.LeadingZerosCount();
        if (leadingZeros == 32)
        {
            // Exponent is zero, so the result is 1.
            stack.PushOne<TTracingInst>();
        }
        else
        {
            int expSize = 32 - leadingZeros;
            // Deduct gas proportional to the number of 32-byte words needed to represent the exponent.
            gasAvailable -= vm.Spec.GetExpByteCost() * expSize;

            if (a.IsZero)
            {
                stack.PushZero<TTracingInst>();
            }
            else if (a.IsOne)
            {
                stack.PushOne<TTracingInst>();
            }
            else
            {
                // Perform exponentiation and push the 256-bit result onto the stack.
                UInt256.Exp(a, new UInt256(bytes, true), out UInt256 result);
                stack.PushUInt256<TTracingInst>(in result);
            }
        }

        return EvmExceptionType.None;
    // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
