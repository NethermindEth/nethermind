
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Extensions;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct CodeChange(ushort blockAccessIndex, byte[] newCode) : IEquatable<CodeChange>, IIndexedChange
{
    public ushort BlockAccessIndex { get; init; } = blockAccessIndex;
    public byte[] NewCode { get; init; } = newCode;

    public readonly bool Equals(CodeChange other) =>
        BlockAccessIndex == other.BlockAccessIndex &&
        CompareByteArrays(NewCode, other.NewCode);

    public override readonly bool Equals(object? obj) =>
        obj is CodeChange other && Equals(other);

    public override readonly int GetHashCode() =>
        HashCode.Combine(BlockAccessIndex, NewCode);

    private static bool CompareByteArrays(byte[]? left, byte[]? right) =>
        left switch
        {
            null when right == null => true,
            null => false,
            _ when right == null => false,
            _ => left.SequenceEqual(right)
        };

    public static bool operator ==(CodeChange left, CodeChange right) =>
        left.Equals(right);

    public static bool operator !=(CodeChange left, CodeChange right) =>
        !(left == right);

    public override readonly string? ToString()
        => $"{BlockAccessIndex}, 0x{Bytes.ToHexString(NewCode)}";
}
