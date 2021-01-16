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

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Specs;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip1283Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => RopstenSpecProvider.ConstantinopleBlockNumber;
        
        protected override ISpecProvider SpecProvider => RopstenSpecProvider.Instance;

        [TestCase("0x60006000556000600055", 412, 0, 0)]
        [TestCase("0x60006000556001600055", 20212, 0, 0)]
        [TestCase("0x60016000556000600055", 20212, 19800, 0)]
        [TestCase("0x60016000556002600055", 20212, 0, 0)]
        [TestCase("0x60016000556001600055", 20212, 0, 0)]
        [TestCase("0x60006000556000600055", 5212, 15000, 1)]
        [TestCase("0x60006000556001600055", 5212, 4800, 1)]
        [TestCase("0x60006000556002600055", 5212, 0, 1)]
        [TestCase("0x60026000556000600055", 5212, 15000, 1)]
        [TestCase("0x60026000556003600055", 5212, 0, 1)]
        [TestCase("0x60026000556001600055", 5212, 4800, 1)]
        [TestCase("0x60026000556002600055", 5212, 0, 1)]
        [TestCase("0x60016000556000600055", 5212, 15000, 1)]
        [TestCase("0x60016000556002600055", 5212, 0, 1)]
        [TestCase("0x60016000556001600055", 412, 0, 1)]
        [TestCase("0x600160005560006000556001600055", 40218, 19800, 0)]
        [TestCase("0x600060005560016000556000600055", 10218, 19800, 1)]
        public void Test(string codeHex, long gasUsed, long refund, byte originalValue)
        {
            TestState.CreateAccount(Recipient, 0);
            Storage.Set(new StorageCell(Recipient, 0), new [] {originalValue});
            Storage.Commit();
            TestState.Commit(RopstenSpecProvider.Instance.GenesisSpec);
            
            var receipt = Execute(Bytes.FromHexString(codeHex));
            AssertGas(receipt, gasUsed + GasCostOf.Transaction - Math.Min((gasUsed + GasCostOf.Transaction) / 2, refund));
        }
    }
}
