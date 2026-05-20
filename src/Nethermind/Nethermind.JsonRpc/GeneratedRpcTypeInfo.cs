// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;

namespace Nethermind.JsonRpc;

internal static partial class GeneratedRpcTypeInfo
{
    public static bool TryGet(Type type, [NotNullWhen(true)] out JsonTypeInfo? typeInfo)
    {
        typeInfo = null;
        TryGetCore(type, ref typeInfo);
        return typeInfo is not null;
    }

    public static bool TryGet<T>([NotNullWhen(true)] out JsonTypeInfo<T>? typeInfo)
    {
        if (TryGet(typeof(T), out JsonTypeInfo? found) && found is JsonTypeInfo<T> typed)
        {
            typeInfo = typed;
            return true;
        }

        typeInfo = null;
        return false;
    }

    static partial void TryGetCore(Type type, ref JsonTypeInfo? typeInfo);
}
