// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;
using Paprika.Store;

namespace Paprika.Tests.Store;

public abstract class DbAddressListTests<TList> where TList : struct, DbAddressList.IDbAddressList
{
    [Test]
    public void Test()
    {
        const int seed = 13;

        TList list = default;
        Random random = new(seed);

        for (int i = 0; i < TList.Length; i++)
        {
            list[i] = NextAddress();
        }

        random = new(seed);
        for (int i = 0; i < TList.Length; i++)
        {
            Assert.That(list[i], Is.EqualTo(NextAddress()));
        }

        Assert.That(TList.Length, Is.GreaterThan(0));

        return;

        DbAddress NextAddress() => DbAddress.Page((uint)random.Next(0, (int)DbAddressList.Max.Raw));
    }
}

[TestFixture]
public class DbAddressListTestsOf4 : DbAddressListTests<DbAddressList.Of4>;

[TestFixture]
public class DbAddressListTestsOf16 : DbAddressListTests<DbAddressList.Of16>;

[TestFixture]
public class DbAddressListTestsOfOf256 : DbAddressListTests<DbAddressList.Of256>;

[TestFixture]
public class DbAddressListTestsOfOf1024 : DbAddressListTests<DbAddressList.Of1024>;
