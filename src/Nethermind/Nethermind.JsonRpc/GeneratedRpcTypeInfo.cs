// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;

namespace Nethermind.JsonRpc;

internal static partial class GeneratedRpcTypeInfo
{
    public static bool TryGet(Type type, [NotNullWhen(true)] out JsonTypeInfo? typeInfo) =>
        RpcGeneratedTypeInfoRegistry.TryGet(type, out typeInfo);

    public static bool TryGet<T>([NotNullWhen(true)] out JsonTypeInfo<T>? typeInfo) =>
        RpcGeneratedTypeInfoRegistry.TryGet(out typeInfo);
}
