// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nethermind.Evm;

/// <summary>
/// Object pool used by the EVM call machinery. The ZisK guest is single-threaded,
/// so a plain LIFO <see cref="Stack{T}"/> replaces the interlocked / segment
/// bookkeeping of <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/>.
/// </summary>
internal sealed class EvmObjectPool<T>
{
    private readonly Stack<T> _items = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool TryDequeue(out T item) => _items.TryPop(out item!);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Enqueue(T item) => _items.Push(item);

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _items.Count;
    }
}
