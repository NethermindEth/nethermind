// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using Nethermind.Core;
using Nethermind.Core.Utils;

namespace Nethermind.State.Flat;

/// <summary>
/// A ref-counted wrapper around pooled RLP bytes for trie nodes.
/// The byte array is rented from <see cref="ArrayPool{T}.Shared"/> and returned when the ref count reaches zero.
/// </summary>
public sealed class RefCounterTrieNodeRlp : RefCountingDisposable
{
    private readonly byte[] _pooledArray;
    private readonly int _length;

    private RefCounterTrieNodeRlp(byte[] pooledArray, int length) : base()
    {
        _pooledArray = pooledArray;
        _length = length;
    }

    /// <summary>
    /// Gets a span over the RLP data.
    /// </summary>
    public ReadOnlySpan<byte> Span => _pooledArray.AsSpan(0, _length);

    /// <summary>
    /// Gets the length of the RLP data.
    /// </summary>
    public int Length => _length;

    /// <summary>
    /// Gets the estimated memory size of this instance for cache tracking.
    /// </summary>
    public long MemorySize => MemorySizes.SmallObjectOverhead + MemorySizes.ArrayOverhead + _pooledArray.Length;

    /// <summary>
    /// Creates a copy of the RLP data as a new byte array.
    /// </summary>
    public byte[] ToArray() => Span.ToArray();

    /// <summary>
    /// Tries to acquire a lease on this RLP data. Returns true if successful, false if already disposed.
    /// Caller must call Dispose() to release the lease when done.
    /// </summary>
    public new bool TryAcquireLease() => base.TryAcquireLease();

    /// <summary>
    /// Creates a new <see cref="RefCounterTrieNodeRlp"/> by renting an array and copying the RLP data.
    /// </summary>
    /// <param name="rlp">The RLP data to copy.</param>
    /// <returns>A new <see cref="RefCounterTrieNodeRlp"/> instance with ref count of 1.</returns>
    public static RefCounterTrieNodeRlp CreateFromRlp(ReadOnlySpan<byte> rlp)
    {
        byte[] pooledArray = ArrayPool<byte>.Shared.Rent(rlp.Length);
        rlp.CopyTo(pooledArray);
        return new RefCounterTrieNodeRlp(pooledArray, rlp.Length);
    }

    protected override void CleanUp() =>
        ArrayPool<byte>.Shared.Return(_pooledArray);
}
