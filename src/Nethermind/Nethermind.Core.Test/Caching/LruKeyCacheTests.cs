//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using FluentAssertions;
using Nethermind.Core.Caching;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Core.Test.Caching
{
    [TestFixture]
    public class LruKeyCacheTests
    {
        private const int Capacity = 16;

        private readonly Account[] _accounts = new Account[Capacity * 2];
        private readonly Address[] _addresses = new Address[Capacity * 2];

        [SetUp]
        public void Setup()
        {
            for (int i = 0; i < Capacity * 2; i++)
            {
                _accounts[i] = Build.An.Account.WithBalance((UInt256)i).TestObject;
                _addresses[i] = Build.An.Address.FromNumber(i).TestObject;
            }
        }

        [Test]
        public void At_capacity()
        {
            LruKeyCache<Address> cache = new LruKeyCache<Address>(Capacity, "test");
            for (int i = 0; i < Capacity; i++)
            {
                cache.Set(_addresses[i]);
            }

            cache.Get(_addresses[Capacity - 1]).Should().BeTrue();
        }
        
        [Test]
        public void Can_reset()
        {
            LruKeyCache<Address> cache = new LruKeyCache<Address>(Capacity, "test");
            cache.Set(_addresses[0]);
            cache.Set(_addresses[0]);
            cache.Get(_addresses[0]).Should().BeTrue();
        }
        
        [Test]
        public void Can_ask_before_first_set()
        {
            LruKeyCache<Address> cache = new LruKeyCache<Address>(Capacity, "test");
            cache.Get(_addresses[0]).Should().BeFalse();
        }
        
        [Test]
        public void Can_clear()
        {
            LruKeyCache<Address> cache = new LruKeyCache<Address>(Capacity, "test");
            cache.Set(_addresses[0]);
            cache.Clear();
            cache.Get(_addresses[0]).Should().BeFalse();
            cache.Set(_addresses[0]);
            cache.Get(_addresses[0]).Should().BeTrue();
        }
        
        [Test]
        public void Beyond_capacity()
        {
            LruKeyCache<Address> cache = new LruKeyCache<Address>(Capacity, "test");
            for (int i = 0; i < Capacity * 2; i++)
            {
                cache.Set(_addresses[i]);
            }

            cache.Get(_addresses[Capacity]).Should().BeTrue();
        }

        [Test]
        public void Can_delete()
        {
            LruKeyCache<Address> cache = new LruKeyCache<Address>(Capacity, "test");
            cache.Set(_addresses[0]);
            cache.Delete(_addresses[0]);
            cache.Get(_addresses[0]).Should().BeFalse();
        }
    }
}
