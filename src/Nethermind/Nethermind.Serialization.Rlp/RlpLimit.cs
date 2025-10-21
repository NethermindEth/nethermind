// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Extensions;

namespace Nethermind.Serialization.Rlp;

public record struct RlpLimit(int Limit, string TypeName = "", ReadOnlyMemory<char> PropertyName = default)
{
    private const int Default = 256 * 1024 * 1024;

    // We shouldn't allocate any single array bigger than 1M
    public static readonly RlpLimit DefaultLimit = new(Default);
    public static readonly RlpLimit Bloom = For<Bloom>(Core.Bloom.ByteLength);
    public static readonly RlpLimit L4 = new(4);
    public static readonly RlpLimit L8 = new(8);
    public static readonly RlpLimit L32 = new(32);
    public static readonly RlpLimit L64 = new(64);
    public static readonly RlpLimit L65 = new(65);
    private string _collectionExpression;

    public RlpLimit() : this(Default) { }

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
    public static RlpLimit For<T>(int limit, string propertyName = "") => new(limit, typeof(T).Name, propertyName.AsMemory());

    public override string ToString() => CollectionExpression;
}
