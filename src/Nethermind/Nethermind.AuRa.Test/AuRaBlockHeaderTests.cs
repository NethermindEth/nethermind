// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Nethermind.Consensus.AuRa;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.AuRa.Test;

public class AuRaBlockHeaderTests
{
    /// <summary>Guards the hand-maintained member roster in <see cref="AuRaBlockHeader.UpgradeFrom"/>.</summary>
    [Test]
    public void UpgradeFrom_copies_every_settable_BlockHeader_member()
    {
        BlockHeader src = new(
            Keccak.Compute("parent"), Keccak.Compute("uncles"), Address.Zero, 1, 2, 3, 4, [5]);

        int seed = 1;
        PropertyInfo[] properties = Array.FindAll(
            typeof(BlockHeader).GetProperties(BindingFlags.Public | BindingFlags.Instance),
            static p => p.SetMethod?.IsPublic is true);
        FieldInfo[] fields = typeof(BlockHeader).GetFields(BindingFlags.Public | BindingFlags.Instance);

        foreach (PropertyInfo property in properties) property.SetValue(src, CreateValue(property.PropertyType, seed++));
        foreach (FieldInfo field in fields) field.SetValue(src, CreateValue(field.FieldType, seed++));

        AuRaBlockHeader upgraded = AuRaBlockHeader.UpgradeFrom(src);

        using (Assert.EnterMultipleScope())
        {
            foreach (PropertyInfo property in properties)
            {
                Assert.That(property.GetValue(upgraded), Is.EqualTo(property.GetValue(src)), property.Name);
            }

            foreach (FieldInfo field in fields)
            {
                Assert.That(field.GetValue(upgraded), Is.EqualTo(field.GetValue(src)), field.Name);
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
