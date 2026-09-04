// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Evm.GasPolicy;
using static System.Runtime.CompilerServices.Unsafe;
using static Nethermind.Evm.VirtualMachineStatics;

namespace Nethermind.Evm;

using Int256;

public static partial class EvmInstructions
{
    /// <summary>
    /// Interface for two-parameter mathematical operations on 256-bit unsigned integers.
    /// Implementers define a specific binary math operation (e.g. addition, subtraction).
    /// </summary>
    public interface IOpMath2Param : IGasCost
    {
        /// <summary>
        /// The gas cost for executing this math operation.
        /// </summary>
        static ulong IGasCost.GasCost => GasCostOf.VeryLow;
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
    /// <typeparam name="TGasPolicy">The gas policy used for gas accounting.</typeparam>
    /// <typeparam name="TOpMath">A struct implementing <see cref="IOpMath2Param"/> that defines the specific operation.</typeparam>
    /// <param name="stack">The execution stack.</param>
    /// <param name="gas">The gas state which is updated by the operation's cost.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> if the operation completes successfully;
    /// otherwise, <see cref="EvmExceptionType.StackUnderflow"/> if insufficient stack elements are available.
    /// </returns>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static EvmExceptionType InstructionMath2Param<TGasPolicy, TOpMath, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TOpMath : struct, IOpMath2Param
        where TTracingInst : struct, IFlag
    {
        // Deduct the gas cost for the specific math operation.
        TGasPolicy.Consume<TOpMath>(ref gas);

        return Math2ParamCore<TOpMath, TTracingInst>(ref stack);
    }

    /// <summary>Gas-free body of <see cref="InstructionMath2Param{TGasPolicy, TOpMath, TTracingInst}"/>.</summary>
    [SkipLocalsInit]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static EvmExceptionType Math2ParamCore<TOpMath, TTracingInst>(ref EvmStack stack)
        where TOpMath : struct, IOpMath2Param
        where TTracingInst : struct, IFlag
    {
        if (!Vector128.IsHardwareAccelerated && typeof(TOpMath) == typeof(OpAdd))
        {
            ref byte addTopRef = ref stack.Pop1Peek32Bytes(out bool isValid);
            if (!isValid) goto StackUnderflow;

            ref ulong top = ref As<byte, ulong>(ref addTopRef);
            ref ulong popped = ref Add(ref top, EvmStack.WordSize / sizeof(ulong));
            System.UInt128 sum = (System.UInt128)BinaryPrimitives.ReverseEndianness(Add(ref top, 3)) +
                BinaryPrimitives.ReverseEndianness(Add(ref popped, 3));
            Add(ref top, 3) = BinaryPrimitives.ReverseEndianness((ulong)sum);
            sum = (sum >> 64) + BinaryPrimitives.ReverseEndianness(Add(ref top, 2)) +
                BinaryPrimitives.ReverseEndianness(Add(ref popped, 2));
            Add(ref top, 2) = BinaryPrimitives.ReverseEndianness((ulong)sum);
            sum = (sum >> 64) + BinaryPrimitives.ReverseEndianness(Add(ref top, 1)) +
                BinaryPrimitives.ReverseEndianness(Add(ref popped, 1));
            Add(ref top, 1) = BinaryPrimitives.ReverseEndianness((ulong)sum);
            sum = (sum >> 64) + BinaryPrimitives.ReverseEndianness(top) +
                BinaryPrimitives.ReverseEndianness(popped);
            top = BinaryPrimitives.ReverseEndianness((ulong)sum);

            if (TTracingInst.IsActive) stack.ReportPushWord(ref addTopRef);
            return EvmExceptionType.None;
        }

        if (!Vector128.IsHardwareAccelerated &&
            (typeof(TOpMath) == typeof(OpLt) ||
             typeof(TOpMath) == typeof(OpGt) ||
             typeof(TOpMath) == typeof(OpSLt) ||
             typeof(TOpMath) == typeof(OpSGt)))
        {
            ref byte rawTopRef = ref stack.Pop1Peek32Bytes(out bool isValid);
            if (!isValid) goto StackUnderflow;

            ref ulong resultParts = ref As<byte, ulong>(ref rawTopRef);
            bool comparison = CompareScalar<TOpMath>(
                ref Add(ref resultParts, EvmStack.WordSize / sizeof(ulong)), ref resultParts);
            resultParts = 0;
            Add(ref resultParts, 1) = 0;
            Add(ref resultParts, 2) = 0;
            Add(ref resultParts, 3) = BinaryPrimitives.ReverseEndianness(comparison ? 1UL : 0UL);

            if (TTracingInst.IsActive) stack.ReportPushWord(ref rawTopRef);
            return EvmExceptionType.None;
        }

        // Pop a and peek the new top slot for in-place write; skips the push's overflow check
        // since the net stack delta (-1) cannot overflow a previously non-overflowing stack.
        ref byte topRef = ref stack.Pop1Peek32Bytes(out UInt256 a, out bool ok);
        if (!ok) goto StackUnderflow;

        EvmStack.ReadUInt256FromSlot(ref topRef, out UInt256 b);
        TOpMath.Operation(in a, in b, out UInt256 result);
        EvmStack.WriteUInt256ToSlot(ref topRef, in result);

        if (TTracingInst.IsActive) stack.ReportPushWord(ref topRef);
        return EvmExceptionType.None;
        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CompareScalar<TOpMath>(ref ulong a, ref ulong b)
        where TOpMath : struct, IOpMath2Param
    {
        bool signed = typeof(TOpMath) == typeof(OpSLt) || typeof(TOpMath) == typeof(OpSGt);
        bool lessThan = typeof(TOpMath) == typeof(OpLt) || typeof(TOpMath) == typeof(OpSLt);

        ulong aPart = BinaryPrimitives.ReverseEndianness(a);
        ulong bPart = BinaryPrimitives.ReverseEndianness(b);
        if (aPart != bPart)
        {
            bool less = signed ? (long)aPart < (long)bPart : aPart < bPart;
            return lessThan ? less : !less;
        }

        aPart = BinaryPrimitives.ReverseEndianness(Add(ref a, 1));
        bPart = BinaryPrimitives.ReverseEndianness(Add(ref b, 1));
        if (aPart != bPart)
            return lessThan ? aPart < bPart : aPart > bPart;

        aPart = BinaryPrimitives.ReverseEndianness(Add(ref a, 2));
        bPart = BinaryPrimitives.ReverseEndianness(Add(ref b, 2));
        if (aPart != bPart)
            return lessThan ? aPart < bPart : aPart > bPart;

        aPart = BinaryPrimitives.ReverseEndianness(Add(ref a, 3));
        bPart = BinaryPrimitives.ReverseEndianness(Add(ref b, 3));
        return lessThan ? aPart < bPart : aPart > bPart;
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
        static ulong IGasCost.GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
            => UInt256.Multiply(in a, in b, out result);
    }

    /// <summary>
    /// Implements division of two 256-bit unsigned integers.
    /// If the divisor is zero, returns zero per EVM semantics.
    /// </summary>
    public struct OpDiv : IOpMath2Param
    {
        static ulong IGasCost.GasCost => GasCostOf.Low;
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
        static ulong IGasCost.GasCost => GasCostOf.Low;
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
        static ulong IGasCost.GasCost => GasCostOf.Low;
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result)
        {
            if (b.IsZeroOrOne)
            {
                // Modulo with 0 or 1 yields zero.
                result = default;
            }
            else
            {
                UInt256.Mod(in a, in b, out result);
            }
        }
    }

