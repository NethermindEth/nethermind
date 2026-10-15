// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt.Persistence;

/// <summary>
/// Persisted <see cref="PbtColumns.Storages"/> key: the address hash, then the zone byte, then the rest of the tree
/// key, so one account's header and overflow slots form a single contiguous range.
/// </summary>
/// <remarks>
/// The tree key is <c>[zone][addressHash][rest]</c>; the persisted form rotates its first 33 bytes. Lengths are
/// unchanged (34 bytes for header slots, 66 for overflow slots).
/// </remarks>
public static class PbtStorageKeyLayout
{
    private const int ZoneOffset = ValueHash256.MemorySize;
    private const int RestOffset = ZoneOffset + 1;

    public static ReadOnlySpan<byte> Encode<TKey>(in TKey key, Span<byte> destination) where TKey : struct, IPbtKey<TKey>
    {
        ReadOnlySpan<byte> bytes = key.Bytes;
        bytes.Slice(1, ValueHash256.MemorySize).CopyTo(destination);
        destination[ZoneOffset] = bytes[0];
        bytes[RestOffset..].CopyTo(destination[RestOffset..]);
        return destination[..bytes.Length];
    }
}
