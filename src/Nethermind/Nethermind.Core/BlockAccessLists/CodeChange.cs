// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public struct CodeChange(int blockAccessIndex, byte[] newCode) : IIndexedChange, IEquatable<CodeChange>
{
    public readonly int BlockAccessIndex { get; init; } = blockAccessIndex;

    [JsonConverter(typeof(ByteArrayConverter))]
    public readonly byte[] NewCode { get; init; } = newCode;

    public ValueHash256 NewCodeHash => _hash ??= ValueKeccak.Compute(NewCode);

    private ValueHash256? _hash;

    public bool Equals(CodeChange other) =>
        BlockAccessIndex == other.BlockAccessIndex &&
        NewCodeHash == other.NewCodeHash;

    public override readonly int GetHashCode() =>
        HashCode.Combine(BlockAccessIndex, NewCode);

    public override readonly string ToString() => $"{BlockAccessIndex}:0x{Convert.ToHexString(NewCode ?? [])}";
}
