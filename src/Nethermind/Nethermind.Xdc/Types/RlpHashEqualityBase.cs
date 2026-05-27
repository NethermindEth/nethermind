// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using System;

namespace Nethermind.Xdc.Types;

public abstract class RlpHashEqualityBase : IEquatable<RlpHashEqualityBase>
{
    private Hash256? _hash;

    public Hash256 Hash => _hash ??= CalculateHash();

    private Hash256 CalculateHash()
    {
        KeccakRlpStream stream = new();
        Encode(stream);
        return stream.GetHash();
    }
    protected abstract void Encode(KeccakRlpStream stream);

    public bool Equals(RlpHashEqualityBase? other) =>
        other is not null &&
        other.GetType() == GetType() &&
        Hash == other.Hash;

    public override bool Equals(object? obj) => Equals(obj as RlpHashEqualityBase);

    public override int GetHashCode() => Hash.GetHashCode();
}
