// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;

namespace Nethermind.Core.Buffers;

/// <summary>
/// The default <see cref="IRefCountingMemoryProvider"/>: rents from <see cref="ArrayPool{T}.Shared"/>
/// and returns the buffer to it on the last release.
/// </summary>
public sealed class PooledRefCountingMemoryProvider : IRefCountingMemoryProvider
{
    public static readonly PooledRefCountingMemoryProvider Instance = new();

    private PooledRefCountingMemoryProvider() { }

    public RefCountingMemory Rent(int length) => RefCountingMemory.Owning(ArrayPool<byte>.Shared.Rent(length), length);
}
