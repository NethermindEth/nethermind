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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    [Explicit("Those are simulations and not unit tests and shouldn't be considered for CI purposes.")]
    public class BundleSourceTests
    {
        private readonly BlockHeader _blockHeader = Build.A.BlockHeader.WithNumber(1).TestObject;

        [TestCaseSource(nameof(LoadTests))]
        public async Task Test(TestJson testJson)
        {
            TestSimulator testSimulator = new(testJson);
            IBundleSimulator simulator = testSimulator;


            IBundleSource selector = testJson.SelectorType switch
            {
                SelectorType.V2 => new BundleSelector(testSimulator, testJson.MaxMergedBundles),
                _ => throw new ArgumentOutOfRangeException()
            };

            IEnumerable<MevBundle> selected = await selector.GetBundles(_blockHeader, _blockHeader.Timestamp, testJson.GasLimit!.Value);
            SimulatedMevBundle[] simulated = (await simulator.Simulate(selected, _blockHeader)).ToArray();
            long totalGasUsedByBundles = simulated.Sum(s => s.GasUsed);
            long gasLeftForTransactions = testJson.GasLimit!.Value - totalGasUsedByBundles;
            IEnumerable<Transaction>? txs = testSimulator.GetTransactions(_blockHeader, gasLeftForTransactions);
            
            UInt256 totalProfit = simulated.Aggregate<SimulatedMevBundle, UInt256>(0, (profit, x) => profit + x.Profit);
            totalProfit += txs.Aggregate<Transaction, UInt256>(0, (profit, x) => profit + (x.GasPrice * (UInt256)x.GasLimit));
            
            totalProfit.Should().Be(testJson.OptimalProfit!.Value, testJson.Description);
        }

        private static IEnumerable<TestJson> AllMaxMergedBundles(TestJson testJson)
        {
            int[] bundles = {1, 2, 4};

            foreach (int bundle in bundles)
            {
                TestJson withBundle = (TestJson)testJson.Clone();
                withBundle.MaxMergedBundles = bundle;
                yield return withBundle;
            }
        }

        private static IEnumerable<TestJson> AllSelectors(TestJson testJson)
        {
            foreach (SelectorType value in Enum.GetValues<SelectorType>())
            {
                TestJson withSelector = (TestJson)testJson.Clone();
                withSelector.SelectorType = value;
                yield return withSelector;
            }
        }

        [SuppressMessage("ReSharper", "InconsistentNaming")]
        public static IEnumerable<TestJson?> LoadTests()
        {
            EthereumJsonSerializer serializer = new();
            foreach (string file in Directory.GetFiles(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tests"), "*.json"))
            {
                using FileStream stream = new(file, FileMode.Open);
                TestJson? testJson = serializer.Deserialize<TestJson>(stream);
                if (testJson is not null)
                {
                    foreach (TestJson withSelector in AllSelectors(testJson))
                    {
                        foreach (TestJson withMaxMergedBundleCount in AllMaxMergedBundles(withSelector))
                        {
                            yield return withMaxMergedBundleCount;
                        }
                    }
                }
            }
        }
    }
}
