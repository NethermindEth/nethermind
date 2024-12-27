// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.Evm.CodeAnalysis.IL.CompilerModes.PartialAOT;
using Nethermind.Evm.Tracing;
using Nethermind.Int256;
using Org.BouncyCastle.Tls;
using Sigil;
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static Nethermind.Evm.CodeAnalysis.IL.CompilerModes.PartialAOT.PartialAOT;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using Label = Sigil.Label;

namespace Nethermind.Evm.CodeAnalysis.IL;
public class Locals<T>(Emit<T> method) : IDisposable
{
    public Local jmpDestination = method.DeclareLocal(typeof(int));
    public Local address = method.DeclareLocal(typeof(Address));
    public Local hash256 = method.DeclareLocal(typeof(Hash256));
    public Local wordRef256A = method.DeclareLocal(typeof(Word).MakeByRefType());
    public Local wordRef256B = method.DeclareLocal(typeof(Word).MakeByRefType());
    public Local wordRef256C = method.DeclareLocal(typeof(Word).MakeByRefType());
    public Local uint256A = method.DeclareLocal(typeof(UInt256));
    public Local uint256B = method.DeclareLocal(typeof(UInt256));
    public Local uint256C = method.DeclareLocal(typeof(UInt256));
    public Local uint256R = method.DeclareLocal(typeof(UInt256));
    public Local localReadOnlyMemory = method.DeclareLocal(typeof(ReadOnlyMemory<byte>));
    public Local localReadonOnlySpan = method.DeclareLocal(typeof(ReadOnlySpan<byte>));
    public Local localZeroPaddedSpan = method.DeclareLocal(typeof(ZeroPaddedSpan));
    public Local localSpan = method.DeclareLocal(typeof(Span<byte>));
    public Local localMemory = method.DeclareLocal(typeof(Memory<byte>));
    public Local localArray = method.DeclareLocal(typeof(byte[]));
    public Local uint64A = method.DeclareLocal(typeof(ulong));
    public Local uint32A = method.DeclareLocal(typeof(uint));
    public Local uint32B = method.DeclareLocal(typeof(uint));
    public Local int64A = method.DeclareLocal(typeof(long));
    public Local int64B = method.DeclareLocal(typeof(long));
    public Local byte8A = method.DeclareLocal(typeof(byte));
    public Local lbool = method.DeclareLocal(typeof(bool));
    public Local byte8B = method.DeclareLocal(typeof(byte));
    public Local storageCell = method.DeclareLocal(typeof(StorageCell));
    public Local gasAvailable = method.DeclareLocal(typeof(long));
    public Local programCounter = method.DeclareLocal(typeof(int));
    public Local stackHeadRef = method.DeclareLocal(typeof(Word).MakeByRefType());
    public Local stackHeadIdx = method.DeclareLocal(typeof(int));
    public Local header = method.DeclareLocal(typeof(BlockHeader));

    public Dictionary<string, Local> AddtionalLocals = new();

    public bool TryDeclareLocal(string name, Type type)
    {
        if (!AddtionalLocals.ContainsKey(name))
        {
            AddtionalLocals.Add(name, method.DeclareLocal(type));
            return true;
        }
        return false;
    }

    public bool TryLoadLocal(string name)
    {
        if (AddtionalLocals.ContainsKey(name))
        {
            method.LoadLocal(AddtionalLocals[name]);
            return true;
        }
        return false;
    }

    public bool TryLoadLocalAddress(string name)
    {
        if (AddtionalLocals.ContainsKey(name))
        {
            method.LoadLocalAddress(AddtionalLocals[name]);
            return true;
        }
        return false;
    }

    public bool TryStoreLocal(string name)
    {
        if (AddtionalLocals.ContainsKey(name))
        {
            method.StoreLocal(AddtionalLocals[name]);
            return true;
        }
        return false;
    }

