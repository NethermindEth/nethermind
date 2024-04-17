// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.CodeAnalysis.IL;
using Sigil;
using System;
using System.Reflection;
using System.Reflection.Emit;

namespace Nethermind.Evm.IL;

/// <summary>
/// Extensions for <see cref="ILGenerator"/>.
/// </summary>
static class EmitExtensions
{
    public static void Load<T>(this Emit<T> il, Local local, FieldInfo wordField)
    {
        if (local.LocalType != typeof(Word*))
        {
            throw new ArgumentException($"Only Word* can be used. This variable is of type {local.LocalType}");
        }

        if (wordField.DeclaringType != typeof(Word))
        {
            throw new ArgumentException($"Only Word fields can be used. This field is declared for {wordField.DeclaringType}");
        }

        il.LoadLocal(local);
        il.LoadField(wordField);
    }

    public static void CleanWord<T>(this Emit<T> il, Local local)
    {
        if (local.LocalType != typeof(Word*))
        {
            throw new ArgumentException(
                $"Only {nameof(Word)} pointers are supported. The passed local was type of {local.LocalType}.");
        }

        il.LoadLocal(local);
        il.InitializeObject(typeof(Word));
    }

    /// <summary>
    /// Advances the stack one word up.
    /// </summary>
    public static void StackPush<T>(this Emit<T> il, Local local)
    {
        il.LoadLocal(local);
        il.LoadConstant(Word.Size);
        il.Convert<int>();
        il.Add();
        il.StoreLocal(local);
    }

    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop<T>(this Emit<T> il, Local local, int count = 1)
    {
        il.LoadLocal(local);
        il.LoadConstant(Word.Size * count);
        il.Convert<int>();
        il.Subtract();
        il.StoreLocal(local);
    }

    /// <summary>
    /// Moves the stack <paramref name="count"/> words down.
    /// </summary>
    public static void StackPop<T>(this Emit<T> il, Local local, Local count)
    {
        il.LoadLocal(local);
        il.LoadConstant(Word.Size);
        il.LoadLocal(count);
        il.Multiply();
        il.Convert<int>();
        il.Subtract();
        il.StoreLocal(local);
    }

    public static void WhileBranch<T>(this Emit<T> il, Local local, Action<Emit<T>> action)
    {
        var start = il.DefineLabel();
        var end = il.DefineLabel();

        il.MarkLabel(start);
        il.LoadLocal(local);
        il.BranchIfFalse(end);

        action(il);

        il.Branch(start);
        il.MarkLabel(end);
    }

    public static void ForBranch<T>(this Emit<T> il, Local count, Action<Emit<T>, Local> action)
    {
        var start = il.DefineLabel();
        var end = il.DefineLabel();

        // declare indexer
        var i = il.DeclareLocal<int>();
        il.LoadLocal(i);
        il.LoadConstant(0);
        il.StoreLocal(i);

        il.MarkLabel(start);
        il.LoadLocal(i);
        il.LoadLocal(count);
        il.BranchIfGreater(end);

        action(il, i);

        il.LoadLocal(i);
        il.LoadConstant(1);
        il.Add();
        il.StoreLocal(i);

        il.MarkLabel(start);
        il.Branch(start);
    }

    /// <summary>
    /// Loads the previous EVM stack value on top of .NET stack.
    /// </summary>
    public static void StackLoadPrevious<T>(this Emit<T> il, Local local, int count = 1)
    {
        il.LoadLocal(local);
        il.LoadConstant(Word.Size * count);
        il.Convert<int>();
        il.Subtract();
    }

    public static void LoadArray<T>(this Emit<T> il, ReadOnlySpan<byte> value)
    {

        il.LoadConstant(value.Length);
        il.NewArray<byte>();

        for (int i = 0; i < value.Length; i++)
        {
            il.Duplicate();
            il.LoadConstant(i);
            il.LoadConstant(value[i]);
            il.StoreElement<byte>();
        }

        il.Call(typeof(MemoryExtensions).GetMethod(nameof(MemoryExtensions.AsSpan), new[] { typeof(byte[]) }));
        il.Call(typeof(ReadOnlySpan<byte>).GetMethod("op_Implicit", new[] { typeof(Span<byte>) }));

    }
}
