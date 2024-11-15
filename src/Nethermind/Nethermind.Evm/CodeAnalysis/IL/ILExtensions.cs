// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Db;
using Nethermind.Evm.CodeAnalysis.IL;
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

namespace Nethermind.Evm.IL;

/// <summary>
/// Extensions for <see cref="ILGenerator"/>.
/// </summary>
static class EmitExtensions
{
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
        MethodInfo method = typeof(EmitExtensions).GetMethod(nameof(EmitExtensions.ReinterpretCast));
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
#if DEBUG
        il.Call(typeof(Debug).GetMethod(nameof(Debug.WriteLine), [typeof(string)]));
#endif
        il.Call(typeof(Console).GetMethod(nameof(Console.WriteLine), [typeof(string)]));
    }


    public static void PrintString<T>(this Emit<T> il, string msg)
    {
        using Local local = il.DeclareLocal<string>();

        il.LoadConstant(msg);
        il.StoreLocal(local);
        il.Print(local);
    }

    public static void Load<T, U>(this Emit<T> il, Local local, Local idx)
    {
        il.LoadLocalAddress(local);
        il.LoadLocal(idx);
        il.Call(typeof(Span<U>).GetMethod("get_Item"));
    }

    public static void Load<T>(this Emit<T> il, Local local, Local idx, FieldInfo wordField)
    {
        il.LoadLocalAddress(local);
        il.LoadLocal(idx);
        il.Call(typeof(Span<Word>).GetMethod("get_Item"));
        il.LoadField(wordField);
    }

    public static void CleanWord<T>(this Emit<T> il, Local local, Local idx)
    {
        il.LoadLocalAddress(local);
        il.LoadLocal(idx);
        il.Call(typeof(Span<Word>).GetMethod("get_Item"));

        il.InitializeObject(typeof(Word));
    }


    public static void CleanAndLoadWord<T>(this Emit<T> il, Local local, Local idx)
    {
        il.LoadLocalAddress(local);
        il.LoadLocal(idx);
        il.Call(typeof(Span<Word>).GetMethod("get_Item"));
        il.Duplicate();

        il.InitializeObject(typeof(Word));
    }

    /// <summary>
    /// Advances the stack one word up.
    /// </summary>
    public static void StackPush<T>(this Emit<T> il, Local idx, int count = 1)
    {
        il.LoadLocal(idx);
        il.LoadConstant(count);
        il.Add();
        il.StoreLocal(idx);
    }

    public static MethodInfo MethodInfo<T>(string name, Type returnType, Type[] argTypes, BindingFlags flags = BindingFlags.Public)
    {
        return typeof(T).GetMethods().First(m => m.Name == name && m.ReturnType == returnType && m.GetParameters().Select(p => p.ParameterType).SequenceEqual(argTypes));
    }

    public static void FindCorrectBranchAndJump<T>(this Emit<T> il, Local jmpDestination, Dictionary<int, Sigil.Label> jumpDestinations, Dictionary<EvmExceptionType, Sigil.Label> evmExceptionLabels)
    {
        int numberOfBitsSet = BitOperations.PopCount((uint)jumpDestinations.Count);

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
        if(BitConverter.IsLittleEndian)
        {
            Array.Reverse(zpbytes);
        }
        
        switch (op.Operation)
        {
            case Instruction.PUSH1:
                il.LoadConstant(zpbytes[0]);
                il.Call(Word.SetByte0);
                break;
            case Instruction.PUSH2:
                il.LoadConstant(BinaryPrimitives.ReadUInt16LittleEndian(zpbytes));
                il.Call(Word.SetUInt0);
                break;
            case Instruction.PUSH3:
            case Instruction.PUSH4:
                il.LoadConstant(BinaryPrimitives.ReadUInt32LittleEndian(zpbytes));
                il.Call(Word.SetUInt0);
                break;
            case Instruction.PUSH5:
            case Instruction.PUSH6:
            case Instruction.PUSH7:
            case Instruction.PUSH8:
                il.LoadConstant(BinaryPrimitives.ReadUInt64LittleEndian(zpbytes));
                il.Call(Word.SetULong0);
                break;
        }
    }

    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop<T>(this Emit<T> il, Local idx, int count = 1)
    {
        il.LoadLocal(idx);
        il.LoadConstant(count);
        il.Subtract();
        il.StoreLocal(idx);
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

    /// <summary>
    /// Loads the previous EVM stack value on top of .NET stack.
    /// </summary>
    public static void StackLoadPrevious<T>(this Emit<T> il, Local src, Local idx, int count = 1)
    {
        il.LoadLocalAddress(src);
        il.LoadLocal(idx);
        il.LoadConstant(count);
        il.Convert<int>();
        il.Subtract();
        il.Call(typeof(Span<Word>).GetMethod("get_Item"));
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
}