    public void Dispose()
    {
        jmpDestination.Dispose();
        address.Dispose();
        hash256.Dispose();
        wordRef256A.Dispose();
        wordRef256B.Dispose();
        wordRef256C.Dispose();
        uint256A.Dispose();
        uint256B.Dispose();
        uint256C.Dispose();
        uint256R.Dispose();
        localReadOnlyMemory.Dispose();
        localReadonOnlySpan.Dispose();
        localZeroPaddedSpan.Dispose();
        localSpan.Dispose();
        localMemory.Dispose();
        localArray.Dispose();
        uint64A.Dispose();
        uint32A.Dispose();
        uint32B.Dispose();
        int64A.Dispose();
        int64B.Dispose();
        byte8A.Dispose();
        lbool.Dispose();
        byte8B.Dispose();
        storageCell.Dispose();
        gasAvailable.Dispose();
        programCounter.Dispose();
        stackHeadRef.Dispose();
        stackHeadIdx.Dispose();
        header.Dispose();

        foreach (var local in AddtionalLocals)
        {
            local.Value.Dispose();
        }
    }

}
public static class TypeEmit
{
    public static string MangleName(string cleanName) => $"<{cleanName}>k__BackingField<ilevm>";
    public static FieldBuilder EmitField<TField>(this TypeBuilder typeBuilder, string fieldName, bool isPublic)
        where TField : allows ref struct
    {
        FieldBuilder fieldBuilder = typeBuilder.DefineField(fieldName, typeof(TField), isPublic ? FieldAttributes.Public : FieldAttributes.Private);
        return fieldBuilder;
    }

    public static PropertyBuilder EmitProperty<TProperty>(this TypeBuilder typeBuilder, string PropertyName, bool hasGetter, bool hasSetter, FieldBuilder? field = null)
        where TProperty : allows ref struct
    {
        if (field is null)
        {
            field = typeBuilder.EmitField<TProperty>(MangleName(PropertyName), isPublic: true);
        }

        PropertyBuilder propertyBuilder = typeBuilder.DefineProperty(PropertyName, PropertyAttributes.None, typeof(ILChunkExecutionState), Type.EmptyTypes);
        MethodBuilder getMethod = typeBuilder.DefineMethod($"get_{PropertyName}", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, typeof(ILChunkExecutionState), Type.EmptyTypes);
        ILGenerator getMethodIL = getMethod.GetILGenerator();
        getMethodIL.Emit(OpCodes.Ldarg_0);
        getMethodIL.Emit(OpCodes.Ldfld, field);
        getMethodIL.Emit(OpCodes.Ret);
        propertyBuilder.SetGetMethod(getMethod);
        MethodBuilder setMethod = typeBuilder.DefineMethod($"set_{PropertyName}", MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig, null, new Type[] { typeof(ILChunkExecutionState) });
        ILGenerator setMethodIL = setMethod.GetILGenerator();
        setMethodIL.Emit(OpCodes.Ldarg_0);
        setMethodIL.Emit(OpCodes.Ldarg_1);
        setMethodIL.Emit(OpCodes.Stfld, field);
        setMethodIL.Emit(OpCodes.Ret);
        propertyBuilder.SetSetMethod(setMethod);
        return propertyBuilder;
    }
}

public static class StackEmit
{
    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop<T>(this Emit<T> il, Local idx, int count)
    {
        il.LoadLocal(idx);
        il.LoadConstant(count);
        il.Subtract();
        il.StoreLocal(idx);
    }

    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop<T>(this Emit<T> il, Local local, Local count)
    {
        il.LoadLocal(local);
        il.LoadLocal(count);
        il.Subtract();
        il.StoreLocal(local);
    }
    /// <summary>
    /// Loads the previous EVM stack value on top of .NET stack.
    /// </summary>
    public static void StackLoadPrevious<T>(this Emit<T> il, Local stackHeadRef, int offset, int count)
    {
        il.LoadLocal(stackHeadRef);

        int offsetFromHead = offset - count;

        if (offsetFromHead == 0) return;

        il.LoadConstant(offsetFromHead * Word.Size);
        il.Convert<nint>();
        il.Call(UnsafeEmit.GetAddBytesOffsetRef<Word>());
    }

    public static void StackSetHead<T>(this Emit<T> il, Local stackHeadRef, int offset)
    {
        if (offset == 0) return;

        il.LoadLocal(stackHeadRef);
        il.LoadConstant(offset * Word.Size);
        il.Convert<nint>();
        il.Call(UnsafeEmit.GetAddBytesOffsetRef<Word>());
        il.StoreLocal(stackHeadRef);
    }

    public static void LoadItemFromSpan<T, U>(this Emit<T> il, Local local, Local idx)
    {
        il.LoadLocalAddress(local);
        il.LoadLocal(idx);
        il.Call(typeof(Span<U>).GetMethod("get_Item"));
    }

    public static void CleanWord<T>(this Emit<T> il, Local stackHeadRef, int offset, int count)
    {
        il.StackLoadPrevious(stackHeadRef, offset, count);
        il.InitializeObject(typeof(Word));
    }


