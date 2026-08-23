// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nethermind.Evm.GasPolicy;

namespace Nethermind.Evm;

/// <summary>Parent-frame stack for <see cref="VirtualMachine{TGasPolicy}"/>.</summary>
/// <remarks>
/// <see cref="System.Collections.Generic.Stack{T}"/> over a reference type runs shared generic code,
/// where the backing array is statically <c>__Canon[]</c>. The JIT cannot prove the store type-exact, so
/// every push lowers to an out-of-line covariance helper on the per-frame path. Storing through a byref
/// keeps the GC write barrier and drops that check. Call depth is bounded, so capacity is fixed at
/// construction and there is no growth path.
/// </remarks>
internal sealed class VmStateStack<TGasPolicy>(int capacity)
    where TGasPolicy : struct, IGasPolicy<TGasPolicy>
{
    private readonly VmState<TGasPolicy>?[] _items = new VmState<TGasPolicy>?[capacity];
    private int _count;

    public void Push(VmState<TGasPolicy> state)
    {
        VmState<TGasPolicy>?[] items = _items;
        int count = _count;
        if ((uint)count >= (uint)items.Length)
        {
            ThrowFull();
        }

        Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(items), (uint)count) = state;
        _count = count + 1;
    }

    public VmState<TGasPolicy> Pop()
    {
        VmState<TGasPolicy>?[] items = _items;
        int count = _count - 1;
        // An empty stack wraps to uint.MaxValue, so one comparison covers both ends.
        if ((uint)count >= (uint)items.Length)
        {
            ThrowEmpty();
        }

        ref VmState<TGasPolicy>? slot = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(items), (uint)count);
        VmState<TGasPolicy> state = slot!;
        // Don't keep a popped frame reachable through the stack.
        slot = null;
        _count = count;
        return state;
    }

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowFull() =>
        throw new InvalidOperationException("EVM call frame stack exceeded its bounded depth.");

    [DoesNotReturn, StackTraceHidden]
    private static void ThrowEmpty() =>
        throw new InvalidOperationException("EVM call frame stack was popped while empty.");
}
