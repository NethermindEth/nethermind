// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Nethermind.Evm.Precompiles;

public static class PrecompileHelper
{
    private static readonly ConcurrentDictionary<Type, string> _names = new();

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "The precompile types are statically known, and their Name properties are preserved."
    )]
    public static string GetStaticName(this IPrecompile precompile)
    {
        Type? type = precompile.GetType();
        string name = _names.GetOrAdd(type, t =>
        {
            PropertyInfo? prop = t.GetProperty(nameof(IPrecompile.Name), BindingFlags.Static | BindingFlags.Public);
            return prop is null ? string.Empty : prop.GetValue(null) as string;
        });

        return name;
    }
}