    public static void CleanAndLoadWord<T>(this Emit<T> il, Local stackHeadRef, int offset, int count)
    {
        il.StackLoadPrevious(stackHeadRef, offset, count);
        il.Duplicate();

        il.InitializeObject(typeof(Word));
    }

    /// <summary>
    /// Advances the stack one word up.
    /// </summary>
    public static void StackPush<T>(this Emit<T> il, Local idx, int count)
    {
        il.LoadLocal(idx);
        il.LoadConstant(count);
        il.Add();
        il.StoreLocal(idx);
    }
}

public static class WordEmit
{

    public static void EmitShiftUInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, bool isLeft, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        MethodInfo shiftOp = typeof(UInt256).GetMethod(isLeft ? nameof(UInt256.LeftShift) : nameof(UInt256.RightShift));
        Label skipPop = il.DefineLabel();
        Label endOfOpcode = il.DefineLabel();

        // Note: Use Vector256 directoly if UInt256 does not use it internally
        // we the two uint256 from the locals.stackHeadRef
        Local shiftBit = il.DeclareLocal<uint>();

        il.StackLoadPrevious(stack.headRef, stack.offset, 1);
        il.Call(Word.GetUInt256);
        il.Duplicate();
        il.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.u0)));
        il.Convert<uint>();
        il.StoreLocal(shiftBit);
        il.StoreLocal(locals[0]);

        il.LoadLocalAddress(locals[0]);
        il.LoadConstant(Word.FullSize);
        il.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
        il.BranchIfFalse(skipPop);

        il.StackLoadPrevious(stack.headRef, stack.offset, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);
        il.LoadLocalAddress(locals[1]);

        il.LoadLocal(shiftBit);

        il.LoadLocalAddress(uint256R);

        il.Call(shiftOp);

        il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
        il.LoadLocal(uint256R);
        il.Call(Word.SetUInt256);
        il.Branch(endOfOpcode);

        il.MarkLabel(skipPop);

        il.CleanWord(stack.headRef, stack.offset, 2);

        il.MarkLabel(endOfOpcode);
    }
    public static void EmitShiftInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label aBiggerOrEqThan256 = il.DefineLabel();
        Label signIsNeg = il.DefineLabel();
        Label endOfOpcode = il.DefineLabel();

        // Note: Use Vector256 directoly if UInt256 does not use it internally
        // we the two uint256 from the locals.stackHeadRef
        il.StackLoadPrevious(stack.headRef, stack.offset, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);

        il.StackLoadPrevious(stack.headRef, stack.offset, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);

        il.LoadLocalAddress(locals[0]);
        il.LoadConstant(Word.FullSize);
        il.Call(typeof(UInt256).GetMethod("op_LessThan", new[] { typeof(UInt256).MakeByRefType(), typeof(int) }));
        il.BranchIfFalse(aBiggerOrEqThan256);

        using Local shiftBits = il.DeclareLocal<int>();


        il.LoadLocalAddress(locals[1]);
        il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadLocalAddress(locals[0]);
        il.LoadField(GetFieldInfo<UInt256>(nameof(UInt256.u0)));
        il.Convert<int>();
        il.LoadLocalAddress(uint256R);
        il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
        il.Call(typeof(Int256.Int256).GetMethod(nameof(Int256.Int256.RightShift), [typeof(int), typeof(Int256.Int256).MakeByRefType()]));
        il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
        il.LoadLocal(uint256R);
        il.Call(Word.SetUInt256);
        il.Branch(endOfOpcode);

        il.MarkLabel(aBiggerOrEqThan256);

        il.LoadLocalAddress(locals[1]);
        il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
        il.Call(GetPropertyInfo(typeof(Int256.Int256), nameof(Int256.Int256.Sign), false, out _));
        il.LoadConstant(0);
        il.BranchIfLess(signIsNeg);

        il.CleanWord(stack.headRef, stack.offset, 2);
        il.Branch(endOfOpcode);

        // sign
        il.MarkLabel(signIsNeg);
        il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
        il.LoadFieldAddress(GetFieldInfo(typeof(Int256.Int256), nameof(Int256.Int256.MinusOne)));
        il.Call(UnsafeEmit.GetAsMethodInfo<Int256.Int256, UInt256>());
        il.LoadObject<UInt256>();
        il.Call(Word.SetUInt256);
        il.Branch(endOfOpcode);

        il.MarkLabel(endOfOpcode);
    }
    public static void EmitBitwiseUInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        // Note: Use Vector256 directoly if UInt256 does not use it internally
        // we the two uint256 from the stack.headRef
        MethodInfo refWordToRefByteMethod = UnsafeEmit.GetAsMethodInfo<Word, byte>();
        MethodInfo readVector256Method = UnsafeEmit.GetReadUnalignedMethodInfo<Vector256<byte>>();
        MethodInfo writeVector256Method = UnsafeEmit.GetWriteUnalignedMethodInfo<Vector256<byte>>();
        MethodInfo operationUnegenerified = operation.MakeGenericMethod(typeof(byte));

        using Local vectorResult = il.DeclareLocal<Vector256<byte>>();

        il.StackLoadPrevious(stack.headRef, stack.offset, 1);
        il.Call(refWordToRefByteMethod);
        il.Call(readVector256Method);
        il.StackLoadPrevious(stack.headRef, stack.offset, 2);
        il.Call(refWordToRefByteMethod);
        il.Call(readVector256Method);

        il.Call(operationUnegenerified);
        il.StoreLocal(vectorResult);

        il.StackLoadPrevious(stack.headRef, stack.offset, 2);
        il.Call(refWordToRefByteMethod);
        il.LoadLocal(vectorResult);
        il.Call(writeVector256Method);
    }
    public static void EmitComparaisonUInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        // we the two uint256 from the stack.headRef
        il.StackLoadPrevious(stack.headRef, stack.offset, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.headRef, stack.offset, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[0]);
        il.LoadLocalAddress(locals[1]);
        il.Call(operation);

        // convert to conv_i
        il.Convert<int>();
        il.Call(ConvertionExplicit<UInt256, int>());
        il.StoreLocal(uint256R);

        // push the result to the stack.headRef
        il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
        il.LoadLocal(uint256R); // stack.headRef: word*, uint256
        il.Call(Word.SetUInt256);
    }
    public static void EmitComparaisonInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, bool isGreaterThan, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label endOpcodeHandling = il.DefineLabel();
        Label pushOnehandling = il.DefineLabel();
        // we the two uint256 from the stack.headRef
        il.StackLoadPrevious(stack.headRef, stack.offset, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.headRef, stack.offset, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[0]);
        il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadLocalAddress(locals[1]);
        il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadObject<Int256.Int256>();
        il.Call(operation);
        il.LoadConstant(0);
        if (isGreaterThan)
        {
            il.BranchIfGreater(pushOnehandling);
        }
        else
        {
            il.BranchIfLess(pushOnehandling);
        }

        il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
        il.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.Zero)));
        il.Branch(endOpcodeHandling);

        il.MarkLabel(pushOnehandling);
        il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
        il.LoadField(GetFieldInfo(typeof(UInt256), nameof(UInt256.One)));
        il.Branch(endOpcodeHandling);

        // push the result to the stack.headRef
        il.MarkLabel(endOpcodeHandling);
        il.Call(Word.SetUInt256);
    }
    public static void EmitBinaryUInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label label = il.DefineLabel();
        il.StackLoadPrevious(stack.headRef, stack.offset, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.headRef, stack.offset, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);

        // incase of custom handling, we branch to the label
        customHandling?.Invoke(il, label, locals);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[0]);
        il.LoadLocalAddress(locals[1]);
        il.LoadLocalAddress(uint256R);
        il.Call(operation);

        // skip the main handling
        il.MarkLabel(label);

        // push the result to the stack.headRef
        il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
        il.LoadLocal(uint256R); // stack.headRef: word*, uint256
        il.Call(Word.SetUInt256);
    }
    public static void EmitBinaryInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label label = il.DefineLabel();

        // we the two uint256 from the stack.headRef
        il.StackLoadPrevious(stack.headRef, stack.offset, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.headRef, stack.offset, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);

        // incase of custom handling, we branch to the label
        customHandling?.Invoke(il, label, locals);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[0]);
        il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadLocalAddress(locals[1]);
        il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
        il.LoadLocalAddress(uint256R);
        il.Call(UnsafeEmit.GetAsMethodInfo<UInt256, Int256.Int256>());
        il.Call(operation);

        // skip the main handling
        il.MarkLabel(label);

        // push the result to the stack.headRef
        il.CleanAndLoadWord(stack.headRef, stack.offset, 2);
        il.LoadLocal(uint256R); // stack.headRef: word*, uint256
        il.Call(Word.SetUInt256);
    }
    public static void EmitTrinaryUInt256Method<T>(Emit<T> il, Local uint256R, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label label = il.DefineLabel();

        // we the two uint256 from the locals.stackHeadRef
        il.StackLoadPrevious(stack.headRef, stack.offset, 1);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[0]);
        il.StackLoadPrevious(stack.headRef, stack.offset, 2);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[1]);
        il.StackLoadPrevious(stack.headRef, stack.offset, 3);
        il.Call(Word.GetUInt256);
        il.StoreLocal(locals[2]);

        // incase of custom handling, we branch to the label
        customHandling?.Invoke(il, label, locals);

        // invoke op  on the uint256
        il.LoadLocalAddress(locals[0]);
        il.LoadLocalAddress(locals[1]);
        il.LoadLocalAddress(locals[2]);
        il.LoadLocalAddress(uint256R);
        il.Call(operation);

        // skip the main handling
        il.MarkLabel(label);

        // push the result to the stack.headRef
        il.CleanAndLoadWord(stack.headRef, stack.offset, 3);
        il.LoadLocal(uint256R); // stack.headRef: word*, uint256
        il.Call(Word.SetUInt256);
    }

    public static void CallGetter<T>(this Emit<T> il, MethodInfo getterInfo, bool isLittleEndian)
    {
        il.Call(getterInfo);
        if (isLittleEndian)
        {
            il.Call(typeof(BinaryPrimitives).GetMethod(nameof(BinaryPrimitives.ReverseEndianness), [getterInfo.ReturnType]));
        }
    }

    public static void CallSetter<T>(this Emit<T> il, MethodInfo setterInfo, bool isLittleEndian)
    {
        if (isLittleEndian)
        {
            il.Call(typeof(BinaryPrimitives).GetMethod(nameof(BinaryPrimitives.ReverseEndianness), [setterInfo.GetParameters()[0].ParameterType]));
        }
        il.Call(setterInfo);
    }

    public static void EmitIsOneCheck<T>(this Emit<T> il, Local? word = null)
    {
        if (word is not null)
        {
            il.LoadLocalAddress(word);
        }

        if (BitConverter.IsLittleEndian)
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsOneLittleEndian)).GetMethod;
            il.Call(methodInfo);
        }
        else
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsOneBigEndian)).GetMethod;
            il.Call(methodInfo);
        }
    }

    public static void EmitIsZeroCheck<T>(this Emit<T> il, Local? word = null)
    {
        if (word is not null)
        {
            il.LoadLocalAddress(word);
        }

        MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsZero)).GetMethod;
        il.Call(methodInfo);
    }

    public static void EmitIsMinusOneCheck<T>(this Emit<T> il, Local? word = null)
    {
        if (word is not null)
        {
            il.LoadLocalAddress(word);
        }

        MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsMinusOne)).GetMethod;
        il.Call(methodInfo);
    }

    public static void EmitIsZeroOrOneCheck<T>(this Emit<T> il, Local? word = null)
    {
        if (word is not null)
        {
            il.LoadLocalAddress(word);
        }

        if (BitConverter.IsLittleEndian)
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsOneOrZeroLittleEndian)).GetMethod;
            il.Call(methodInfo);
        }
        else
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsOneOrZeroBigEndian)).GetMethod;
            il.Call(methodInfo);
        }
    }

    public static void EmitIsP255Check<T>(this Emit<T> il, Local? word = null)
    {
        if (word is not null)
        {
            il.LoadLocalAddress(word);
        }

        if (BitConverter.IsLittleEndian)
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsP255LittleEndian)).GetMethod;
            il.Call(methodInfo);
        }
        else
        {
            MethodInfo methodInfo = typeof(Word).GetProperty(nameof(Word.IsP255BigEndian)).GetMethod;
            il.Call(methodInfo);
        }
    }
}
public static class UnsafeEmit
{

