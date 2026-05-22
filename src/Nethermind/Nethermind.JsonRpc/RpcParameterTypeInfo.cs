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
        if (RpcGeneratedTypeInfoRegistry.TryGet(type, out JsonTypeInfo? generated))
        {
            return generated;
        }

        EthereumJsonSerializer.JsonOptions.TryGetTypeInfo(type, out JsonTypeInfo? typeInfo);
        return typeInfo;
    }
}

internal static class RpcParameterTypeInfo<T>
{
    public static JsonTypeInfo<T>? Get() => RpcParameterTypeInfo.Get(typeof(T)) as JsonTypeInfo<T>;
}
