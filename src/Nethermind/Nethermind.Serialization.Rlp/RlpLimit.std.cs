// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;

namespace Nethermind.Serialization.Rlp;

public partial record struct RlpLimit(int Limit, string TypeName = "", ReadOnlyMemory<char> PropertyName = default)
{
    public string CollectionExpression
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => field ??= GenerateCollectionExpression();
    }

    private string GenerateCollectionExpression() =>
        PropertyName.IsEmpty
            ? TypeName
            : $"{TypeName}.{PropertyName}";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RlpLimit For<T>(int limit, string propertyName = "") => new(limit, typeof(T).Name, propertyName.AsMemory());

    public override string ToString() => CollectionExpression;
}
