// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Nethermind.JsonRpc;

/// <summary>Registers generated JSON-RPC payload metadata providers from assemblies that declare RPC modules.</summary>
public static class RpcGeneratedTypeInfoRegistry
{
    private static readonly object _lock = new();
    private static Dictionary<RuntimeTypeHandle, Func<Type, JsonTypeInfo?>> _registrations = [];
    private static Func<Type, JsonTypeInfo?>[] _providers = [];

    /// <summary>Registers a generated provider that can resolve JSON metadata for RPC payload types declared by its assembly.</summary>
    /// <param name="provider">A generated lookup delegate that returns metadata for known types and null otherwise.</param>
    public static void Register(Func<Type, JsonTypeInfo?> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_lock)
        {
            Func<Type, JsonTypeInfo?>[] providers = _providers;
            Func<Type, JsonTypeInfo?>[] updatedProviders = new Func<Type, JsonTypeInfo?>[providers.Length + 1];
            Array.Copy(providers, updatedProviders, providers.Length);
            updatedProviders[providers.Length] = provider;
            Volatile.Write(ref _providers, updatedProviders);
        }
    }

    /// <summary>Registers generated metadata provider entries for the specified RPC payload types.</summary>
    /// <param name="types">The generated payload types known to the provider.</param>
    /// <param name="provider">A generated lookup delegate that returns metadata for registered types.</param>
    public static void Register(Type[] types, Func<Type, JsonTypeInfo?> provider)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(provider);

        lock (_lock)
        {
            Dictionary<RuntimeTypeHandle, Func<Type, JsonTypeInfo?>> registrations = new(_registrations);
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i] ?? throw new ArgumentException("Registered RPC payload types cannot contain null.", nameof(types));
                registrations.TryAdd(type.TypeHandle, provider);
            }

            Volatile.Write(ref _registrations, registrations);
        }
    }

    /// <summary>Registers a generated metadata provider for a statically known RPC payload type.</summary>
    /// <typeparam name="T">The RPC payload type.</typeparam>
    /// <param name="provider">A generated lookup delegate that returns metadata for <typeparamref name="T"/>.</param>
    public static void Register<T>(Func<JsonTypeInfo<T>> provider)
    {
        ArgumentNullException.ThrowIfNull(provider);

        RpcGeneratedTypeInfoRegistry<T>.Register(provider);
    }

    internal static bool TryGet<T>([NotNullWhen(true)] out JsonTypeInfo<T>? typeInfo) =>
        RpcGeneratedTypeInfoRegistry<T>.TryGet(out typeInfo);

    internal static bool TryGet(Type type, out JsonTypeInfo? typeInfo)
    {
        Dictionary<RuntimeTypeHandle, Func<Type, JsonTypeInfo?>> registrations = Volatile.Read(ref _registrations);
        if (registrations.TryGetValue(type.TypeHandle, out Func<Type, JsonTypeInfo?>? provider))
        {
            typeInfo = provider(type);
            if (typeInfo is not null)
            {
                return true;
            }
        }

        Func<Type, JsonTypeInfo?>[] providers = Volatile.Read(ref _providers);
        for (int i = 0; i < providers.Length; i++)
        {
            typeInfo = providers[i](type);
            if (typeInfo is not null)
            {
                return true;
            }
        }

        typeInfo = null;
        return false;
    }
}

internal static class RpcGeneratedTypeInfoRegistry<T>
{
    private static Func<JsonTypeInfo<T>>? _provider;
    private static JsonTypeInfo<T>? _typeInfo;

    public static void Register(Func<JsonTypeInfo<T>> provider) =>
        Volatile.Write(ref _provider, provider);

    public static bool TryGet([NotNullWhen(true)] out JsonTypeInfo<T>? typeInfo)
    {
        JsonTypeInfo<T>? cached = Volatile.Read(ref _typeInfo);
        if (cached is not null)
        {
            typeInfo = cached;
            return true;
        }

        Func<JsonTypeInfo<T>>? provider = Volatile.Read(ref _provider);
        if (provider is null)
        {
            typeInfo = null;
            return false;
        }

        typeInfo = Cache(provider());
        return true;
    }

    private static JsonTypeInfo<T> Cache(JsonTypeInfo<T> generated)
    {
        JsonTypeInfo<T>? existing = Interlocked.CompareExchange(ref _typeInfo, generated, null);
        return existing ?? generated;
    }
}
