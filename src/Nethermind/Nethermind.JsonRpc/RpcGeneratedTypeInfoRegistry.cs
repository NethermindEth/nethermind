// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization.Metadata;
using System.Threading;

namespace Nethermind.JsonRpc;

/// <summary>
/// Registers generated JSON-RPC payload metadata providers from assemblies that declare RPC modules.
/// </summary>
public static class RpcGeneratedTypeInfoRegistry
{
    private static readonly object _lock = new();
    private static Func<Type, JsonTypeInfo?>[] _providers = [];

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

    internal static bool TryGet(Type type, out JsonTypeInfo? typeInfo)
    {
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
