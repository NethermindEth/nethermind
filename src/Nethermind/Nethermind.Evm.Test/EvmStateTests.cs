//  Copyright (c) 2018 Demerzel Solutions Limited
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
                () => new EvmState(10000, new ExecutionEnvironment(), ExecutionType.Call, true, true));
        }

        [Test]
        public void THings_are_cold_to_start_with()
        {
            EvmState evmState = new EvmState(10000, new ExecutionEnvironment(), ExecutionType.Call, true, false);
            StorageCell storageCell = new StorageCell(TestItem.AddressA, 1);
            evmState.IsCold(TestItem.AddressA).Should().BeFalse();
            evmState.IsCold(storageCell).Should().BeFalse();
        }
        
        [Test]
        public void Nothing_to_commit()
        {
            EvmState parentEvmState = new EvmState(10000, new ExecutionEnvironment(), ExecutionType.Call, true, false);
            EvmState evmState = new EvmState(10000, new ExecutionEnvironment(), ExecutionType.Call, true, false);
            
            evmState.CommitToParent(parentEvmState);
        }
        
        [Test]
        public void Address_to_commit_keeps_it_warm()
        {
            EvmState parentEvmState = new EvmState(10000, new ExecutionEnvironment(), ExecutionType.Call, true, false);
            EvmState evmState = new EvmState(10000, new ExecutionEnvironment(), ExecutionType.Call, true, false);
            evmState.WarmUp(TestItem.AddressA);
            
            evmState.CommitToParent(parentEvmState);
            parentEvmState.IsCold(TestItem.AddressA).Should().BeFalse();
        }
        
        [Test]
        public void Storage_to_commit_keeps_it_warm()
        {
            EvmState parentEvmState = new EvmState(10000, new ExecutionEnvironment(), ExecutionType.Call, true, false);
            EvmState evmState = new EvmState(10000, new ExecutionEnvironment(), ExecutionType.Call, true, false);
            StorageCell storageCell = new StorageCell(TestItem.AddressA, 1);
            evmState.WarmUp(storageCell);
            
            evmState.CommitToParent(parentEvmState);
            parentEvmState.IsCold(storageCell).Should().BeFalse();
        }

        public class Context() { }
    }
}