// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using Nethermind.Core.Extensions;
using Nethermind.Evm;
using Nethermind.Evm.GasPolicy;
using Nethermind.Int256;

namespace Nethermind.IlEvm;

public static unsafe partial class IlEvm
{
    // Constant-operand forms: the constant arrives as four literal limbs, so the JIT folds it into the operation.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlMath2Const<TOpMath>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param
    {
        // Built as one vector: four limb stores read back whole could not be forwarded.
        UInt256 constant = Unsafe.BitCast<Vector256<ulong>, UInt256>(Vector256.Create(c0, c1, c2, c3));
        ref UInt256 value = ref Unsafe.As<byte, UInt256>(ref operand);
        UInt256 result;
        if (constantOnTop)
            TOpMath.Operation(in constant, in value, out result);
        else
            TOpMath.Operation(in value, in constant, out result);
        // One 32-byte store, which the next opcode's whole-word read is forwarded from.
        Unsafe.WriteUnaligned(ref destination, Vector256.Create(result.u0, result.u1, result.u2, result.u3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlBitwiseConst<TOp>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise
    {
        EvmWord constant = Vector256.Create(c0, c1, c2, c3).AsByte();
        ref EvmWord value = ref Unsafe.As<byte, EvmWord>(ref operand);
        Unsafe.As<byte, EvmWord>(ref destination) = constantOnTop ? TOp.Operation(in constant, in value) : TOp.Operation(in value, in constant);
    }

    // Comparisons answered as a flag, through the opcode's own operation, for a JUMPI or ISZERO that consumes them.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlPredicate2<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param
    {
        ref UInt256 a = ref Unsafe.As<byte, UInt256>(ref top);
        TOpMath.Operation(in a, in Unsafe.Subtract(ref a, 1), out UInt256 result);
        return !result.IsZero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlPredicate2Const<TOpMath>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param
    {
        UInt256 constant = Unsafe.BitCast<Vector256<ulong>, UInt256>(Vector256.Create(c0, c1, c2, c3));
        ref UInt256 value = ref Unsafe.As<byte, UInt256>(ref operand);
        UInt256 result;
        if (constantOnTop)
            TOpMath.Operation(in constant, in value, out result);
        else
            TOpMath.Operation(in value, in constant, out result);
        return !result.IsZero;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlPredicateBitwise<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise
    {
        ref EvmWord a = ref Unsafe.As<byte, EvmWord>(ref top);
        return TOp.Operation(in a, in Unsafe.Subtract(ref a, 1)) != default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlPredicateBitwiseConst<TOp>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise
    {
        EvmWord constant = Vector256.Create(c0, c1, c2, c3).AsByte();
        ref EvmWord value = ref Unsafe.As<byte, EvmWord>(ref operand);
        return (constantOnTop ? TOp.Operation(in constant, in value) : TOp.Operation(in value, in constant)) != default;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlPredicate1<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param =>
        TOp.Operation(Unsafe.As<byte, EvmWord>(ref top)) != default;

    /// <summary>MLOAD at a constant offset below 2^32 of a word inside the active, initialized memory; false for anything else.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlTryMLoadAt(ref EvmPooledMemory memory, ulong offset, ref byte destination)
    {
        ref byte source = ref GetActiveInitializedWord(ref memory, offset);
        if (Unsafe.IsNullRef(ref source))
            return false;

        Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<EvmWord>(ref source).ByteSwap());
        return true;
    }

    /// <summary>MSTORE at a constant offset below 2^32 of the word in <paramref name="value"/>; <see cref="ulong.MaxValue"/> where it needs the body.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong IlTryMStoreAt(ref EvmPooledMemory memory, ulong offset, ref byte value, ulong gas)
    {
        ulong expansion = GetWordOverwriteCost(ref memory, offset);
        if (expansion > gas)
            return ulong.MaxValue;

        ref byte destination = ref CommitWordOverwrite(ref memory, offset);
        Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<EvmWord>(ref value).ByteSwap());
        return gas - expansion;
    }

    /// <summary>MSTORE of a constant word, given in memory byte order, at a constant offset below 2^32.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong IlTryMStoreWordAt(ref EvmPooledMemory memory, ulong offset, ulong m0, ulong m1, ulong m2, ulong m3, ulong gas)
    {
        ulong expansion = GetWordOverwriteCost(ref memory, offset);
        if (expansion > gas)
            return ulong.MaxValue;

        ref byte destination = ref CommitWordOverwrite(ref memory, offset);
        Unsafe.WriteUnaligned(ref destination, Vector256.Create(m0, m1, m2, m3));
        return gas - expansion;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlMath2<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param
    {
        ref UInt256 a = ref Unsafe.As<byte, UInt256>(ref top);
        ref UInt256 b = ref Unsafe.Subtract(ref a, 1);
        TOpMath.Operation(in a, in b, out UInt256 result);
        Unsafe.WriteUnaligned(ref Unsafe.As<UInt256, byte>(ref b), Vector256.Create(result.u0, result.u1, result.u2, result.u3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlMath2InPlace<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param
    {
        ref UInt256 a = ref Unsafe.As<byte, UInt256>(ref top);
        ref UInt256 b = ref Unsafe.Subtract(ref a, 1);
        TOpMath.Operation(in a, in b, out b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlBitwise<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise
    {
        ref EvmWord a = ref Unsafe.As<byte, EvmWord>(ref top);
        ref EvmWord b = ref Unsafe.Subtract(ref a, 1);
        b = TOp.Operation(in a, in b);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlMath1<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param
    {
        ref EvmWord value = ref Unsafe.As<byte, EvmWord>(ref top);
        value = TOp.Operation(value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref EvmPooledMemory IlMemoryOf(VirtualMachine<EthereumGasPolicy> vm) => ref vm.VmState.Memory;

    /// <summary>CLZ as a one-word operation, so it compiles as NOT does; the core runs it through a body of its own.</summary>
    internal struct IlOpClz : EvmInstructions.IOpMath1Param
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static EvmWord Operation(EvmWord value)
        {
            Vector256<ulong> limbs = value.AsUInt64();
            ulong u3 = limbs.GetElement(3), u2 = limbs.GetElement(2), u1 = limbs.GetElement(1), u0 = limbs.GetElement(0);
            // A zero word counts 192 + 64.
            int count = u3 != 0 ? System.Numerics.BitOperations.LeadingZeroCount(u3)
                : u2 != 0 ? 64 + System.Numerics.BitOperations.LeadingZeroCount(u2)
                : u1 != 0 ? 128 + System.Numerics.BitOperations.LeadingZeroCount(u1)
                : 192 + System.Numerics.BitOperations.LeadingZeroCount(u0);
            return Vector256.CreateScalar((ulong)count).AsByte();
        }
    }

    /// <summary>CALLDATALOAD of an offset held in a register, past the end of the input or not below 2^32: zeros where the input ends.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector256<ulong> IlCallDataWord(ref byte input, nint length, Vector256<ulong> offset) => IlCallDataWordAt(ref input, length, offset);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static Vector256<ulong> IlCallDataWordAhead(ref byte input, nint length, Vector256<ulong> offset) => IlCallDataWordAt(ref input, length, offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> IlCallDataWordAt(ref byte input, nint length, Vector256<ulong> offset)
    {
        // Past the input, or past 2^64, the body reads nothing and pushes zero.
        ulong at = offset.GetElement(0);
        if ((offset.GetElement(1) | offset.GetElement(2) | offset.GetElement(3)) != 0 || at >= (ulong)length)
            return Vector256<ulong>.Zero;
        // Not through IlCallDataLoadAt: the .NET 10 JIT emits its InitBlockUnaligned of a word in a vector local as a VEX.256 vmovq, which faults.
        uint available = (uint)length - (uint)at;
        ref byte source = ref Unsafe.Add(ref input, (nint)at);
        EvmWord word;
        if (available >= EvmStack.WordSize)
        {
            word = Unsafe.ReadUnaligned<EvmWord>(ref source);
        }
        else
        {
            word = default;
            Unsafe.CopyBlockUnaligned(ref Unsafe.As<EvmWord, byte>(ref word), ref source, available);
        }

        return word.ByteSwap().AsUInt64();
    }

    // The memory's word fast paths, experimental public members of the core's memory; the offsets passed are below 2^32.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte GetActiveInitializedWord(ref EvmPooledMemory memory, ulong offset) => ref memory.GetActiveInitializedWord(offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong GetWordOverwriteCost(ref EvmPooledMemory memory, ulong offset) => memory.GetWordOverwriteCost(offset);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref byte CommitWordOverwrite(ref EvmPooledMemory memory, ulong offset) => ref memory.CommitWordOverwrite(offset);

    /// <summary>Carries one into limb 1 and up of the word at <paramref name="slot"/>, for an addition whose low limb wrapped.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlCarry(ref byte slot) => IlCarryWord(ref slot);

    /// <summary>Borrows one from limb 1 and up of the word at <paramref name="slot"/>, for a subtraction whose low limb wrapped.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlBorrow(ref byte slot) => IlBorrowWord(ref slot);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector256<ulong> IlCarryVector(Vector256<ulong> word) => IlCarryLimbs(word);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Vector256<ulong> IlBorrowVector(Vector256<ulong> word) => IlBorrowLimbs(word);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static Vector256<ulong> IlCarryVectorAhead(Vector256<ulong> word) => IlCarryLimbs(word);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static Vector256<ulong> IlBorrowVectorAhead(Vector256<ulong> word) => IlBorrowLimbs(word);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> IlCarryLimbs(Vector256<ulong> word)
    {
        ulong u1 = word.GetElement(1) + 1, u2 = word.GetElement(2), u3 = word.GetElement(3);
        if (u1 == 0 && ++u2 == 0)
            u3++;
        return Vector256.Create(word.GetElement(0), u1, u2, u3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<ulong> IlBorrowLimbs(Vector256<ulong> word)
    {
        ulong u1 = word.GetElement(1), u2 = word.GetElement(2), u3 = word.GetElement(3);
        if (u1-- == 0 && u2-- == 0)
            u3--;
        return Vector256.Create(word.GetElement(0), u1, u2, u3);
    }

    /// <summary>The carry, reading and writing the word whole, as compiled code wrote it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlCarryWord(ref byte slot)
    {
        Vector256<ulong> word = Unsafe.ReadUnaligned<Vector256<ulong>>(ref slot);
        ulong u1 = word.GetElement(1) + 1, u2 = word.GetElement(2), u3 = word.GetElement(3);
        if (u1 == 0 && ++u2 == 0)
            u3++;
        Unsafe.WriteUnaligned(ref slot, Vector256.Create(word.GetElement(0), u1, u2, u3));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlBorrowWord(ref byte slot)
    {
        Vector256<ulong> word = Unsafe.ReadUnaligned<Vector256<ulong>>(ref slot);
        ulong u1 = word.GetElement(1), u2 = word.GetElement(2), u3 = word.GetElement(3);
        if (u1-- == 0 && u2-- == 0)
            u3--;
        Unsafe.WriteUnaligned(ref slot, Vector256.Create(word.GetElement(0), u1, u2, u3));
    }

    // Called twins: what compiled code calls when the helpers above are not inlined, trading a call for much smaller code; found by the "Called" suffix.

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlMath2Called<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlMath2<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlMath2InPlaceCalled<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlMath2InPlace<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlMath2ConstCalled<TOpMath>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param =>
        IlMath2Const<TOpMath>(ref operand, ref destination, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlBitwiseCalled<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise => IlBitwise<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlBitwiseConstCalled<TOp>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise =>
        IlBitwiseConst<TOp>(ref operand, ref destination, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlMath1Called<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param => IlMath1<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlPredicate2Called<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlPredicate2<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlPredicate2ConstCalled<TOpMath>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param =>
        IlPredicate2Const<TOpMath>(ref operand, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlPredicateBitwiseCalled<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise => IlPredicateBitwise<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlPredicateBitwiseConstCalled<TOp>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise =>
        IlPredicateBitwiseConst<TOp>(ref operand, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlPredicate1Called<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param => IlPredicate1<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlTryMLoadCalled(ref EvmPooledMemory memory, ref byte slot) => IlTryMLoad(ref memory, ref slot);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong IlTryMStoreCalled(ref EvmPooledMemory memory, ref byte offsetSlot, ulong gas) => IlTryMStore(ref memory, ref offsetSlot, gas);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IlTryMLoadAtCalled(ref EvmPooledMemory memory, ulong offset, ref byte destination) => IlTryMLoadAt(ref memory, offset, ref destination);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong IlTryMStoreAtCalled(ref EvmPooledMemory memory, ulong offset, ref byte value, ulong gas) => IlTryMStoreAt(ref memory, offset, ref value, gas);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static ulong IlTryMStoreWordAtCalled(ref EvmPooledMemory memory, ulong offset, ulong m0, ulong m1, ulong m2, ulong m3, ulong gas) =>
        IlTryMStoreWordAt(ref memory, offset, m0, m1, m2, m3, gas);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void IlCallDataLoadAtCalled(ref byte input, nint length, ulong offset, ref byte destination) => IlCallDataLoadAt(ref input, length, offset, ref destination);

    // Ahead twins: the same code optimized from its first call, for an ahead copy (IlContract.Ahead) while the called twins run tier-0 code; unprofiled, they lose once those tier up (IlWarmUp.SwitchFromAhead).

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlCarryAhead(ref byte slot) => IlCarryWord(ref slot);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlBorrowAhead(ref byte slot) => IlBorrowWord(ref slot);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlMath2CalledAhead<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlMath2<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlMath2InPlaceCalledAhead<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlMath2InPlace<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlMath2ConstCalledAhead<TOpMath>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param =>
        IlMath2Const<TOpMath>(ref operand, ref destination, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlBitwiseCalledAhead<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise => IlBitwise<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlBitwiseConstCalledAhead<TOp>(ref byte operand, ref byte destination, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise =>
        IlBitwiseConst<TOp>(ref operand, ref destination, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlMath1CalledAhead<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param => IlMath1<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlPredicate2CalledAhead<TOpMath>(ref byte top) where TOpMath : struct, EvmInstructions.IOpMath2Param => IlPredicate2<TOpMath>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlPredicate2ConstCalledAhead<TOpMath>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOpMath : struct, EvmInstructions.IOpMath2Param =>
        IlPredicate2Const<TOpMath>(ref operand, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlPredicateBitwiseCalledAhead<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpBitwise => IlPredicateBitwise<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlPredicateBitwiseConstCalledAhead<TOp>(ref byte operand, ulong c0, ulong c1, ulong c2, ulong c3, bool constantOnTop)
        where TOp : struct, EvmInstructions.IOpBitwise =>
        IlPredicateBitwiseConst<TOp>(ref operand, c0, c1, c2, c3, constantOnTop);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlPredicate1CalledAhead<TOp>(ref byte top) where TOp : struct, EvmInstructions.IOpMath1Param => IlPredicate1<TOp>(ref top);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlTryMLoadCalledAhead(ref EvmPooledMemory memory, ref byte slot) => IlTryMLoad(ref memory, ref slot);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong IlTryMStoreCalledAhead(ref EvmPooledMemory memory, ref byte offsetSlot, ulong gas) => IlTryMStore(ref memory, ref offsetSlot, gas);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool IlTryMLoadAtCalledAhead(ref EvmPooledMemory memory, ulong offset, ref byte destination) => IlTryMLoadAt(ref memory, offset, ref destination);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong IlTryMStoreAtCalledAhead(ref EvmPooledMemory memory, ulong offset, ref byte value, ulong gas) => IlTryMStoreAt(ref memory, offset, ref value, gas);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static ulong IlTryMStoreWordAtCalledAhead(ref EvmPooledMemory memory, ulong offset, ulong m0, ulong m1, ulong m2, ulong m3, ulong gas) =>
        IlTryMStoreWordAt(ref memory, offset, m0, m1, m2, m3, gas);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static void IlCallDataLoadAtCalledAhead(ref byte input, nint length, ulong offset, ref byte destination) => IlCallDataLoadAt(ref input, length, offset, ref destination);

    private static readonly ConcurrentDictionary<MethodInfo, MethodInfo> _ilCalledTwins = new(), _ilAheadTwins = new();

    /// <summary>Whether the code being laid out on this thread is a contract's ahead copy, whose calls go to the ahead twins (<see cref="PrecompileAhead"/>).</summary>
    [ThreadStatic] private static bool _layingOutAhead;

    /// <summary>The called twin of an inlined helper, found by name; its ahead twin while an ahead copy is laid out.</summary>
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Opcode bodies are rooted by the dispatch table; the compiler is off without dynamic code.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The twins are private static methods of this type, referenced here.")]
    private static MethodInfo IlCalledTwin(MethodInfo helper) => _layingOutAhead
        ? _ilAheadTwins.GetOrAdd(helper, static h => IlTwin(h, "CalledAhead"))
        : _ilCalledTwins.GetOrAdd(helper, static h => IlTwin(h, "Called"));

    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "IlEvmSettings.Enabled is false without dynamic code, so nothing reaches the compiler.")]
    [UnconditionalSuppressMessage("Trimming", "IL2060", Justification = "Opcode bodies are rooted by the dispatch table; the compiler is off without dynamic code.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The twins are private static methods of this type, referenced here.")]
    private static MethodInfo IlTwin(MethodInfo helper, string suffix)
    {
        MethodInfo twin = typeof(IlEvm).GetMethod(helper.Name + suffix, BindingFlags.NonPublic | BindingFlags.Static)!;
        return helper.IsGenericMethod ? twin.MakeGenericMethod(helper.GetGenericArguments()) : twin;
    }

    /// <summary>MLOAD of a word inside the active, initialized memory, which needs no gas beyond the opcode's; false for anything else.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IlTryMLoad(ref EvmPooledMemory memory, ref byte slot)
    {
        ref ulong offset = ref Unsafe.As<byte, ulong>(ref slot);
        if ((Unsafe.Add(ref offset, 1) | Unsafe.Add(ref offset, 2) | Unsafe.Add(ref offset, 3) | (offset >> 32)) != 0)
            return false;

        ref byte source = ref GetActiveInitializedWord(ref memory, offset);
        if (Unsafe.IsNullRef(ref source))
            return false;

        // Memory holds the word big-endian; the slot takes it in limb layout, over the offset it held.
        Unsafe.WriteUnaligned(ref slot, Unsafe.ReadUnaligned<EvmWord>(ref source).ByteSwap());
        return true;
    }

    /// <summary>MSTORE of a word that needs no clearing and no new backing, paying its expansion out of <paramref name="gas"/>: the gas left, or <see cref="ulong.MaxValue"/> for anything else.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong IlTryMStore(ref EvmPooledMemory memory, ref byte offsetSlot, ulong gas)
    {
        ref ulong offset = ref Unsafe.As<byte, ulong>(ref offsetSlot);
        if ((Unsafe.Add(ref offset, 1) | Unsafe.Add(ref offset, 2) | Unsafe.Add(ref offset, 3) | (offset >> 32)) != 0)
            return ulong.MaxValue;

        ulong expansion = GetWordOverwriteCost(ref memory, offset);
        if (expansion > gas)
            return ulong.MaxValue;

        ref byte destination = ref CommitWordOverwrite(ref memory, offset);
        // The value sits below the offset in limb layout; memory holds it big-endian.
        Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<EvmWord>(ref Unsafe.Subtract(ref offsetSlot, EvmStack.WordSize)).ByteSwap());
        return gas - expansion;
    }

    /// <summary>Runs an embedded body for compiled code and finishes its segment's charge, or says how the frame leaves.</summary>
    /// <remarks>
    /// The body sees the interpreter's gas (the segment's charge for it and those after it comes back), and the rest is charged again
    /// after: too little means a fault the walk finds or, where it cannot tell, a hand-over past this opcode. A halt, or a counter moved elsewhere, ends the run.
    /// </remarks>
    /// <param name="opsAdjust">What the caller adds to its opcode count when the frame leaves.</param>
    /// <returns>None to go on; <see cref="IlDeopt"/> to hand over at <paramref name="programCounter"/> once virtual values are written out; else the frame's result. Gas, head and counter are then the frame's.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static EvmExceptionType IlEmbedded(IlChunk chunk, int site, VirtualMachine<EthereumGasPolicy> vm, ref EvmStack stack, ref EthereumGasPolicy gas, ref nint programCounter, out int opsAdjust, nint head, ulong gasNow) =>
        IlEmbeddedRun(chunk, site, vm, ref stack, ref gas, ref programCounter, out opsAdjust, head, gasNow);

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static EvmExceptionType IlEmbeddedAhead(IlChunk chunk, int site, VirtualMachine<EthereumGasPolicy> vm, ref EvmStack stack, ref EthereumGasPolicy gas, ref nint programCounter, out int opsAdjust, nint head, ulong gasNow) =>
        IlEmbeddedRun(chunk, site, vm, ref stack, ref gas, ref programCounter, out opsAdjust, head, gasNow);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static EvmExceptionType IlEmbeddedRun(IlChunk chunk, int site, VirtualMachine<EthereumGasPolicy> vm, ref EvmStack stack, ref EthereumGasPolicy gas, ref nint programCounter, out int opsAdjust, nint head, ulong gasNow)
    {
        ref readonly IlSite info = ref chunk.Sites[site];
        // The interpreter's head and gas before the opcode: the segment's charge for it and the ones after it comes back.
        stack.Head = head + info.HeadDelta;
        gas.Value = gasNow + info.GasBack;
        // Bodies see the counter past their opcode byte, as the dispatch loop passes it.
        nint pc = info.Pc + 1;
        EvmExceptionType result = ((delegate*<ref EvmStack, ref EthereumGasPolicy, VirtualMachine<EthereumGasPolicy>, ref nint, EvmExceptionType>)info.Execute)(ref stack, ref gas, vm, ref pc);
        if (result == EvmExceptionType.None && pc == info.NextPc)
        {
            ref ulong left = ref gas.Value;
            if (left >= info.GasAfter)
            {
                left -= info.GasAfter;
                opsAdjust = 0;
                return EvmExceptionType.None;
            }

            programCounter = info.NextPc;
            result = chunk.Contract.FailSegment(info.FirstAfter, info.CountedAfter, left, stack.Head, 0, out ulong gasLeft, out int walked);
            if (result == IlDeopt)
            {
                opsAdjust = -info.CountedAfter;
                return IlDeopt;
            }

            left = gasLeft;
            opsAdjust = walked - info.CountedAfter;
            return result;
        }

        programCounter = pc;
        opsAdjust = -info.CountedAfter;
        return result == EvmExceptionType.None ? IlDeopt : result;
    }

    /// <summary>The stack inputs and outputs of the opcodes that may run inside a segment through their bodies, none of which suspends, jumps or reads the program counter.</summary>
    private static (int Inputs, int Outputs)? IlEmbeddableStackEffect(Instruction instruction) => instruction switch
    {
        Instruction.EXP => (2, 1),
        Instruction.KECCAK256 => (2, 1),
        Instruction.BALANCE => (1, 1),
        Instruction.CALLDATACOPY => (3, 0),
        Instruction.CODECOPY => (3, 0),
        Instruction.EXTCODESIZE => (1, 1),
        Instruction.EXTCODECOPY => (4, 0),
        Instruction.RETURNDATACOPY => (3, 0),
        Instruction.EXTCODEHASH => (1, 1),
        Instruction.BLOCKHASH => (1, 1),
        Instruction.SELFBALANCE => (0, 1),
        Instruction.BLOBHASH => (1, 1),
        Instruction.SLOAD => (1, 1),
        Instruction.SSTORE => (2, 0),
        Instruction.TLOAD => (1, 1),
        Instruction.TSTORE => (2, 0),
        Instruction.MCOPY => (3, 0),
        >= Instruction.LOG0 and <= Instruction.LOG4 => (2 + (instruction - Instruction.LOG0), 0),
        _ => null,
    };

    /// <summary>CALLDATALOAD at a constant offset below 2^64, written straight into <paramref name="destination"/>, as the body writes it.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void IlCallDataLoadAt(ref byte input, nint length, ulong offset, ref byte destination)
    {
        if (offset >= (ulong)length)
        {
            Unsafe.InitBlockUnaligned(ref destination, 0, EvmStack.WordSize);
            return;
        }

        uint available = (uint)length - (uint)offset;
        ref byte source = ref Unsafe.Add(ref input, (nint)offset);
        if (available >= EvmStack.WordSize)
        {
            Unsafe.WriteUnaligned(ref destination, Unsafe.ReadUnaligned<EvmWord>(ref source).ByteSwap());
            return;
        }

        EvmWord word = default;
        Unsafe.CopyBlockUnaligned(ref Unsafe.As<EvmWord, byte>(ref word), ref source, available);
        Unsafe.WriteUnaligned(ref destination, word.ByteSwap());
    }
}
