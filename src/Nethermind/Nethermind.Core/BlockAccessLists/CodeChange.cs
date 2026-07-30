// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct CodeChange(uint index, byte[] code) : IIndexedChange, IEquatable<CodeChange>
{
    public uint Index { get; init; } = index;

    [JsonConverter(typeof(ByteArrayConverter))]
    public byte[] Code { get; init; } = code;

    public ValueHash256 CodeHash { get; init; } = code is null ? default : ValueKeccak.Compute(code);

    public bool Equals(CodeChange other) =>
        Index == other.Index &&
        CodeHash == other.CodeHash;

    public override int GetHashCode() =>
        HashCode.Combine(Index, Code);

    public override string ToString() => $"{Index}:0x{Convert.ToHexString(Code ?? [])}";
}
