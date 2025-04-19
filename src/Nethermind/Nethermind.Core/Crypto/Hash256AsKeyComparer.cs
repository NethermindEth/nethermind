// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Core.Crypto;

public class Hash256AsKeyComparer : IEqualityComparer<Hash256AsKey>, IAlternateEqualityComparer<ValueHash256, Hash256AsKey>
{
    public static Hash256AsKeyComparer Instance { get; } = new();

    private Hash256AsKeyComparer() { }

    public bool Equals(Hash256AsKey x, Hash256AsKey y) => x.Equals(y);

    public int GetHashCode(Hash256AsKey obj) => obj.GetHashCode();

    public bool Equals(ValueHash256 alternate, Hash256AsKey other) => alternate.Equals(in other.Value.ValueHash256);

    public int GetHashCode(ValueHash256 alternate) => alternate.GetHashCode();

    public Hash256AsKey Create(ValueHash256 alternate) => alternate.ToCommitment();
}
