// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Nethermind.JsonRpc;

/// <summary>
/// Registers generated JSON-RPC payload metadata providers from assemblies that declare RPC modules.
/// </summary>
public static class RpcGeneratedTypeInfoRegistry
{
    private static readonly object _lock = new();
    private static Dictionary<RuntimeTypeHandle, RegisteredProvider> _registrations = new();
    private static Func<Type, JsonTypeInfo?>[] _providers = [];

    private readonly struct RegisteredProvider(Type type, Func<Type, JsonTypeInfo?> provider)
    {
        public Type Type { get; } = type;

        public Func<Type, JsonTypeInfo?> Provider { get; } = provider;
    }

    /// <summary>
    /// Registers a generated provider that can resolve JSON metadata for RPC payload types declared by its assembly.
    /// </summary>
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

    /// <summary>
    /// Registers generated metadata provider entries for the specified RPC payload types.
    /// </summary>
    /// <param name="types">The generated payload types known to the provider.</param>
    /// <param name="provider">A generated lookup delegate that returns metadata for registered types.</param>
    public static void Register(Type[] types, Func<Type, JsonTypeInfo?> provider)
    {
        ArgumentNullException.ThrowIfNull(types);
        ArgumentNullException.ThrowIfNull(provider);

        lock (_lock)
        {
            Dictionary<RuntimeTypeHandle, RegisteredProvider> registrations = new(_registrations);
            for (int i = 0; i < types.Length; i++)
            {
                Type type = types[i] ?? throw new ArgumentException("Registered RPC payload types cannot contain null.", nameof(types));
                registrations.TryAdd(type.TypeHandle, new RegisteredProvider(type, provider));
            }

            Volatile.Write(ref _registrations, registrations);
        }
    }

    internal static bool TryGet(Type type, out JsonTypeInfo? typeInfo)
    {
        Dictionary<RuntimeTypeHandle, RegisteredProvider> registrations = Volatile.Read(ref _registrations);
        if (registrations.TryGetValue(type.TypeHandle, out RegisteredProvider registration))
        {
            typeInfo = registration.Provider(registration.Type);
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