    public static MethodInfo GetAddOffsetRef<TResult>()
    {
        MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.Add) && m.GetParameters()[0].ParameterType.IsByRef && m.GetParameters()[1].ParameterType == typeof(nint));
        return method.MakeGenericMethod(typeof(TResult));
    }

    public static MethodInfo GetSubtractOffsetRef<TResult>()
    {
        MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.Subtract) && m.GetParameters()[0].ParameterType.IsByRef && m.GetParameters()[1].ParameterType == typeof(nint));
        return method.MakeGenericMethod(typeof(TResult));
    }

    public static MethodInfo GetAddBytesOffsetRef<TResult>()
    {
        MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.AddByteOffset) && m.GetParameters()[0].ParameterType.IsByRef && m.GetParameters()[1].ParameterType == typeof(nint));
        return method.MakeGenericMethod(typeof(TResult));
    }

    public static MethodInfo GetSubtractBytesOffsetRef<TResult>()
    {
        MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.SubtractByteOffset) && m.GetParameters()[0].ParameterType.IsByRef && m.GetParameters()[1].ParameterType == typeof(nint));
        return method.MakeGenericMethod(typeof(TResult));
    }

    public static MethodInfo GetReadUnalignedMethodInfo<TResult>()
    {
        MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.ReadUnaligned) && m.GetParameters()[0].ParameterType.IsByRef);
        return method.MakeGenericMethod(typeof(TResult));
    }

    public static MethodInfo GetWriteUnalignedMethodInfo<TResult>()
    {
        MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.WriteUnaligned) && m.GetParameters()[0].ParameterType.IsByRef);
        return method.MakeGenericMethod(typeof(TResult));
    }

    public static MethodInfo GetAsMethodInfo<TOriginal, TResult>()
    {
        MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.As) && m.ReturnType.IsByRef);
        return method.MakeGenericMethod(typeof(TOriginal), typeof(TResult));
    }

