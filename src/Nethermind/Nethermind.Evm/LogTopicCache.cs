// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nethermind.Core.Crypto;

namespace Nethermind.Evm;

/// <summary>
/// Shares one <see cref="Hash256"/> per repeated log topic through a fixed-size, direct-mapped table.
/// </summary>
/// <remarks>
/// A slot holds a reference to an immutable instance and is overwritten on a miss, so a racing reader
/// sees either the old or the new instance. A hit is confirmed by comparing all 32 bytes, never by the
/// slot alone, so a reader returns an instance equal to the topic or allocates a fresh one.
/// </remarks>
internal static class LogTopicCache
{
    public const int Count = 1024;
    private const int Mask = Count - 1;

    private static readonly Hash256?[] Entries = new Hash256?[Count];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Hash256 Get(ReadOnlySpan<byte> topic)
    {
        ValueHash256 key = new(topic);
        ref Hash256? slot = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(Entries), key.GetHashCode() & Mask);

        Hash256? cached = Volatile.Read(ref slot);
        if (cached is not null && cached.ValueHash256 == key) return cached;

        Hash256 created = new(in key);
        Volatile.Write(ref slot, created);
        return created;
    }
}
