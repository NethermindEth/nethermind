﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Store;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    [TestFixture]
    public class Eip2200Tests : VirtualMachineTestsBase
    {
        protected override long BlockNumber => RopstenSpecProvider.IstanbulBlockNumber;
        
        protected override ISpecProvider SpecProvider => RopstenSpecProvider.Instance;

        [TestCase("0x60006000556000600055", 1612, 0, 0)]
        [TestCase("0x60006000556001600055", 20812, 0, 0)]
        [TestCase("0x60016000556000600055", 20812, 19200, 0)]
        [TestCase("0x60016000556002600055", 20812, 0, 0)]
        [TestCase("0x60016000556001600055", 20812, 0, 0)]
        [TestCase("0x60006000556000600055", 5812, 15000, 1)]
        [TestCase("0x60006000556001600055", 5812, 4200, 1)]
        [TestCase("0x60006000556002600055", 5812, 0, 1)]
        [TestCase("0x60026000556000600055", 5812, 15000, 1)]
        [TestCase("0x60026000556003600055", 5812, 0, 1)]
        [TestCase("0x60026000556001600055", 5812, 4200, 1)]
        [TestCase("0x60026000556002600055", 5812, 0, 1)]
        [TestCase("0x60016000556000600055", 5812, 15000, 1)]
        [TestCase("0x60016000556002600055", 5812, 0, 1)]
        [TestCase("0x60016000556001600055", 1612, 0, 1)]
        [TestCase("0x600160005560006000556001600055", 40818, 19200, 0)]
        [TestCase("0x600060005560016000556000600055", 10818, 19200, 1)]
        public void Test(string codeHex, long gasUsed, long refund, byte originalValue)
        {
            TestState.CreateAccount(Recipient, 0);
            Storage.Set(new StorageAddress(Recipient, 0), new [] {originalValue});
            Storage.Commit();
            TestState.Commit(RopstenSpecProvider.Instance.GenesisSpec);
            
            var receipt = Execute(Bytes.FromHexString(codeHex));
            AssertGas(receipt, gasUsed + GasCostOf.Transaction - Math.Min((gasUsed + GasCostOf.Transaction) / 2, refund));
        }
        
        [TestCase("0x60006000556000600055", 1612, 0, 0, true)]
        [TestCase("0x60016000556000600055", 20812, 19200, 0, true)]
        [TestCase("0x60016000556002600055", 20812, 0, 0, true)]
        [TestCase("0x60016000556001600055", 20812, 0, 0, true)]
        [TestCase("0x60006000556000600055", 5812, 15000, 1, true)]
        [TestCase("0x60006000556001600055", 5812, 4200, 1, true)]
        [TestCase("0x60026000556000600055", 5812, 15000, 1, true)]
        [TestCase("0x60026000556003600055", 5812, 0, 1, true)]
        [TestCase("0x60026000556001600055", 5812, 4200, 1, true)]
        [TestCase("0x60026000556002600055", 5812, 0, 1, true)]
        [TestCase("0x60016000556001600055", 1612, 0, 1, true)]
        [TestCase("0x60006000556002600055", 5812, 0, 1, true)]

        [TestCase("0x60016000556000600055", 5812, 15000, 1, false)]
        [TestCase("0x60016000556002600055", 5812, 0, 1, false)]
        [TestCase("0x600160005560006000556001600055", 40818, 19200, 0, false)]
        [TestCase("0x600060005560016000556000600055", 10818, 19200, 1, false)]
        [TestCase("0x60006000556001600055", 20812, 0, 0, false)]
        public void Test_when_gas_at_stipend(string codeHex, long gasUsed, long refund, byte originalValue, bool outOfGasExpected)
        {
            TestState.CreateAccount(Recipient, 0);
            Storage.Set(new StorageAddress(Recipient, 0), new [] {originalValue});
            Storage.Commit();
            TestState.Commit(RopstenSpecProvider.Instance.GenesisSpec);
            
            var receipt = Execute(BlockNumber, 21000 + gasUsed + (2300 - 800), Bytes.FromHexString(codeHex));
            Assert.AreEqual(outOfGasExpected ? 0 : 1, receipt.StatusCode);
        }
        
        [TestCase("0x60006000556000600055", 1612, 0, 0)]
        [TestCase("0x60016000556001600055", 1612, 0, 1)]
        public void Test_when_gas_just_above_stipend(string codeHex, long gasUsed, long refund, byte originalValue)
        {
            TestState.CreateAccount(Recipient, 0);
            Storage.Set(new StorageAddress(Recipient, 0), new [] {originalValue});
            Storage.Commit();
            TestState.Commit(RopstenSpecProvider.Instance.GenesisSpec);
            
            var receipt = Execute(BlockNumber, 21000 + gasUsed + (2301 - 800), Bytes.FromHexString(codeHex));
            Assert.AreEqual(1, receipt.StatusCode);
        }
        
        [TestCase("0x60006000556000600055", 1612, 0, 0)]
        [TestCase("0x60016000556001600055", 1612, 0, 1)]
        public void Test_when_gas_just_below_stipend(string codeHex, long gasUsed, long refund, byte originalValue)
        {
            TestState.CreateAccount(Recipient, 0);
            Storage.Set(new StorageAddress(Recipient, 0), new [] {originalValue});
            Storage.Commit();
            TestState.Commit(RopstenSpecProvider.Instance.GenesisSpec);
            
            var receipt = Execute(BlockNumber, 21000 + gasUsed + (2299 - 800), Bytes.FromHexString(codeHex));
            Assert.AreEqual(0, receipt.StatusCode);
        }
    }
}