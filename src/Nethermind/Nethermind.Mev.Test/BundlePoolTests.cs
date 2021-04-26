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
using FluentAssertions;
using Nethermind.Core;
using Nethermind.Mev.Data;
using Nethermind.Mev.Source;
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
                yield return new BundleTest(9, DefaultTimestamp, 2, 4, null);
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
            BundlePool bundlePool = new();

            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 4, 0, 0));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 5, 0, 0));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 6, 0, 0));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 9, 0, 0));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 9, 0, long.MaxValue));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 9, 0, DefaultTimestamp - 1));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 12, 0, 0));
            bundlePool.AddBundle(new MevBundle(Array.Empty<Transaction>(), 15, 0, 0));
            
            if(test.action != null) test.action(bundlePool);
            List<MevBundle> result = bundlePool.GetBundles(test.block, test.testTimestamp).ToList();
            result.Count.Should().Be(test.expectedCount);
        }
        public record BundleTest(long block, ulong testTimestamp, int expectedCount, int expectedRemaining, Action<BundlePool>? action);
    }
}
