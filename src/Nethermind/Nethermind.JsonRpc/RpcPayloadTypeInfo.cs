// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using Nethermind.Serialization.Json;

namespace Nethermind.JsonRpc;

internal static class RpcPayloadTypeInfo
{
    private static readonly ConcurrentDictionary<(JsonSerializerOptions Options, Type Type), JsonTypeInfo> _cache = new();
    private static readonly ConcurrentDictionary<Type, JsonTypeInfo> _canonicalGeneratedCache = new();

    /// <summary>Gets the metadata for a payload's runtime type.</summary>
    /// <remarks>
    /// When no resolver has metadata for the runtime type, as with compiler-generated iterators once reflection is disabled,
    /// the <see cref="object"/> metadata is returned; it dispatches on the runtime type and falls back to the nearest
    /// ancestor that has metadata, such as the collection interface an iterator implements.
    /// </remarks>
    public static JsonTypeInfo Get(JsonSerializerOptions options, Type type) =>
        ReferenceEquals(options, EthereumJsonSerializer.JsonOptions)
            ? GetCanonical(type)
            : _cache.GetOrAdd((options, type), static key => GetOrObject(key.Options, key.Type));

    private static JsonTypeInfo GetOrObject(JsonSerializerOptions options, Type type) =>
        options.TryGetTypeInfo(type, out JsonTypeInfo? typeInfo) ? typeInfo : options.GetTypeInfo(typeof(object));

    private static JsonTypeInfo GetCanonical(Type type)
    {
        if (_canonicalGeneratedCache.TryGetValue(type, out JsonTypeInfo? cached))
        {
            return cached;
        }

        if (RpcGeneratedTypeInfoRegistry.TryGet(type, out JsonTypeInfo? generated))
        {
            return _canonicalGeneratedCache.GetOrAdd(type, generated);
        }

        return GetOrObject(EthereumJsonSerializer.JsonOptions, type);
    }
}

internal static class RpcPayloadTypeInfo<T>
{
    private static readonly ConcurrentDictionary<JsonSerializerOptions, JsonTypeInfo<T>> _cache = new();
    private static JsonTypeInfo<T>? _canonicalGeneratedTypeInfo;

    public static JsonTypeInfo<T> Get(JsonSerializerOptions options) =>
        ReferenceEquals(options, EthereumJsonSerializer.JsonOptions)
            ? GetCanonical()
            : _cache.GetOrAdd(options, static options => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T)));

    private static JsonTypeInfo<T> GetCanonical()
    {
        JsonTypeInfo<T>? cached = Volatile.Read(ref _canonicalGeneratedTypeInfo);
        if (cached is not null)
        {
            return cached;
        }

        if (RpcGeneratedTypeInfoRegistry.TryGet(out JsonTypeInfo<T>? generated))
        {
            return Interlocked.CompareExchange(ref _canonicalGeneratedTypeInfo, generated, null) ?? generated;
        }

        return (JsonTypeInfo<T>)EthereumJsonSerializer.JsonOptions.GetTypeInfo(typeof(T));
    }
}

internal static class RpcPayloadTypeShape
{
    public static bool CanHaveDerivedRuntimeType(Type? type) =>
        type is not null &&
        !type.IsValueType &&
        (!type.IsSealed || type.IsArray && CanHaveDerivedRuntimeType(type.GetElementType()));
}

internal static class RpcPayloadTypeShape<T>
{
    public static readonly bool CanHaveDerivedRuntimeType = RpcPayloadTypeShape.CanHaveDerivedRuntimeType(typeof(T));

    public static readonly bool CanBeStreamable =
        !typeof(T).IsValueType &&
        (CanHaveDerivedRuntimeType || typeof(IStreamableResult).IsAssignableFrom(typeof(T)));
}
