// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Nethermind.Core.Extensions;
using Nethermind.Int256;

namespace Nethermind.Core.BlockAccessLists;

/// <summary>
/// Per-slot changes from a decoded BAL. Backed by a <see cref="StorageChange"/> array sorted by
/// <see cref="StorageChange.Index"/> (the decoder validates ordering on the way in), so
/// <see cref="Get(uint, Span{byte})"/> can binary-search via <see cref="System.MemoryExtensions"/>.
/// </summary>
public class ReadOnlySlotChanges(UInt256 key, StorageChange[] changes) : IEquatable<ReadOnlySlotChanges>
{
    public UInt256 Key { get; } = key;

    [JsonConverter(typeof(StorageChangesByIndexConverter))]
    public StorageChange[] Changes { get; } = changes;

    public bool Equals(ReadOnlySlotChanges? other)
        => other is not null && Key.Equals(other.Key) && Changes.SequenceEqual(other.Changes);

    public override bool Equals(object? obj) => obj is ReadOnlySlotChanges other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Key, Changes.Length);

    public override string ToString() => $"{Key}:[{string.Join(", ", Changes)}]";

    public ReadOnlySlotChanges(UInt256 key) : this(key, []) { }

    /// <summary>Storage value as visible at the start of <paramref name="blockAccessIndex"/>
    /// (i.e. last change strictly before the index). Writes up to 32 big-endian bytes into
    /// <paramref name="buffer"/> (which must be at least 32 long) and returns a leading-zero-stripped
    /// slice. Returns an empty span when no entry is recorded before the index.</summary>
    /// <remarks>
    /// Takes a caller-owned buffer instead of allocating: in the parallel BAL path this is on
    /// the SLOAD hot path and was previously allocating one trimmed byte[] per call. The buffer
    /// must outlive the returned span — typical caller is a per-instance field on
    /// <see cref="Nethermind.State.BlockAccessListBasedWorldState"/>.
    /// </remarks>
    public ReadOnlySpan<byte> Get(uint blockAccessIndex, Span<byte> buffer)
        => TryGetLastBefore(blockAccessIndex, buffer, out ReadOnlySpan<byte> result) ? result : ReadOnlySpan<byte>.Empty;

    /// <summary>Like <see cref="Get(uint, Span{byte})"/> but distinguishes "no entry before the
    /// index" (returns <c>false</c>; caller can fall through to a parent-state reader) from
    /// "entry present whose value happens to be zero" (returns <c>true</c> with an empty span).</summary>
    public bool TryGetLastBefore(uint blockAccessIndex, Span<byte> buffer, out ReadOnlySpan<byte> result)
    {
        ReadOnlySpan<StorageChange> span = Changes;
        int idx = span.BinarySearch(new IndexKey<StorageChange>(blockAccessIndex));
        // Whether found exactly or not, idx (or ~idx) is the position of the first entry with
        // Index >= blockAccessIndex. The last strictly-before entry is one step earlier.
        int lastBefore = (idx >= 0 ? idx : ~idx) - 1;
        if (lastBefore < 0)
        {
            result = ReadOnlySpan<byte>.Empty;
            return false;
        }

        // StorageChange.Value is already in big-endian wire form (see ctor); reinterpret the
        // 32-byte EvmWord vector as a byte span and copy into the caller's buffer.
        EvmWord value = span[lastBefore].Value;
        ReadOnlySpan<byte> valueBytes = MemoryMarshal.CreateReadOnlySpan(
            ref Unsafe.As<EvmWord, byte>(ref value), 32);
        valueBytes.CopyTo(buffer);
        result = buffer[..32].WithoutLeadingZeros();
        return true;
    }
}
