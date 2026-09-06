// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test;

/// <summary>
/// Reflection helpers for tests guarding hand-maintained <see cref="BlockHeader"/> member rosters
/// (<c>CopyProcessingFields</c>, <c>AuRaBlockHeader.UpgradeFrom</c>) against silently dropping a new field.
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

    /// <summary>Asserts <paramref name="copy"/> reproduces every settable member of <paramref name="source"/>, except those named in <paramref name="except"/>.</summary>
    public static void AssertCarriesAllMembers(BlockHeader source, BlockHeader copy, params string[] except)
    {
        HashSet<string> skip = [.. except];
        using (Assert.EnterMultipleScope())
        {
            foreach (PropertyInfo property in SettableProperties)
            {
                if (skip.Contains(property.Name)) continue;
                Assert.That(property.GetValue(copy), Is.EqualTo(property.GetValue(source)), property.Name);
            }

            foreach (FieldInfo field in PublicFields)
            {
                if (skip.Contains(field.Name)) continue;
                Assert.That(field.GetValue(copy), Is.EqualTo(field.GetValue(source)), field.Name);
            }
        }
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
