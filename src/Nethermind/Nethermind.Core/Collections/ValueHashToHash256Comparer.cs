// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core.Crypto;

namespace Nethermind.Core.Collections;

public class ValueHashToHash256Comparer : IEqualityComparer<Hash256AsKey>, IAlternateEqualityComparer<ValueHash256, Hash256AsKey>
{
    public static ValueHashToHash256Comparer Instance { get; } = new();
    public int GetHashCode([DisallowNull] Hash256AsKey obj) => obj.Value.ValueHash256.GetHashCode();

    public bool Equals(Hash256AsKey x, Hash256AsKey y) => x.Value == y.Value;

    public Hash256AsKey Create(ValueHash256 alternate) => new Hash256(in alternate);

    public bool Equals(ValueHash256 alternate, Hash256AsKey other)
        => alternate == other.Value.ValueHash256;

    public int GetHashCode(ValueHash256 alternate)
        => alternate.GetHashCode();
}
