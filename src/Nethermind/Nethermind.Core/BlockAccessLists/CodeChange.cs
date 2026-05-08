// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public struct CodeChange(int index, byte[] code) : IIndexedChange, IEquatable<CodeChange>
{
    public readonly int Index { get; init; } = index;

    [JsonConverter(typeof(ByteArrayConverter))]
    public readonly byte[] Code { get; init; } = code;

    public ValueHash256 CodeHash => _hash ??= ValueKeccak.Compute(Code);

    private ValueHash256? _hash;

    public bool Equals(CodeChange other) =>
        Index == other.Index &&
        CodeHash == other.CodeHash;

    public override readonly int GetHashCode() =>
        HashCode.Combine(Index, Code);

    public override readonly string ToString() => $"{Index}:0x{Convert.ToHexString(Code ?? [])}";
}
