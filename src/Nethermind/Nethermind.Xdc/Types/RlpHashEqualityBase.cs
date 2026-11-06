// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;

namespace Nethermind.Xdc.Types;

public abstract class RlpHashEqualityBase
{
    private Hash256? _hash;

    public Hash256 Hash => _hash ??= CalculateHash();

    protected abstract Hash256 CalculateHash();

    public override bool Equals(object? obj) =>
                obj is RlpHashEqualityBase other && Hash == other.Hash;
    public override int GetHashCode() => Hash.GetHashCode();
}
