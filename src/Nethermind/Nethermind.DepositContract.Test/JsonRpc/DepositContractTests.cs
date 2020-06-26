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

using System.IO;
using FluentAssertions;
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.DepositContract.Test.JsonRpc
{
    [TestFixture]
    public class DepositContractTests
    {
        [Test]
        public void event_hash_is_valid()
        {
            DepositContract depositContract = new DepositContract(new AbiEncoder(), Address.Zero);
            depositContract.DepositEventHash.Should().Be(new Keccak("0x649bbc62d0e31342afea4e5cd82d4049e7e1ee912fc0889aa790803be39038c5"));
        }
        
        [Test]
        public void can_make_deposit()
        {
            DepositContract depositContract = new DepositContract(new AbiEncoder(), Address.Zero);
            Transaction transaction = depositContract.Deposit(TestItem.AddressA, new byte[42], new byte[32], new byte[96], new byte[32]);
            transaction.Data.Should().HaveCount(484);
        }
    }
}
