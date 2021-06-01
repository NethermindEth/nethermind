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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avro.File;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Mev.Data;
using Nethermind.Mev.Execution;
using Nethermind.Mev.Source;
using Nethermind.Runner.Ethereum.Api;
using NSubstitute;
using NSubstitute.Exceptions;
using NUnit.Framework;

namespace Nethermind.Mev.Test
{
    [TestFixture]
    public class BundlePoolTests
    {
        private const ulong DefaultTimestamp = 1_000_000;
        
        public static IEnumerable BundleRetrievalTest
        {
            get
            {
                yield return new BundleTest(8, DefaultTimestamp, 0, 4, null);
                yield return new BundleTest(9, DefaultTimestamp, 1, 5, null);
                yield return new BundleTest(10, 8, 0, 2, 
                    p => p.AddBundle(new MevBundle(Array.Empty<Transaction>(), 10, 5, 7)));
                yield return new BundleTest(11, DefaultTimestamp, 0, 2, null);
                yield return new BundleTest(12, DefaultTimestamp, 1, 2, null);
                yield return new BundleTest(13, DefaultTimestamp, 0, 1, null);
                yield return new BundleTest(14, DefaultTimestamp, 0, 1, null);
                yield return new BundleTest(15, DefaultTimestamp, 1, 1, null);
                yield return new BundleTest(16, DefaultTimestamp, 0, 0, null);
            }
        }
        
        [TestCaseSource(nameof(BundleRetrievalTest))]
        public void should_retrieve_right_bundles_from_pool(BundleTest test)
        {
            BundlePool bundlePool = CreateBundlePool(test.block);
            if(test.action != null) test.action(bundlePool);
            List<MevBundle> result = bundlePool.GetBundles(test.block, test.testTimestamp).ToList();
            result.Count.Should().Be(test.expectedCount);
        }
        
        [TestCaseSource(nameof(BundleRetrievalTest))]
        public void should_retire_bundles_from_pool_after_finalization(BundleTest test)
        {
            IBlockFinalizationManager blockFinalizationManager = Substitute.For<IBlockFinalizationManager>();
            BundlePool bundlePool = CreateBundlePool(test.block, blockFinalizationManager);
            FinalizeEventArgs finalizeEventArgs = new(
                Build.A.BlockHeader.WithNumber(test.block+1).TestObject, 
                Build.A.BlockHeader.WithNumber(test.block).TestObject
            );
            
            blockFinalizationManager.BlocksFinalized += Raise.EventWith(finalizeEventArgs);
            if(test.action != null) test.action(bundlePool);
            List<MevBundle> result = bundlePool.GetBundles(test.block, test.testTimestamp).ToList();
            result.Count.Should().Be(test.expectedCount);
        }

        private static BundlePool CreateBundlePool(long currentBlock, IBlockFinalizationManager? blockFinalizationManager = null)
        {
            BundlePool bundlePool = new(
                Substitute.For<IBlockTree>(),
                Substitute.For<IBundleSimulator>(),
                blockFinalizationManager ?? Substitute.For<IBlockFinalizationManager>(),
                new Timestamper(),
                new MevConfig(),
                LimboLogs.Instance);

            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 4, 0, 0));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 5, 0, 0));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 6, 0, 0));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 9, 0, 0));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 9, 0, long.MaxValue));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 9, 0, DefaultTimestamp - 1));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 12, 0, 0));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 15, 0, 0));
            
            return bundlePool;
        }
        
        
        [Test]
        public static void should_add_bundle_with_correct_timestamps()
        {
            ITimestamper timestamper = new ManualTimestamper(new DateTime(2021, 1, 1)); //this needs to be 1970?
            ulong timestamp = timestamper.UnixTime.Seconds;
            
            BundlePool bundlePool = new(
                Substitute.For<IBlockTree>(),
                Substitute.For<IBundleSimulator>(),
                null,
                timestamper,
                new MevConfig(),
                LimboLogs.Instance);

            Transaction[] txs = Array.Empty<Transaction>();
            MevBundle[] bundles = new []
            {
                new MevBundle(txs, 1, 0, 0), //should get added
                new MevBundle(txs, 2, 5, 0), //should not get added, min > max
                new MevBundle(txs, 3,  timestamp + 50, timestamp + 100), //should get added
                new MevBundle(txs, 4,  timestamp + 4000, timestamp + 5000), //should not get added, min time too large
                
            };

            bundles.Select(b => bundlePool.AddBundle(b))
                .Should().BeEquivalentTo(new bool[] {true, false, true, false});
        }

        [Test]
        public static void sort_bundles_by_increasing_block_number_and_then_min_timestamp() //add better checking for this
        {
            ITimestamper timestamper = new ManualTimestamper(new DateTime(2021, 1, 1)); //this needs to be 1970?
            ulong timestamp = timestamper.UnixTime.Seconds;
            
            Transaction[] txs = Array.Empty<Transaction>();
            IBlockTree sub = Substitute.For<IBlockTree>();
            BundlePool txPool = new BundlePool(
                Substitute.For<IBlockTree>(),
                Substitute.For<IBundleSimulator>(),
                null, 
                timestamper,
                new MevConfig(), 
                LimboLogs.Instance);
            List<MevBundle> bundleList = new();
            
            for (int i = 3; i > 0; i--)
            {
                bundleList.Add(new MevBundle(txs, i, 0, 0)); //should come back in reverse order
            }

            bundleList.Add(new MevBundle(txs, 1, 10, 20));  //should be ahead of 1,0 but before 2,0 
            bundleList.Add(new MevBundle(txs, 4, 5, 10)); //should be ahead of 4,0 but before 5,0
            foreach (MevBundle bundle in bundleList)
            {
                txPool.AddBundle(bundle);
            }

            List<(long Key, Int64 MinTimestamp)> outputList = new();
            long? BestBlockNumber = sub.Head?.Number ?? sub.BestSuggestedHeader?.Number; 
            foreach (KeyValuePair<long, MevBundle[]> kvp in txPool.GetMevBundles())
            {
                foreach (MevBundle bundle in kvp.Value)
                {
                    outputList.Add((kvp.Key, (Int64) bundle.MinTimestamp));
                }
            }

            for (int i = 0; i < outputList.Count - 1; i++)
            {
                if (outputList[i].Key == outputList[i + 1].Key)
                {
                    outputList[i].MinTimestamp.CompareTo(outputList[i + 1].MinTimestamp).Should().BeOneOf(-1, 0);
                }
                else if (outputList[i].Key > BestBlockNumber && outputList[i + 1].Key > BestBlockNumber)
                {
                    outputList[i].Key.CompareTo(outputList[i + 1].MinTimestamp).Should().BeOneOf(-1, 0);
                }
                else
                {
                    outputList[i].Key.CompareTo(outputList[i + 1].MinTimestamp).Should().BeOneOf(1, 0);
                }
            }
        }
        
        public record BundleTest(long block, ulong testTimestamp, int expectedCount, int expectedRemaining, Action<BundlePool>? action);
        
    }
}
