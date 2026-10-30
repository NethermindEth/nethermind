// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

public abstract class RlpHashEqualityBase
{
    private Hash256? _hash;

    public Hash256 Hash => _hash ??= CalculateHash();

    private Hash256 CalculateHash()
    {
        scoped KeccakRlpWriter writer = new();
        LengthList lengths = new(stackalloc int[LengthList.DefaultStackallocLength]);
        try
        {
            Encode(ref writer, ref lengths);
        }
        finally
        {
            lengths.Dispose();
        }
        return writer.GetHash();
    }
    protected abstract void Encode(ref KeccakRlpWriter writer, ref LengthList lengths);

    public override bool Equals(object? obj) =>
                obj is RlpHashEqualityBase other && Hash == other.Hash;
    public override int GetHashCode() => Hash.GetHashCode();
}
