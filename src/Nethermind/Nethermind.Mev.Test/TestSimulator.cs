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
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using NUnit.Framework;

namespace Nethermind.Mev.Test
{
    public class TestSimulator : IBundleSimulator, IBundleSource, ITxSource, ISimulatedBundleSource
    {
        private readonly TestJson _testJson;

        public TestSimulator(TestJson testJson)
        {
            ValidateTest(testJson);
            _testJson = testJson;
        }

        public Task<SimulatedMevBundle> Simulate(MevBundle bundle, BlockHeader parent, CancellationToken cancellationToken = default)
        {
            long gasUsed = 0;
            UInt256 txFees = 0;
            UInt256 coinbasePayments = 0;
            foreach (Transaction transaction in bundle.Transactions)
            {
                foreach (TxForTest? txForTest in _testJson.Txs!)
                {
                    if (txForTest!.Hash == transaction.Hash)
                    {
                        gasUsed += txForTest.GasUsed;
                        txFees += txForTest.GasPrice * (UInt256)txForTest.GasUsed;
                        coinbasePayments += txForTest.CoinbasePayment;
                    }
                }
            }

            SimulatedMevBundle simulatedMevBundle = new(bundle, true, gasUsed, txFees, coinbasePayments);
            return Task.FromResult(simulatedMevBundle);
        }

        public Task<IEnumerable<MevBundle>> GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken cancellationToken = default) => 
            Task.FromResult(_testJson.Bundles!.Select(ToBundle));

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            return _testJson.Txs!.Where(tx => tx.Visibility != TxVisibility.Relay)
                .Select(tx => ToTx(tx!.Hash));
        }

        /// <summary>
        /// Would be nicer with schema but let us keep it simple here.
        /// </summary>
        /// <param name="testJson">Test object to validate</param>
        /// <returns>(<value>True</value>, <value>null</value>) if valid,
        /// otherwise (<value>False</value>, <value>"error description"</value>).</returns>
        private static void ValidateTest(TestJson testJson)
        {
            if (testJson.GasLimit is null)
            {
                Assert.Fail("Gas limit not specified");
            }

            if (testJson.Name is null)
            {
                Assert.Fail("Name missing");
            }

            if (testJson.Description is null)
            {
                Assert.Fail("Description missing");
            }

            if (testJson.Bundles is null)
            {
                Assert.Fail("Bundles missing");
            }

            if (testJson.Txs is null)
            {
                Assert.Fail("Transactions missing");
            }

            if (testJson.OptimalProfit is null)
            {
                Assert.Fail("Optimal profit not specified");
            }

            if (testJson.Bundles!.Any(item => item is null))
            {
                Assert.Fail("One of the bundles is null");
            }

            if (testJson.Txs!.Any(item => item is null))
            {
                Assert.Fail("One of the transactions is null");
            }
        }

        private MevBundle ToBundle(MevBundleForTest? bundleForTest)
        {
            if (bundleForTest == null) throw new ArgumentNullException(nameof(bundleForTest));
            return new(bundleForTest.Txs.Select(ToTx).ToArray(), 1, UInt256.Zero, UInt256.Zero);
        }

        private Transaction ToTx(Keccak txs)
        {
            TxForTest txForTest = _testJson.Txs!.Single(t => t!.Hash == txs)!; 
            return new() {Hash = txForTest.Hash, GasLimit = txForTest.GasUsed, GasPrice = txForTest.GasPrice};
        }

        async Task<IEnumerable<SimulatedMevBundle>> ISimulatedBundleSource.GetBundles(BlockHeader parent, UInt256 timestamp, long gasLimit, CancellationToken token)
        {
            IEnumerable<MevBundle> bundles = await GetBundles(parent, timestamp, gasLimit, token);
            IEnumerable<Task<SimulatedMevBundle>> simulatedBundles = bundles.Select(b => Simulate(b, parent, token));
            return await Task.WhenAll(simulatedBundles);
        }
    }
}
