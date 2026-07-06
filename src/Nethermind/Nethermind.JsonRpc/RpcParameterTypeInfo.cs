// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization.Metadata;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

internal static class RpcParameterTypeInfo
{
    public static JsonTypeInfo? Get(Type type) =>
        EthereumJsonSerializer.JsonRpcRequestOptions.GetTypeInfo(type);
}
