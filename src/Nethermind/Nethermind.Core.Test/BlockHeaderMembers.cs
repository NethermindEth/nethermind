// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Test;

/// <summary>
/// Reflection helpers for tests that guard hand-maintained <see cref="BlockHeader"/> member rosters
/// (e.g. <c>CopyProcessingFields</c>, <c>AuRaBlockHeader.UpgradeFrom</c>) against silently dropping a
/// newly added field.
/// </summary>
public static class BlockHeaderMembers
{
    public static readonly PropertyInfo[] SettableProperties = Array.FindAll(
        typeof(BlockHeader).GetProperties(BindingFlags.Public | BindingFlags.Instance),
        static p => p.SetMethod?.IsPublic is true);

    public static readonly FieldInfo[] PublicFields =
        typeof(BlockHeader).GetFields(BindingFlags.Public | BindingFlags.Instance);

    /// <summary>Assigns a distinct, type-appropriate value to every settable property and public field.</summary>
    public static void FillWithDistinctValues(BlockHeader header)
    {
        int seed = 1;
        foreach (PropertyInfo property in SettableProperties) property.SetValue(header, CreateValue(property.PropertyType, seed++));
        foreach (FieldInfo field in PublicFields) field.SetValue(header, CreateValue(field.FieldType, seed++));
    }

    private static object CreateValue(Type type, int seed)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(Hash256)) return Keccak.Compute(seed.ToString());
        if (type == typeof(Address)) return new Address(Keccak.Compute(seed.ToString()).Bytes[..20]);
        if (type == typeof(Bloom))
        {
            Bloom bloom = new();
            bloom.Set(Keccak.Compute(seed.ToString()).BytesToArray());
            return bloom;
        }
        if (type == typeof(byte[])) return new[] { (byte)seed };
        if (type == typeof(long)) return (long)seed;
        if (type == typeof(ulong)) return (ulong)seed;
        if (type == typeof(UInt256)) return (UInt256)seed;
        if (type == typeof(bool)) return true;
        throw new NotSupportedException($"Add a sample value for new BlockHeader member type {type.Name}.");
    }
}
