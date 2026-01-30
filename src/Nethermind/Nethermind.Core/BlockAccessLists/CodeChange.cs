
// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct CodeChange(int blockAccessIndex, byte[] newCode) : IEquatable<CodeChange>, IIndexedChange
{
    public int BlockAccessIndex { get; init; } = blockAccessIndex;
    [JsonConverter(typeof(ByteArrayConverter))]
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
}
