// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Store;

namespace Paprika.Tests.Store;

public class DbAddressSetTests
{
    [Test]
    public void Dummy()
    {
        const uint max = 1000;

        using DbAddressSet set = new(DbAddress.Page(max));

        for (uint i = 0; i < max; i++)
        {
            DbAddress addr = DbAddress.Page(i);
            Assert.That(set[addr], Is.True);
            set[addr] = false;
        }

        for (uint i = 0; i < max; i++)
        {
            Assert.That(set[DbAddress.Page(i)], Is.False);
        }
    }

    [Test]
    public void Set_enumeration()
    {
        const uint max = 5;
        using DbAddressSet set = new(DbAddress.Page(max));

        set[DbAddress.Page(0)] = false;
        set[DbAddress.Page(2)] = false;
        set[DbAddress.Page(4)] = false;

        Assert.That(set.EnumerateSet().ToArray(), Is.EquivalentTo(new[] { DbAddress.Page(1), DbAddress.Page(3) }));
    }
}