#pragma warning disable CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type
    public unsafe static MethodInfo GetCastMethodInfo<TOriginal, TResult>() where TResult : struct
    {
        MethodInfo method = typeof(UnsafeEmit).GetMethod(nameof(UnsafeEmit.ReinterpretCast));
        return method.MakeGenericMethod(typeof(TOriginal), typeof(TResult));
    }
    public unsafe static Span<TResult> ReinterpretCast<TOriginal, TResult>(Span<TOriginal> original)
        where TOriginal : struct
        where TResult : struct
    {
        Span<TResult> result = Span<TResult>.Empty;
        fixed (TOriginal* ptr = original)
        {
            result = new Span<TResult>(ptr, original.Length * sizeof(TOriginal) / sizeof(TResult));
        }
        return result;
    }
#pragma warning restore CS8500 // This takes the address of, gets the size of, or declares a pointer to a managed type

}

/// <summary>
/// Extensions for <see cref="ILGenerator"/>.
/// </summary>
static class EmitExtensions
{
    public static void UpdateStackHeadAndPushRerSegmentMode<T>(Emit<T> method, Local stackHeadRef, Local stackHeadIdx, int pc, SubSegmentMetadata stackMetadata)
    {
        if (stackMetadata.LeftOutStack != 0 && pc == stackMetadata.End)
        {
            method.StackSetHead(stackHeadRef, stackMetadata.LeftOutStack);
            method.LoadLocal(stackHeadIdx);
            method.LoadConstant(stackMetadata.LeftOutStack);
            method.Add();
            method.StoreLocal(stackHeadIdx);
        }
    }

