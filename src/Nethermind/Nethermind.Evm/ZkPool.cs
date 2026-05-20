// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#if ZK_EVM
using System.Collections.Generic;

namespace Nethermind.Evm;

/// <summary>
/// Single-threaded object pool for the ZisK guest. Drop-in replacement for the
/// <see cref="System.Collections.Concurrent.ConcurrentQueue{T}"/> pools used by
/// the EVM call machinery, whose interlocked / segment bookkeeping is pure
/// overhead with a single thread. Exposes the same TryDequeue / Enqueue shape
/// so call sites are unchanged.
/// </summary>
internal sealed class ZkPool<T>
{
    private readonly Stack<T> _items = new();

    public bool TryDequeue(out T item) => _items.TryPop(out item!);

    public void Enqueue(T item) => _items.Push(item);

    public int Count => _items.Count;
}
#endif
