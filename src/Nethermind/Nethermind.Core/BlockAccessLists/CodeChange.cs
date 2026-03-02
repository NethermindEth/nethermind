// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json.Serialization;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public readonly record struct CodeChange(ushort BlockAccessIndex, [property: JsonConverter(typeof(ByteArrayConverter))] byte[] NewCode) : IIndexedChange
{
    public bool Equals(CodeChange other) =>
        BlockAccessIndex == other.BlockAccessIndex &&
        CompareByteArrays(NewCode, other.NewCode);

    public override int GetHashCode() =>
        HashCode.Combine(BlockAccessIndex, NewCode);

    private static bool CompareByteArrays(byte[]? left, byte[]? right) =>
        left switch
        {
            null when right == null => true,
            null => false,
            _ when right == null => false,
            _ => left.SequenceEqual(right)
        };

    public override string ToString() => $"{BlockAccessIndex}:0x{Convert.ToHexString(NewCode ?? [])}";
}
