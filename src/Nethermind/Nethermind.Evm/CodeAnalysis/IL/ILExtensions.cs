// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Db;
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
using System.Text;
using static Nethermind.Evm.CodeAnalysis.IL.EmitExtensions;
using Label = Sigil.Label;

namespace Nethermind.Evm.CodeAnalysis.IL;
public static class GasEmit
{
    public static void EmitStaticGasCheck<T>(this Emit<T> il, Local gasAvailable, long gasCost, Dictionary<EvmExceptionType, Label> evmExceptionLabels)
    {
        il.LoadLocal(gasAvailable);
        il.LoadConstant(gasCost);
        il.Subtract();
        il.Duplicate();
        il.StoreLocal(gasAvailable);
        il.LoadConstant((long)0);
        il.BranchIfLess(il.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.OutOfGas));
    }
}
public static class ReleaseSpecEmit
{
    private static string GetSegmentId(SubSegmentMetadata subsegment) => subsegment.Instructions
            .Aggregate(new StringBuilder(), (acc, op) => acc.Append(op.ToString()))
            .ToString();
    public static void DeclareOpcodeValidityCheckVariables<T>(Emit<T> method, ContractCompilerMetadata metadata, Locals<T> locals)
    {
        foreach (var subSegment in metadata.SubSegments.Values.Where(subs => subs.IsReachable))
        {
            locals.TryDeclareLocal(GetSegmentId(subSegment), typeof(Nullable<bool>));
        }
    }

    public static void EmitAmortizedStaticEnvCheck<T>(this Emit<T> method, SubSegmentMetadata segmentMetadata, Locals<T> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels)
    {
        method.LoadVmState(locals, false);
        method.Call(GetPropertyInfo(typeof(EvmState), nameof(EvmState.IsStatic), false, out _));
        method.BranchIfTrue(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.StaticCallViolation));
    }

    public static void EmitAmortizedOpcodeCheck<T>(this Emit<T> method, SubSegmentMetadata segmentMetadata, Locals<T> locals, Dictionary<EvmExceptionType, Label> evmExceptionLabels)
    {
        Label alreadyCheckedLabel = method.DefineLabel(locals.GetLabelName());
        Label hasToCheckLabel = method.DefineLabel(locals.GetLabelName());
        string segmentRefName = GetSegmentId(segmentMetadata);
        if (!locals.TryLoadLocal(segmentRefName, true))
        {
            throw new InvalidOperationException($"method {nameof(DeclareOpcodeValidityCheckVariables) } must be called before calling {nameof(EmitAmortizedOpcodeCheck)}");
        }

        method.Call(typeof(bool?).GetProperty(nameof(Nullable<bool>.HasValue)).GetGetMethod());
        method.BranchIfTrue(alreadyCheckedLabel);

        foreach (var opcode in segmentMetadata.Instructions)
        {
            if(opcode.RequiresAvailabilityCheck())
            {
                method.LoadSpec(locals, false);
                method.LoadConstant((byte)opcode);
                method.Call(typeof(InstructionExtensions).GetMethod(nameof(InstructionExtensions.IsEnabled)));
                method.BranchIfFalse(method.AddExceptionLabel(evmExceptionLabels, EvmExceptionType.BadInstruction));
            }
        }

        method.LoadConstant(true);
        method.NewObject(typeof(bool?).GetConstructor([typeof(bool)]));
        locals.TryStoreLocal(segmentRefName);

        method.MarkLabel(alreadyCheckedLabel);
    }
}
public static class StackEmit
{
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

