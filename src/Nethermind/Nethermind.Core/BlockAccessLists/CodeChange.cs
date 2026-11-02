// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;

namespace Nethermind.Core.BlockAccessLists;

public readonly struct CodeChange : IIndexedChange, IEquatable<CodeChange>
{
    public CodeChange(uint index, byte[] code)
    {
        Index = index;
        Code = code;
        CodeHash = code is null ? default : ValueKeccak.Compute(code);
    }

    private CodeChange(uint index, byte[] code, in ValueHash256 codeHash)
    {
        Index = index;
        Code = code;
        CodeHash = codeHash;
        IsAdopted = true;
    }

    /// <summary>A code hash change made by EIP-8298 <c>SETCODEFROM</c>, encoded as <c>[index, b"", codeHash]</c>.</summary>
    /// <param name="index">The block access index of the change.</param>
    /// <param name="codeHash">The adopted code hash.</param>
    /// <param name="code">The adopted bytecode when execution knows it; a decoded change carries none.</param>
    public static CodeChange Adopted(uint index, in ValueHash256 codeHash, byte[]? code = null) => new(index, code ?? [], in codeHash);

    public uint Index { get; init; }

    /// <remarks>
    /// Never encoded for an adopted change. One decoded from a block access list is empty: its bytecode is in the
    /// pre-block state or in a non-adopted change at a lower index, and is resolved through <see cref="CodeHash"/>.
    /// </remarks>
    [JsonConverter(typeof(ByteArrayConverter))]
    public byte[] Code { get; init; }

    public ValueHash256 CodeHash { get; init; }

    /// <summary>Whether <c>SETCODEFROM</c> set the code (EIP-8298), so the change records its hash rather than bytecode.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsAdopted { get; init; }

    public bool Equals(CodeChange other) =>
        Index == other.Index &&
        CodeHash == other.CodeHash &&
        IsAdopted == other.IsAdopted;

    public override int GetHashCode() =>
        HashCode.Combine(Index, Code);

    public override string ToString() => IsAdopted ? $"{Index}:adopted {CodeHash}" : $"{Index}:0x{Convert.ToHexString(Code ?? [])}";
}
