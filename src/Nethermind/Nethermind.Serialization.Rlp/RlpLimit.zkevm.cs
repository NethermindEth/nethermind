// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Serialization.Rlp;

/// <remarks>
/// Drops the collection name so the struct holds no references: ILC can then precompute every static
/// <see cref="RlpLimit"/>, and the guest runs no class constructor for the types that declare one.
/// </remarks>
public partial record struct RlpLimit(int Limit)
{
    public RlpLimit(int limit, string typeName, ReadOnlyMemory<char> propertyName = default) : this(limit) { }

    public string CollectionExpression => "";

    public static RlpLimit For<T>(int limit, string propertyName = "") => new(limit);

    public override string ToString() => CollectionExpression;
}