    public static void LoadItemFromSpan<T, U>(this Emit<T> il, Local idx, bool isReadOnly, Local? local = null)
    {
        if (local is not null)
        {
            il.LoadLocalAddress(local);
        }

        il.LoadLocal(idx);
        if (isReadOnly)
        {
            il.Call(typeof(ReadOnlySpan<U>).GetMethod("get_Item"));
        }
        else
        {
            il.Call(typeof(Span<U>).GetMethod("get_Item"));
        }
    }
    public static void LoadItemFromSpan<T, U>(this Emit<T> il, int idx, bool isReadOnly, Local? local = null)
    {
        if (local is not null)
        {
            il.LoadLocalAddress(local);
        }

        il.LoadConstant(idx);
        if(isReadOnly)
        {
            il.Call(typeof(ReadOnlySpan<U>).GetMethod("get_Item"));
        }
        else
        {
            il.Call(typeof(Span<U>).GetMethod("get_Item"));
        }
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
}
public static class WordEmit
{
    public static void EmitShiftUInt256Method<T>(Emit<T> il, Local uint256R, Locals<T> localNames, (Local headRef, Local headIdx, int offset) stack, bool isLeft, Dictionary<EvmExceptionType, Label> exceptions, params ReadOnlySpan<Local> locals)
    {
        MethodInfo shiftOp = typeof(UInt256).GetMethod(isLeft ? nameof(UInt256.LeftShift) : nameof(UInt256.RightShift));
        Label skipPop = il.DefineLabel(localNames.GetLabelName());
        Label endOfOpcode = il.DefineLabel(localNames.GetLabelName());

        // Note: Use Vector256 directoly if UInt256 does not use it internally
        // we the two uint256 from the locals.stackHeadRef
        Local shiftBit = il.DeclareLocal<uint>(localNames.GetLocalName());

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
    public static void EmitShiftInt256Method<T>(Emit<T> il, Local uint256R, Locals<T> localNames, (Local headRef, Local headIdx, int offset) stack, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label aBiggerOrEqThan256 = il.DefineLabel(localNames.GetLabelName());
        Label signIsNeg = il.DefineLabel(localNames.GetLabelName());
        Label endOfOpcode = il.DefineLabel(localNames.GetLabelName());

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

        using Local shiftBits = il.DeclareLocal<int>(localNames.GetLocalName());


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

        using Local minusOneLocal = il.DeclareLocal<Int256.Int256>(localNames.GetLocalName());
        il.LoadField(GetFieldInfo(typeof(Int256.Int256), nameof(Int256.Int256.MinusOne)));
        il.StoreLocal(minusOneLocal);

        il.LoadLocalAddress(minusOneLocal);
        il.Call(UnsafeEmit.GetAsMethodInfo<Int256.Int256, UInt256>());
        il.LoadObject<UInt256>();
        il.Call(Word.SetUInt256);
        il.Branch(endOfOpcode);

        il.MarkLabel(endOfOpcode);
    }
    public static void EmitBitwiseUInt256Method<T>(Emit<T> il, Local uint256R, Locals<T> localNames, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        // Note: Use Vector256 directoly if UInt256 does not use it internally
        // we the two uint256 from the stack.headRef
        MethodInfo refWordToRefByteMethod = UnsafeEmit.GetAsMethodInfo<Word, byte>();
        MethodInfo readVector256Method = UnsafeEmit.GetReadUnalignedMethodInfo<Vector256<byte>>();
        MethodInfo writeVector256Method = UnsafeEmit.GetWriteUnalignedMethodInfo<Vector256<byte>>();
        MethodInfo operationUnegenerified = operation.MakeGenericMethod(typeof(byte));

        using Local vectorResult = il.DeclareLocal<Vector256<byte>>(localNames.GetLocalName());

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
    public static void EmitComparaisonUInt256Method<T>(Emit<T> il, Local uint256R, Locals<T> localNames, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
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
    public static void EmitComparaisonInt256Method<T>(Emit<T> il, Local uint256R, Locals<T> localNames, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, bool isGreaterThan, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label endOpcodeHandling = il.DefineLabel(localNames.GetLabelName());
        Label pushOnehandling = il.DefineLabel(localNames.GetLabelName());
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
    public static void EmitBinaryUInt256Method<T>(Emit<T> il, Local uint256R, Locals<T> localNames, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label label = il.DefineLabel(localNames.GetLabelName());
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
    public static void EmitBinaryInt256Method<T>(Emit<T> il, Local uint256R, Locals<T> localNames, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label label = il.DefineLabel(localNames.GetLabelName());

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
    public static void EmitTrinaryUInt256Method<T>(Emit<T> il, Local uint256R, Locals<T> localNames, (Local headRef, Local headIdx, int offset) stack, MethodInfo operation, Action<Emit<T>, Label, Local[]> customHandling, Dictionary<EvmExceptionType, Label> exceptions, params Local[] locals)
    {
        Label label = il.DefineLabel(localNames.GetLabelName());

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

    public static void EmitCheck<T>(this Emit<T> il, string checkName, Local? word = null)
    {
        if (word is not null)
        {
            if (word.LocalType != typeof(Word).MakeByRefType())
            {
                throw new Exception($"Expected type {typeof(Word).MakeByRefType()} found type {word.LocalType}");
            }
            il.LoadLocal(word);
        }

        PropertyInfo checkProp = typeof(Word).GetProperty(checkName);

        if(checkProp is null)
        {
            throw new Exception($"Type of Word does not have a property named {checkName}");
        }

        if (checkProp.PropertyType != typeof(bool))
        {
            throw new Exception($"Expected check to return type {typeof(bool)} found type {checkProp.PropertyType}");
        }

        il.Call(checkProp.GetMethod);
    }
}
public static class UnsafeEmit
{
    public static MethodInfo GetAddBytesOffsetRef<TResult>()
    {
        MethodInfo method = typeof(Unsafe).GetMethods().First((m) => m.Name == nameof(Unsafe.AddByteOffset) && m.GetParameters()[0].ParameterType.IsByRef && m.GetParameters()[1].ParameterType == typeof(nint));
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

    public static void UpdateStackHeadIdxAndPushRefOpcodeMode<T>(Emit<T> method, Local stackHeadRef, Local stackHeadIdx, OpcodeMetadata opMetadata)
    {
        var delta = opMetadata.StackBehaviorPush - opMetadata.StackBehaviorPop;
        method.LoadLocal(stackHeadIdx);
        method.LoadConstant(delta);
        method.Add();
        method.StoreLocal(stackHeadIdx);

        method.StackSetHead(stackHeadRef, delta);
    }

    public static void EmitCallToErrorTrace<T>(Emit<T> method, Local gasAvailable, KeyValuePair<EvmExceptionType, Label> kvp, Locals<T> locals)
    {
        Label skipTracing = method.DefineLabel(locals.GetLabelName());
        method.LoadTxTracer(locals, false);
        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
        method.BranchIfFalse(skipTracing);

        method.LoadTxTracer(locals, false);
        method.LoadLocal(gasAvailable);
        method.LoadConstant((int)kvp.Key);
        method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>.EndInstructionTraceError), BindingFlags.Static | BindingFlags.Public));

        method.MarkLabel(skipTracing);
    }
    public static void EmitCallToEndInstructionTrace<T>(Emit<T> method, Local gasAvailable, Locals<T> locals)
    {
        Label skipTracing = method.DefineLabel(locals.GetLabelName());
        method.LoadTxTracer(locals, false);
        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
        method.BranchIfFalse(skipTracing);

        method.LoadTxTracer(locals, false);
        method.LoadLocal(gasAvailable);
        method.LoadMemory(locals, true);
        method.Call(GetPropertyInfo<EvmPooledMemory>(nameof(EvmPooledMemory.Size), false, out _));
        method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>.EndInstructionTrace), BindingFlags.Static | BindingFlags.Public));

        method.MarkLabel(skipTracing);
    }
    public static void EmitCallToStartInstructionTrace<T>(Emit<T> method, Local gasAvailable, Local head, int pc, Instruction op, Locals<T> locals)
    {
        Label skipTracing = method.DefineLabel(locals.GetLabelName());
        method.LoadTxTracer(locals, false);
        method.CallVirtual(typeof(ITxTracer).GetProperty(nameof(ITxTracer.IsTracingInstructions)).GetGetMethod());
        method.BranchIfFalse(skipTracing);

        method.LoadTxTracer(locals, false);
        method.LoadConstant((int)op);
        method.LoadVmState(locals, false);
        method.LoadLocal(gasAvailable);
        method.LoadConstant(pc);
        method.LoadLocal(head);
        method.Call(typeof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>).GetMethod(nameof(VirtualMachine<VirtualMachine.IsTracing, VirtualMachine.IsPrecompiling>.StartInstructionTrace), BindingFlags.Static | BindingFlags.Public));

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

    public static MethodInfo MethodInfo<T>(string name, Type returnType, Type[] argTypes, BindingFlags flags = BindingFlags.Public)
    {
        return typeof(T).GetMethods().First(m => m.Name == name && m.ReturnType == returnType && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(argTypes));
    }

    public static void FindCorrectBranchAndJump<T>(this Emit<T> il, Local jmpDestination, Locals<T> localNames, Dictionary<int, Sigil.Label> jumpDestinations, Dictionary<EvmExceptionType, Sigil.Label> evmExceptionLabels)
    {
        int numberOfBitsSet = BitOperations.Log2((uint)jumpDestinations.Count) + 1;

        int length = 1 << numberOfBitsSet;
        int bitMask = length - 1;
        Sigil.Label[] jumps = new Sigil.Label[length];
        for (int i = 0; i < length; i++)
        {
            jumps[i] = il.DefineLabel(localNames.GetLabelName());
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
    public static void SpecialPushOpcode<T>(this Emit<T> il, Instruction op, ReadOnlySpan<byte> immediates)
    {
        uint count = op - Instruction.PUSH0;
        uint argSize = BitOperations.RoundUpToPowerOf2(count);

        byte[] zpbytes = immediates.SliceWithZeroPadding(0, (int)argSize, PadDirection.Left).ToArray();

        switch (op)
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
    public static void FakeBranch<T>(this Emit<T> il, Sigil.Label label)
    {
        il.LoadConstant(true);
        il.BranchIfTrue(label);
    }

    public static Sigil.Label AddExceptionLabel<T>(this Emit<T> il, Dictionary<EvmExceptionType, Sigil.Label> dict, EvmExceptionType evmExceptionType)
    {
        if (!dict.ContainsKey(evmExceptionType))
        {
            dict[evmExceptionType] = il.DefineLabel(evmExceptionType.ToString());
        }
        return dict[evmExceptionType];
    }
}
