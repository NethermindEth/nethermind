// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core.Buffers;

namespace Nethermind.Trie;

#pragma warning disable NETH003 // Build variant: only one of TrackingCappedArrayPool.std.cs / TrackingCappedArrayPool.zkevm.cs is compiled per build
/// <summary>
/// Allocating buffer source for the zkVM guest &mdash; see the std counterpart for the tracking pool.
/// </summary>
/// <remarks>
/// The guest never collects, so a fresh array costs a bump-pointer allocation plus a DMA clear,
/// while the std pool's bookkeeping (a queue of rentals, a power-of-two bucket lookup and a
/// software <c>Log2</c> per rent and return) is all main-loop instructions. Allocating outright is
/// measurably cheaper here.
/// </remarks>
public sealed class TrackingCappedArrayPool : ICappedArrayPool, IDisposable
{
    // The std counterpart's parameters size and shape its rental tracking; the guest keeps none.
    public TrackingCappedArrayPool(int initialCapacity, ArrayPool<byte>? arrayPool = null, bool canBeParallel = true) { }

    public TrackingCappedArrayPool() { }

    public CappedArray<byte> Rent(int size) => size == 0 ? CappedArray<byte>.Empty : new(new byte[size], size);

    public void Return(in CappedArray<byte> buffer) { }

    public void Dispose() { }
}
