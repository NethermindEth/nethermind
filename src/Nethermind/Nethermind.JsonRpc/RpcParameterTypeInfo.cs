// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

internal static class RpcParameterTypeInfo
{
    public static JsonTypeInfo? Get(Type type)
    {
        EthereumJsonSerializer.JsonOptions.TryGetTypeInfo(type, out JsonTypeInfo? typeInfo);
        return typeInfo;
    }
}

internal static class RpcParameterTypeInfo<T>
{
    private static readonly JsonTypeInfo<T>? _typeInfo = RpcParameterTypeInfo.Get(typeof(T)) as JsonTypeInfo<T>;

    public static JsonTypeInfo<T>? Get() => _typeInfo;
}
