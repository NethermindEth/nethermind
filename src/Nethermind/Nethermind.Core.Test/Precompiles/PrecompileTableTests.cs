// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Frozen;
using System.Collections.Generic;
using System.Reflection;
using Nethermind.Core.Precompiles;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Precompiles;

public class PrecompileTableTests
{
    private const string Dense = nameof(Dense);
    private const string Sparse = nameof(Sparse);

    /// <summary> An index inside the dense array and one far outside it, as Taiko's L1SLOAD is. </summary>
    private static readonly PrecompileTable<string> Table = new(new Dictionary<AddressAsKey, string>
    {
        [Address.FromNumber((UInt256)0x01)] = Dense,
        [Address.FromNumber((UInt256)0x100)] = Dense,
        [Address.FromNumber((UInt256)0x10001)] = Sparse,
    }.ToFrozenDictionary());

    [TestCase(0x01, Dense)]
    [TestCase(0x100, Dense)]
    [TestCase(0x10001, Sparse)]
    [TestCase(0x02, null)]
    [TestCase(0x101, null)]
    [TestCase(0x10002, null)]
    public void Resolves_by_precompile_index(int number, string? expected)
    {
        Assert.That(Table.TryGetValue(Address.FromNumber((UInt256)number), out string? value), Is.EqualTo(expected is not null));
        Assert.That(value, Is.EqualTo(expected));
    }

    [Test]
    public void Ignores_addresses_that_are_not_low()
    {
        Assert.That(Table.TryGetValue(Address.SystemUser, out string? value), Is.False);
        Assert.That(value, Is.Null);
        Assert.That(() => Table[Address.SystemUser], Throws.TypeOf<KeyNotFoundException>());
    }

    [Test]
    public void Empty_table_resolves_nothing() =>
        Assert.That(new PrecompileTable<string>(FrozenDictionary<AddressAsKey, string>.Empty)
            .TryGetValue(Address.FromNumber((UInt256)0x01), out _), Is.False);

    /// <remarks>
    /// <see cref="Core.Specs.IReleaseSpecExtensions"/> rejects any address without a precompile index, so a
    /// precompile placed outside the low range would silently stop being one.
    /// </remarks>
    [Test]
    public void Every_known_precompile_lives_at_a_low_address()
    {
        foreach (FieldInfo field in typeof(PrecompiledAddresses).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            AddressAsKey address = (AddressAsKey)field.GetValue(null)!;
            Assert.That(address.Value.PrecompileIndexOrNegative(), Is.GreaterThanOrEqualTo(0), field.Name);
        }
    }
}