    /// <summary>
    /// Implements the signed modulo operation.
    /// If the divisor is zero or one, the result is defined as zero.
    /// Otherwise, performs the modulo operation on the signed representations.
    /// </summary>
    public struct OpSMod : IOpMath2Param
    {
        static ulong IGasCost.GasCost => GasCostOf.Low;
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
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => result = a < b ? UInt256.One : default;
    }

    /// <summary>
    /// Implements the greater-than comparison.
    /// Returns 1 if the first operand is greater than the second; otherwise, returns 0.
    /// </summary>
    public struct OpGt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => result = a > b ? UInt256.One : default;
    }

    /// <summary>
    /// Implements the signed less-than comparison.
    /// Converts unsigned operands to signed representations and returns 1 if the first is less than the second.
    /// </summary>
    public struct OpSLt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => result = As<UInt256, Int256>(ref AsRef(in a))
                .CompareTo(As<UInt256, Int256>(ref AsRef(in b))) < 0 ?
                UInt256.One :
                default;
    }

    /// <summary>
    /// Implements the signed greater-than comparison.
    /// Converts unsigned operands to signed representations and returns 1 if the first is greater than the second.
    /// </summary>
    public struct OpSGt : IOpMath2Param
    {
        public static void Operation(in UInt256 a, in UInt256 b, out UInt256 result) => result = As<UInt256, Int256>(ref AsRef(in a))
                .CompareTo(As<UInt256, Int256>(ref AsRef(in b))) > 0 ?
                UInt256.One :
                default;
    }

    /// <summary>
    /// Implements the EXP opcode to perform exponentiation.
    /// The operation deducts gas based on the size of the exponent and computes the result.
    /// </summary>
    /// <param name="vm">The virtual machine instance.</param>
    /// <param name="stack">The execution stack where the program counter is pushed.</param>
    /// <param name="gas">Reference to the gas state; updated by the gas cost.</param>
    /// <returns>
    /// <see cref="EvmExceptionType.None"/> on success; or <see cref="EvmExceptionType.StackUnderflow"/> if not enough items on stack.
    /// </returns>
    [SkipLocalsInit]
    public static EvmExceptionType InstructionExp<TGasPolicy, TTracingInst>(ref EvmStack stack, ref TGasPolicy gas, VirtualMachine<TGasPolicy> vm)
        where TGasPolicy : struct, IGasPolicy<TGasPolicy>
        where TTracingInst : struct, IFlag
    {
        // Charge the fixed gas cost for exponentiation.
        TGasPolicy.Consume<ExpGasCost>(ref gas);

        // Pop the base value and exponent from the stack.
        if (!stack.PopUInt256(out UInt256 a, out UInt256 exponent))
        {
            goto StackUnderflow;
        }

        // Determine the effective byte-length of the exponent.
        int leadingZeros = exponent.CountLeadingZeros() >> 3;
        if (leadingZeros == 32)
        {
            // Exponent is zero, so the result is 1.
            return stack.PushOne<TTracingInst>();
        }

        ulong expSize = (ulong)(32 - leadingZeros);
        // Deduct gas proportional to the number of 32-byte words needed to represent the exponent.
        TGasPolicy.ConsumeExpBytes(ref gas, vm.Spec, expSize);

        if (a.IsZero)
        {
            return stack.PushZero<TTracingInst>();
        }
        if (a.IsOne)
        {
            return stack.PushOne<TTracingInst>();
        }

        // Perform exponentiation and push the 256-bit result onto the stack.
        UInt256.Exp(in a, in exponent, out UInt256 expResult);
        return stack.PushUInt256<TTracingInst>(in expResult);
        // Jump forward to be unpredicted by the branch predictor.
    StackUnderflow:
        return EvmExceptionType.StackUnderflow;
    }
}
