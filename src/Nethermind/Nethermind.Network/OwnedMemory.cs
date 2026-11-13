// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;

namespace Nethermind.Network;

internal static class OwnedMemory
{
    /// <summary>
    /// Copies <paramref name="data"/> into pooled memory, for an <see cref="IOwningMessageSerializer{T}"/> to take.
    /// </summary>
    /// <param name="data">The bytes to copy.</param>
    /// <param name="memory">The copy, which lies within the memory of the returned owner.</param>
    public static IMemoryOwner<byte> Copy(ReadOnlySpan<byte> data, out Memory<byte> memory)
    {
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(data.Length);
        memory = owner.Memory[..data.Length];
        data.CopyTo(memory.Span);
        return owner;
    }
}