    public static void UpdateStackHeadIdxAndPushRefOpcodeMode<T>(Emit<T> method, Local stackHeadRef, Local stackHeadIdx, OpcodeInfo op)
    {
        var delta = op.Metadata.StackBehaviorPush - op.Metadata.StackBehaviorPop;
        method.LoadLocal(stackHeadIdx);
        method.LoadConstant(delta);
        method.Add();
        method.StoreLocal(stackHeadIdx);

        method.StackSetHead(stackHeadRef, delta);
    }

    public static void EmitCallToErrorTrace<T>(Emit<T> method, Local gasAvailable, KeyValuePair<EvmExceptionType, Label> kvp, EnvLoader<T> envLoader, Locals<T> locals)
    {
        Label skipTracing = method.DefineLabel();
        envLoader.LoadTxTracer(method, locals, false);
        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
        method.BranchIfFalse(skipTracing);

        envLoader.LoadTxTracer(method, locals, false);
        method.LoadLocal(gasAvailable);
        method.LoadConstant((int)kvp.Key);
        method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.EndInstructionTraceError), BindingFlags.Static | BindingFlags.NonPublic));

        method.MarkLabel(skipTracing);
    }
    public static void EmitCallToEndInstructionTrace<T>(Emit<T> method, Local gasAvailable, EnvLoader<T> envLoader, Locals<T> locals)
    {
        Label skipTracing = method.DefineLabel();
        envLoader.LoadTxTracer(method, locals, false);
        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
        method.BranchIfFalse(skipTracing);

        envLoader.LoadTxTracer(method, locals, false);
        method.LoadLocal(gasAvailable);
        envLoader.LoadMemory(method, locals, true);
        method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
        method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.EndInstructionTrace), BindingFlags.Static | BindingFlags.NonPublic));

        method.MarkLabel(skipTracing);
    }
    public static void EmitCallToStartInstructionTrace<T>(Emit<T> method, Local gasAvailable, Local head, OpcodeInfo op, EnvLoader<T> envLoader, Locals<T> locals)
    {
        Label skipTracing = method.DefineLabel();
        envLoader.LoadTxTracer(method, locals, false);
        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
        method.BranchIfFalse(skipTracing);

        envLoader.LoadTxTracer(method, locals, false);
        method.LoadConstant((int)op.Operation);
        envLoader.LoadVmState(method, locals, false);
        method.LoadLocal(gasAvailable);
        method.LoadConstant(op.ProgramCounter);
        method.LoadLocal(head);
        method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsOptimizing>.StartInstructionTrace), BindingFlags.Static | BindingFlags.NonPublic));

        method.MarkLabel(skipTracing);
    }
    public static MethodInfo ConvertionImplicit<TFrom, TTo>() => ConvertionImplicit(typeof(TFrom), typeof(TTo));
    public static MethodInfo ConvertionImplicit(Type tfrom, Type tto) => tfrom.GetMethod("op_Implicit", new[] { tto });
    public static MethodInfo ConvertionExplicit<TFrom, TTo>() => ConvertionExplicit(typeof(TFrom), typeof(TTo));
    public static MethodInfo ConvertionExplicit(Type tfrom, Type tto) => tfrom.GetMethod("op_Explicit", new[] { tto });
    public static FieldInfo GetFieldInfo<T>(string name, BindingFlags? flags = null) => GetFieldInfo(typeof(T), name, flags);
    public static FieldInfo GetFieldInfo(Type TypeInstance, string name, BindingFlags? flags = null)
    {
        return flags is null ? TypeInstance.GetField(name) : TypeInstance.GetField(name, flags.Value);
    }
    public static MethodInfo GetPropertyInfo<T>(string name, bool getSetter, out PropertyInfo propInfo, BindingFlags? flags = null)
        => GetPropertyInfo(typeof(T), name, getSetter, out propInfo, flags);
    public static MethodInfo GetPropertyInfo(Type typeInstance, string name, bool getSetter, out PropertyInfo propInfo, BindingFlags? flags = null)
    {
        propInfo = flags is null ? typeInstance.GetProperty(name) : typeInstance.GetProperty(name, flags.Value);
        return getSetter ? propInfo.GetSetMethod() : propInfo.GetGetMethod();
    }

    public static void LoadRefArgument<T>(this Emit<T> il, ushort index, Type targetType)
    {
        il.LoadArgument(index);
        if (targetType.IsValueType)
        {
            il.LoadObject(targetType);
        }
        else
        {
            il.LoadIndirect(targetType);
        }
    }

    public static void Print<T>(this Emit<T> il, Local local)
    {
        if (local.LocalType.IsValueType)
        {
            il.LoadLocalAddress(local);
            il.Call(local.LocalType.GetMethod("ToString", []));
        }
        else
        {
            il.LoadLocal(local);
            il.CallVirtual(local.LocalType.GetMethod("ToString", []));
        }
        il.Call(typeof(Debug).GetMethod(nameof(Debug.Write), [typeof(string)]));
    }
    public static void PrintString<T>(this Emit<T> il, string msg)
    {
        using Local local = il.DeclareLocal<string>();

        il.LoadConstant(msg);
        il.StoreLocal(local);
        il.Print(local);
    }


    public static MethodInfo MethodInfo<T>(string name, Type returnType, Type[] argTypes, BindingFlags flags = BindingFlags.Public)
    {
        return typeof(T).GetMethods().First(m => m.Name == name && m.ReturnType == returnType && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(argTypes));
    }

    public static void FindCorrectBranchAndJump<T>(this Emit<T> il, Local jmpDestination, Dictionary<int, Sigil.Label> jumpDestinations, Dictionary<EvmExceptionType, Sigil.Label> evmExceptionLabels)
    {
        int numberOfBitsSet = BitOperations.Log2((uint)jumpDestinations.Count) + 1;

        int length = 1 << numberOfBitsSet;
        int bitMask = length - 1;
        Sigil.Label[] jumps = new Sigil.Label[length];
        for (int i = 0; i < length; i++)
        {
            jumps[i] = il.DefineLabel();
        }

        il.LoadLocal(jmpDestination);
        il.LoadConstant(bitMask);
        il.And();


        il.Switch(jumps);

        for (int i = 0; i < length; i++)
        {
            il.MarkLabel(jumps[i]);
            // for each destination matching the bit mask emit check for the equality
            foreach (ushort dest in jumpDestinations.Keys.Where(dest => (dest & bitMask) == i))
            {
                il.LoadLocal(jmpDestination);
                il.LoadConstant(dest);
                il.BranchIfEqual(jumpDestinations[dest]);
            }
            // each bucket ends with a jump to invalid access to do not fall through to another one
            il.Branch(evmExceptionLabels[EvmExceptionType.InvalidJumpDestination]);
        }
    }

    // requires a zeroed WORD on the stack
    public static void SpecialPushOpcode<T>(this Emit<T> il, OpcodeInfo op, byte[][] data)
    {
        uint count = op.Operation - Instruction.PUSH0;
        uint argSize = BitOperations.RoundUpToPowerOf2(count);

        int argIndex = op.Arguments.Value;
        byte[] zpbytes = data[argIndex].AsSpan().SliceWithZeroPadding(0, (int)argSize, PadDirection.Left).ToArray();

        switch (op.Operation)
        {
            case Instruction.PUSH1:
                il.LoadConstant(zpbytes[0]);
                il.CallSetter(Word.SetByte0, BitConverter.IsLittleEndian);
                break;
            case Instruction.PUSH2:
                il.LoadConstant(BinaryPrimitives.ReadUInt16BigEndian(zpbytes));
                il.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                break;
            case Instruction.PUSH3:
            case Instruction.PUSH4:
                il.LoadConstant(BinaryPrimitives.ReadUInt32BigEndian(zpbytes));
                il.CallSetter(Word.SetUInt0, BitConverter.IsLittleEndian);
                break;
            case Instruction.PUSH5:
            case Instruction.PUSH6:
            case Instruction.PUSH7:
            case Instruction.PUSH8:
                il.LoadConstant(BinaryPrimitives.ReadUInt64BigEndian(zpbytes));
                il.CallSetter(Word.SetULong0, BitConverter.IsLittleEndian);
                break;
        }
    }

    public static void EmitAsSpan<T>(this Emit<T> il)
    {
        MethodInfo method = typeof(System.MemoryExtensions).GetMethods()
            .Where(m => m.Name == nameof(System.MemoryExtensions.AsSpan)
                        && m.GetParameters().Length == 3
                        && m.IsGenericMethod)
            .First();
        il.Call(method.MakeGenericMethod(typeof(byte)));
    }


    public static void WhileBranch<T>(this Emit<T> il, Local cond, Action<Emit<T>, Local> action)
    {
        var start = il.DefineLabel();
        var end = il.DefineLabel();

        // start of the loop
        il.MarkLabel(start);

        // if cond
        il.LoadLocal(cond);
        il.BranchIfFalse(end);

        // emit body of loop
        action(il, cond);

        // jump to start of the loop
        il.Branch(start);

        // end of the loop
        il.MarkLabel(end);
    }

    public static void ForBranch<T>(this Emit<T> il, Local count, Action<Emit<T>, Local> action)
    {
        var start = il.DefineLabel();
        var end = il.DefineLabel();

        // declare i
        var i = il.DeclareLocal<int>();

        // we initialize i to 0
        il.LoadConstant(0);
        il.StoreLocal(i);

        // start of the loop
        il.MarkLabel(start);

        // i < count
        il.LoadLocal(i);
        il.LoadLocal(count);
        il.BranchIfEqual(end);

        // emit body of loop
        action(il, i);

        // i++
        il.LoadLocal(i);
        il.LoadConstant(1);
        il.Add();
        il.StoreLocal(i);

        // jump to start of the loop
        il.Branch(start);

        // end of the loop
        il.MarkLabel(end);
    }

    public static void LoadArray<T>(this Emit<T> il, ReadOnlySpan<byte> value)
    {
        il.LoadConstant(value.Length);
        il.NewArray<byte>();

        // get methodInfo of AsSpan from int[] it is a public instance method

        for (int i = 0; i < value.Length; i++)
        {
            il.Duplicate();
            il.LoadConstant(i);
            il.LoadConstant(value[i]);
            il.StoreElement<byte>();
        }

        il.Call(typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", new[] { typeof(byte[]) }));

    }
    public static void FakeBranch<T>(this Emit<T> il, Sigil.Label label)
    {
        il.LoadConstant(true);
        il.BranchIfTrue(label);
    }

    public static Sigil.Label AddExceptionLabel<T>(this Emit<T> il, Dictionary<EvmExceptionType, Sigil.Label> dict, EvmExceptionType evmExceptionType)
    {
        if (!dict.ContainsKey(evmExceptionType))
        {
            dict[evmExceptionType] = il.DefineLabel();
        }
        return dict[evmExceptionType];
    }
}
