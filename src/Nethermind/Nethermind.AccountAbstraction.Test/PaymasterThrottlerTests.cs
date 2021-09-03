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
// 

using System;
using System.Threading;
using FluentAssertions;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using NUnit.Framework;

namespace Nethermind.AccountAbstraction.Test
{
    [TestFixture]
    public class PaymasterThrottlerTests
    {
        private Random rand;
        
        private PaymasterThrottler _paymasterThrottler;

        private Address[] _addresses =
        {
            new("0xab07ae311586661c77f35c31a62dc05e3fd0fe18"),
            new("0xb7c255fa6f20564f2ceea12991330c0bc6f4d036"),
            new("0xc3b690a32e3ee1e243832980ab521d8328d3ce57"),
            new("0xd86af6bcfe83dabb7286cc59a4477a7c2e39e00a"),
            new("0xe6003ab48efe095b78a044ee57cb4826702e4102"),
            new("0xf377bc8842565c3d6ba3fb015ea2cb3036206384")
        }; 
        
        [SetUp]
        public void SetUp()
        {
            // Modifying internal timer interval so that internal maps are updated every 2 secs
            _paymasterThrottler = new PaymasterThrottler(0, 0 ,2);
            rand = new Random();
        }

        [Test]
        public void Can_read_and_increment_internal_maps()
        {
            int index;
            
            for (int i = 0; i < 1000; i++)
            {
                index = rand.Next(0, _addresses.Length);
                _paymasterThrottler.IncrementOpsSeen(_addresses[index]);
                _paymasterThrottler.IncrementOpsIncluded(_addresses[index]);
            }

            foreach (Address addr in _addresses)
            {
                _paymasterThrottler.GetPaymasterOpsSeen(addr)
                    .Should().Be(_paymasterThrottler.GetPaymasterOpsIncluded(addr));
            }
        }

        [Test]
        public void Internal_maps_are_correctly_updated()
        {
            const uint HourSpan = 24;
            
            for (int i = 0; i < 100; i++)
            {
                _paymasterThrottler.IncrementOpsSeen(_addresses[0]);
                _paymasterThrottler.IncrementOpsIncluded(_addresses[1]);
            }

            _paymasterThrottler.IncrementOpsSeen(_addresses[2]);
            
             /*
              100 - 100 // 24 = 96
              96 - 96 // 24 = 92
              92 - 92 // 24 = 89
              89 - 89 // 24 = 86
              */

             uint opsSeenBeforeUpdate;
             uint opsIncludedBeforeUpdate;
             const uint PERIODS = 10;
             
             for (int i = 0; i < PERIODS; i++)
             {
                 opsSeenBeforeUpdate = _paymasterThrottler.GetPaymasterOpsSeen(_addresses[0]);
                 opsIncludedBeforeUpdate = _paymasterThrottler.GetPaymasterOpsIncluded(_addresses[1]);

                 Thread.Sleep(2050);

                 _paymasterThrottler.GetPaymasterOpsSeen(_addresses[0])
                     .Should().Be(opsSeenBeforeUpdate - FloorDivision(opsSeenBeforeUpdate, HourSpan));
                 _paymasterThrottler.GetPaymasterOpsIncluded(_addresses[1])
                     .Should().Be(opsIncludedBeforeUpdate - FloorDivision(opsIncludedBeforeUpdate, HourSpan));
                 _paymasterThrottler.GetPaymasterOpsSeen(_addresses[2])
                     .Should().Be(1);
             }
        }
        
        [Test]
        public void Internal_maps_are_randomly_incremented_and_correctly_updated()
        {
            const uint HourSpan = 24;
            int index;
            
            for (int i = 0; i < 10000; i++)
            {
                index = rand.Next(0, _addresses.Length - 1);
                if (i % 2 == 0) _paymasterThrottler.IncrementOpsSeen(_addresses[index]);
                else _paymasterThrottler.IncrementOpsIncluded(_addresses[index]);
            }
            
            uint opsSeenBeforeUpdate;
            uint opsIncludedBeforeUpdate;

            foreach (Address addr in _addresses)
            {
                opsSeenBeforeUpdate = _paymasterThrottler.GetPaymasterOpsSeen(addr);
                opsIncludedBeforeUpdate = _paymasterThrottler.GetPaymasterOpsIncluded(addr);

                Thread.Sleep(2050);

                _paymasterThrottler.GetPaymasterOpsSeen(addr)
                    .Should().Be(opsSeenBeforeUpdate - FloorDivision(opsSeenBeforeUpdate, HourSpan));
                _paymasterThrottler.GetPaymasterOpsIncluded(addr)
                    .Should().Be(opsIncludedBeforeUpdate - FloorDivision(opsIncludedBeforeUpdate, HourSpan));
            }
        }
        
        private uint FloorDivision(uint dividend, uint divisor)
        {
            if (divisor == 0) throw new Exception("PaymasterThrottler: Divisor cannot be == 0");

            uint remainder = dividend % divisor;
            return (dividend - remainder) / divisor;
        }
    }
}
