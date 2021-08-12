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
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Resettables;
using Nethermind.Core.Test.Builders;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    public class EvmStateTests
    {
        [Test]
        public void Top_level_continuations_are_not_valid()
        {
            Assert.Throws<InvalidOperationException>(
                () => _ = CreateEvmState(true));
        }

        [Test]
        public void Things_are_cold_to_start_with()
        {
            EvmState evmState = CreateEvmState();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            evmState.IsCold(TestItem.AddressA).Should().BeTrue();
            evmState.IsCold(storageCell).Should().BeTrue();
        }
        
        [Test]
        public void Can_warm_address_up_twice()
        {
            EvmState evmState = CreateEvmState();
            Address address = TestItem.AddressA;
            evmState.WarmUp(address);
            evmState.WarmUp(address);
            evmState.IsCold(address).Should().BeFalse();
        }
        
        [Test]
        public void Can_warm_up_many()
        {
            EvmState evmState = CreateEvmState();
            for (int i = 0; i < TestItem.Addresses.Length; i++)
            {
                evmState.WarmUp(TestItem.Addresses[i]);
                evmState.WarmUp(new StorageCell(TestItem.Addresses[i], 1));
            }
            
            for (int i = 0; i < TestItem.Addresses.Length; i++)
            {
                evmState.IsCold(TestItem.Addresses[i]).Should().BeFalse();
                evmState.IsCold(new StorageCell(TestItem.Addresses[i], 1)).Should().BeFalse();
            }
        }
        
        [Test]
        public void Can_warm_storage_up_twice()
        {
            EvmState evmState = CreateEvmState();
            Address address = TestItem.AddressA;
            StorageCell storageCell = new(address, 1);
            evmState.WarmUp(storageCell);
            evmState.WarmUp(storageCell);
            evmState.IsCold(storageCell).Should().BeFalse();
        }
        
        [Test]
        public void Nothing_to_commit()
        {
            EvmState parentEvmState = CreateEvmState();
            EvmState evmState = CreateEvmState();
            
            evmState.CommitToParent(parentEvmState);
        }
        
        [Test]
        public void Address_to_commit_keeps_it_warm()
        {
            EvmState parentEvmState = CreateEvmState();
            EvmState evmState = CreateEvmState();
            evmState.WarmUp(TestItem.AddressA);
            
            evmState.CommitToParent(parentEvmState);
            parentEvmState.IsCold(TestItem.AddressA).Should().BeFalse();
        }
        
        [Test]
        public void Storage_to_commit_keeps_it_warm()
        {
            EvmState parentEvmState = CreateEvmState();
            EvmState evmState = CreateEvmState();
            StorageCell storageCell = new(TestItem.AddressA, 1);
            evmState.WarmUp(storageCell);
            
            evmState.CommitToParent(parentEvmState);
            parentEvmState.IsCold(storageCell).Should().BeFalse();
        }
        
        [Test]
        public void Logs_are_committed()
        {
            EvmState parentEvmState = CreateEvmState();
            EvmState evmState = CreateEvmState();
            LogEntry logEntry = new(Address.Zero, Bytes.Empty, Array.Empty<Keccak>());
            evmState.Logs.Add(logEntry);

            evmState.CommitToParent(parentEvmState);
            parentEvmState.Logs.Contains(logEntry).Should().BeTrue();
        }
        
        [Test]
        public void Destroy_list_is_committed()
        {
            EvmState parentEvmState = CreateEvmState();
            EvmState evmState = CreateEvmState();
            evmState.DestroyList.Add(Address.Zero);

            evmState.CommitToParent(parentEvmState);
            parentEvmState.DestroyList.Contains(Address.Zero).Should().BeTrue();
        }
        
        [Test]
        public void Commit_adds_refunds()
        {
            EvmState parentEvmState = CreateEvmState();
            EvmState evmState = CreateEvmState();
            evmState.CommitToParent(parentEvmState);
            parentEvmState.Refund.Should().Be(evmState.Refund);
        }
        
        [Test]
        public void Can_dispose_without_init()
        {
            EvmState evmState = CreateEvmState();
            evmState.Dispose();
        }
        
        [Test]
        public void Can_dispose_after_init()
        {
            EvmState evmState = CreateEvmState();
            evmState.InitStacks();
            evmState.Dispose();
        }

        private static EvmState CreateEvmState(bool isContinuation = false) => 
            new(10000, 
                new ExecutionEnvironment(), 
                ExecutionType.Call, 
                true, 
                Resettable.EmptyPosition, 
                Resettable.EmptyPosition,
                isContinuation);

        public class Context { }
    }
}
