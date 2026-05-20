// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Nethermind.JsonRpc;

internal static class RpcPayloadTypeInfo
{
    private static readonly ConcurrentDictionary<(JsonSerializerOptions Options, Type Type), JsonTypeInfo> _cache = new();

    public static JsonTypeInfo Get(JsonSerializerOptions options, Type type) =>
        _cache.GetOrAdd((options, type), static key => key.Options.GetTypeInfo(key.Type));
}

internal static class RpcPayloadTypeInfo<T>
{
    private static readonly ConcurrentDictionary<JsonSerializerOptions, JsonTypeInfo<T>> _cache = new();

    public static JsonTypeInfo<T> Get(JsonSerializerOptions options) =>
        _cache.GetOrAdd(options, static options => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));
}
