// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

public record struct RlpLimit(int Limit, string TypeName, ReadOnlyMemory<char> PropertyName)
{
    // We shouldn't allocate any single array bigger than 1M
    public static readonly RlpLimit DefaultLimit = new((int)256.KiB(), "", ReadOnlyMemory<char>.Empty);
    public static readonly RlpLimit Bloom = For<Bloom>("", Core.Bloom.ByteLength);
    public static readonly RlpLimit L4 = new(4, "", ReadOnlyMemory<char>.Empty);
    public static readonly RlpLimit L8 = new(8, "", ReadOnlyMemory<char>.Empty);
    public static readonly RlpLimit L32 = new(32, "", ReadOnlyMemory<char>.Empty);
    public static readonly RlpLimit L64 = new(64, "", ReadOnlyMemory<char>.Empty);
    public static readonly RlpLimit L65 = new(65, "", ReadOnlyMemory<char>.Empty);

    private string _collectionExpression;

    public string CollectionExpression
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _collectionExpression ??= GenerateCollectionExpression();
    }

    private string GenerateCollectionExpression() =>
        PropertyName.IsEmpty
            ? TypeName
            : $"{TypeName}.{PropertyName}";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static RlpLimit For<T>(string propertyName, int limit) => new(limit, typeof(T).Name, propertyName.AsMemory());

    public override string ToString() => CollectionExpression;
}
