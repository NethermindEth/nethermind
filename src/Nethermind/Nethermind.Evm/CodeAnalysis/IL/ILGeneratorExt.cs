// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.CodeAnalysis.IL;
using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Nethermind.Evm.IL;

/// <summary>
/// Extensions for <see cref="ILGenerator"/>.
/// </summary>
static class EmitExtensions
{
    public static void Load(this ILGenerator il, LocalBuilder local)
    {
        switch (local.LocalIndex)
        {
            case 0:
                il.Emit(OpCodes.Ldloc_0);
                break;
            case 1:
                il.Emit(OpCodes.Ldloc_1);
                break;
            case 2:
                il.Emit(OpCodes.Ldloc_2);
                break;
            case 3:
                il.Emit(OpCodes.Ldloc_3);
                break;
            default:
                if (local.LocalIndex < 255)
                {
                    il.Emit(OpCodes.Ldloc_S, (byte)local.LocalIndex);
                }
                else
                {
                    il.Emit(OpCodes.Ldloc, local.LocalIndex);
                }
                break;
        }
    }

    public static void LoadAddress(this ILGenerator il, LocalBuilder local)
    {
        if (local.LocalIndex <= 255)
        {
            il.Emit(OpCodes.Ldloca_S, (byte)local.LocalIndex);
        }
        else
        {
            il.Emit(OpCodes.Ldloca, local.LocalIndex);
        }
    }

    public static void Load(this ILGenerator il, LocalBuilder local, FieldInfo wordField)
    {
        if (local.LocalType != typeof(Word*))
        {
            throw new ArgumentException($"Only Word* can be used. This variable is of type {local.LocalType}");
        }

        if (wordField.DeclaringType != typeof(Word))
        {
            throw new ArgumentException($"Only Word fields can be used. This field is declared for {wordField.DeclaringType}");
        }

        il.Load(local);
        il.Emit(OpCodes.Ldfld, wordField);
    }

    public static void CleanWord(this ILGenerator il, LocalBuilder local)
    {
        if (local.LocalType != typeof(Word*))
        {
            throw new ArgumentException(
                $"Only {nameof(Word)} pointers are supported. The passed local was type of {local.LocalType}.");
        }

        il.Load(local);
        il.Emit(OpCodes.Initobj, typeof(Word));
    }

    /// <summary>
    /// Advances the stack one word up.
    /// </summary>
    public static void StackPush(this ILGenerator il, LocalBuilder local)
    {
        il.Load(local);
        il.LoadValue(Word.Size);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Add);
        il.Store(local);
    }

    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop(this ILGenerator il, LocalBuilder local, int count = 1)
    {
        il.Load(local);
        il.LoadValue(Word.Size * count);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sub);
        il.Store(local);
    }

    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop(this ILGenerator il, LocalBuilder local, LocalBuilder count)
    {
        il.Load(local);
        il.LoadValue(Word.Size);
        il.Load(count);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sub);
        il.Store(local);
    }

    /// <summary>
    /// Loads the previous EVM stack value on top of .NET stack.
    /// </summary>
    public static void StackLoadPrevious(this ILGenerator il, LocalBuilder local, int count = 1)
    {
        il.Load(local);
        il.LoadValue(Word.Size * count);
        il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sub);
    }

    public static void Store(this ILGenerator il, LocalBuilder local)
    {
        switch (local.LocalIndex)
        {
            case 0:
                il.Emit(OpCodes.Stloc_0);
                break;
            case 1:
                il.Emit(OpCodes.Stloc_1);
                break;
            case 2:
                il.Emit(OpCodes.Stloc_2);
                break;
            case 3:
                il.Emit(OpCodes.Stloc_3);
                break;
            default:
                if (local.LocalIndex < 255)
                {
                    il.Emit(OpCodes.Stloc_S, (byte)local.LocalIndex);
                }
                else
                {
                    il.Emit(OpCodes.Stloc, local.LocalIndex);
                }
                break;
        }
    }

    public static void LoadArray(this ILGenerator il, Span<byte> value)
    {
        // declare il data
        il.Emit(OpCodes.Ldc_I4, value.Length);
        il.Emit(OpCodes.Newarr, typeof(byte));

        // fill the array
        for (int i = 0; i < value.Length; i++)
        {
            il.Emit(OpCodes.Dup);
            il.LoadValue(i);
            il.LoadValue(value[i]);
            il.Emit(OpCodes.Stelem_I1);
        }

        Span<byte> arr = value.ToArray();
        
        // invoke System.MemoryExtensions::AsSpan to get the span
        il.Emit(OpCodes.Call, typeof(MemoryExtensions).GetMethod(nameof(MemoryExtensions.AsSpan), new[] { typeof(byte[]) }));
        // invoke [System.Runtime]System.Span`1<uint8>::op_Implicit(valuetype [System.Runtime]System.Span`1<!0>
        il.Emit(OpCodes.Call, typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", new[] { typeof(Span<byte>) }));

    }

    public static void LoadValue(this ILGenerator il, long value)
    {
        il.Emit(OpCodes.Ldc_I8, value);
    }

    public static void LoadValue(this ILGenerator il, int value)
    {
        switch (value)
        {
            case 0:
                il.Emit(OpCodes.Ldc_I4_0);
                break;
            case 1:
                il.Emit(OpCodes.Ldc_I4_1);
                break;
            case 2:
                il.Emit(OpCodes.Ldc_I4_2);
                break;
            case 3:
                il.Emit(OpCodes.Ldc_I4_3);
                break;
            case 4:
                il.Emit(OpCodes.Ldc_I4_4);
                break;
            case 5:
                il.Emit(OpCodes.Ldc_I4_5);
                break;
            case 6:
                il.Emit(OpCodes.Ldc_I4_6);
                break;
            case 7:
                il.Emit(OpCodes.Ldc_I4_7);
                break;
            case 8:
                il.Emit(OpCodes.Ldc_I4_8);
                break;
            default:
                if (value <= 255)
                    il.Emit(OpCodes.Ldc_I4_S, (byte)value);
                else
                    il.Emit(OpCodes.Ldc_I4, value);
                break;
        }
    }
}
