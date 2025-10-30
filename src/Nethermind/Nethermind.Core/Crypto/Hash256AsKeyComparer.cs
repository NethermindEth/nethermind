// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Collections;

namespace Nethermind.Core.Crypto;

public class Hash256AsKeyComparer : IEqualityComparer<ComparableBox<Hash256>>, IAlternateEqualityComparer<ValueHash256, ComparableBox<Hash256>>
{
    public static Hash256AsKeyComparer Instance { get; } = new();

    private Hash256AsKeyComparer() { }

    public bool Equals(ComparableBox<Hash256> x, ComparableBox<Hash256> y) => x.Equals(y);

    public int GetHashCode(ComparableBox<Hash256> obj) => obj.GetHashCode();

    public bool Equals(ValueHash256 alternate, ComparableBox<Hash256> other) => other.Value is not null && alternate.Equals(in other.Value.ValueHash256);

    public int GetHashCode(ValueHash256 alternate) => alternate.GetHashCode();

    public ComparableBox<Hash256> Create(ValueHash256 alternate) => alternate.ToCommitment();
}
