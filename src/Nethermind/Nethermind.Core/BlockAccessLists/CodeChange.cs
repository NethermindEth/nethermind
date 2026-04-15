// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public struct CodeChange(int blockAccessIndex, byte[] newCode) : IIndexedChange
{
    public int BlockAccessIndex {get; init; } = blockAccessIndex;

    [JsonConverter(typeof(ByteArrayConverter))]
    public byte[] NewCode { get; init; } = newCode;

    public ValueHash256 NewCodeHash => _hash ??= ValueKeccak.Compute(NewCode);

    private ValueHash256? _hash;

    public readonly bool Equals(CodeChange other) =>
        BlockAccessIndex == other.BlockAccessIndex &&
        CompareByteArrays(NewCode, other.NewCode);

    public override readonly int GetHashCode() =>
        HashCode.Combine(BlockAccessIndex, NewCode);

    private static bool CompareByteArrays(byte[]? left, byte[]? right) =>
        ReferenceEquals(left, right) || (left is not null && right is not null && left.SequenceEqual(right));

    public override readonly string ToString() => $"{BlockAccessIndex}:0x{Convert.ToHexString(NewCode ?? [])}";
}
