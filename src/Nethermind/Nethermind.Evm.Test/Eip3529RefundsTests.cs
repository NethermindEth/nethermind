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
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.Tracing.GethStyle;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Evm.Test
{
    // https://eips.ethereum.org/EIPS/eip-3529
    public class Eip3529Tests : VirtualMachineTestsBase
    {
        const long LondonTestBlockNumber = 5;
        protected override ISpecProvider SpecProvider
        {
            get
            {
                ISpecProvider specProvider = Substitute.For<ISpecProvider>();
                specProvider.GetSpec(Arg.Is<long>(x => x >= LondonTestBlockNumber)).Returns(London.Instance);
                specProvider.GetSpec(Arg.Is<long>(x => x < LondonTestBlockNumber)).Returns(Berlin.Instance);
                return specProvider;
            }
        }
        
       [TestCase("0x60006000556000600055", 212, 0, 0)]
       [TestCase("0x60006000556001600055", 20112, 0, 0)]
       [TestCase("0x60016000556000600055", 20112, 19900, 0)]
       [TestCase("0x60016000556002600055", 20112, 0, 0)]
       [TestCase("0x60016000556001600055", 20112, 0, 0)]
       [TestCase("0x60006000556000600055", 3012, 15000, 1)]
       [TestCase("0x60006000556001600055", 3012, 2800, 1)]
       [TestCase("0x60006000556002600055", 3012, 0, 1)]
       [TestCase("0x60026000556000600055", 3012, 15000, 1)]
       [TestCase("0x60026000556003600055", 3012, 0, 1)]
       [TestCase("0x60026000556001600055", 3012, 2800, 1)]
       [TestCase("0x60026000556002600055", 3012, 0, 1)]
       [TestCase("0x60016000556000600055", 3012, 15000, 1)]
       [TestCase("0x60016000556002600055", 3012, 0, 1)]
       [TestCase("0x60016000556001600055", 212, 0, 1)]
       [TestCase("0x600160005560006000556001600055", 40118, 19900, 0)]
       [TestCase("0x600060005560016000556000600055", 5918, 17800, 1)]
        public void Before_introducing_eip3529(string codeHex, long gasUsed, long refund, byte originalValue)
        {
            Test(codeHex, gasUsed, refund, originalValue, true);
        }
        
        // ToDo - Skip for now
        // [TestCase("0x60006000556000600055", 212, 0, 0)]
        // [TestCase("0x60006000556001600055", 20112, 0, 0)]
        // [TestCase("0x60016000556000600055", 20112, 19900, 0)]
        // [TestCase("0x60016000556002600055", 20112, 0, 0)]
        // [TestCase("0x60016000556001600055", 20112, 0, 0)]
        // [TestCase("0x60006000556000600055", 3012, 4800, 1)]
        // [TestCase("0x60006000556001600055", 3012, 2800, 1)]
        // [TestCase("0x60006000556002600055", 3012, 0, 1)]
        // [TestCase("0x60026000556000600055", 3012, 4800, 1)]
        // [TestCase("0x60026000556003600055", 3012, 0, 1)]
        // [TestCase("0x60026000556001600055", 3012, 2800, 1)]
        // [TestCase("0x60026000556002600055", 3012, 0, 1)]
        // [TestCase("0x60016000556000600055", 3012, 4800, 1)]
        // [TestCase("0x60016000556002600055", 3012, 0, 1)]
        // [TestCase("0x60016000556001600055", 212, 0, 1)]
        // [TestCase("0x600160005560006000556001600055", 40118, 19900, 0)]
        // [TestCase("0x600060005560016000556000600055", 5918, 7600, 1)]
        public void After_introducing_eip3529(string codeHex, long gasUsed, long refund, byte originalValue)
        {
            Test(codeHex, gasUsed, refund, originalValue, false);
        }

        private void Test(string codeHex, long gasUsed, long refund, byte originalValue, bool eip3529Enabled)
        {
            TestState.CreateAccount(Recipient, 0);
            Storage.Set(new StorageCell(Recipient, 0), new [] {originalValue});
            Storage.Commit();
            TestState.Commit(Berlin.Instance);
            _processor = new TransactionProcessor(SpecProvider, TestState, Storage, Machine, LimboLogs.Instance);
            long blockNumber = eip3529Enabled ? LondonTestBlockNumber : LondonTestBlockNumber - 1;
            (Block block, Transaction transaction) = PrepareTx(blockNumber, 100000, Bytes.FromHexString(codeHex));

            transaction.GasPrice = 20.GWei();
            TestAllTracerWithOutput tracer = CreateTracer();
            _processor.Execute(transaction, block.Header, tracer);
            
            AssertGas(tracer, gasUsed + GasCostOf.Transaction - Math.Min((gasUsed + GasCostOf.Transaction) / 2, refund));
        }
    }
}
