
// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Intrinsics;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Specs;
using Nethermind.Evm.CodeAnalysis;
using Nethermind.Evm.CodeAnalysis.IL;
using Nethermind.State;
using NUnit.Framework;

namespace Nethermind.Evm.Test.CodeAnalysis
{


    [TestFixture]
    public class CMSketchTests
    {
        [TestCase(100, 2, 500)]
        [TestCase(100, 2, 50)]
        [TestCase(10, 2, 10)]
        [TestCase(10, 1)]
        [TestCase(3, 3)]
        [TestCase(1, 1)]
        public void validate_count_min_sketch_bounds_single_update(int buckets, int hashFunctions, int numberOfItemsInStream = 1)
        {

            CMSketch sketch = new CMSketch(hashFunctions, buckets);
            Dictionary<ulong, ulong> actualCounts = new Dictionary<ulong, ulong>();
            ulong totalItems = 0;

            for (int i = 0; i < numberOfItemsInStream; i++)
            {
                sketch.Update(totalItems);
                actualCounts[totalItems] = 1;
                totalItems++;
            }

            validate_count_min_sketch_bounds(sketch, actualCounts);
        }


        [TestCase(0.99999, 0.999, 500)]
        [TestCase(0.999, 0.99, 50)]
        [TestCase(0.85, 0.8, 50)]
        [TestCase(0.1, 0.1, 10)]
        [TestCase(0.999, 0.9, 10)]
        public void validate_count_min_sketch_from_bounds_single_update(double e, double delta, int numberOfItemsInStream = 1)
        {

            CMSketch sketch = new CMSketch(e, delta);
            Dictionary<ulong, ulong> actualCounts = new Dictionary<ulong, ulong>();
            ulong totalItems = 0;

            for (int i = 0; i < numberOfItemsInStream; i++)
            {
                sketch.Update(totalItems);
                actualCounts[totalItems] = 1;
                totalItems++;
            }

            validate_count_min_sketch_bounds(sketch, actualCounts);
        }


        // CMSketch bounds
        // Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= 1 - (2 ^ (-numberOfHashFunctions))
        private static void validate_count_min_sketch_bounds(CMSketch sketch, Dictionary<ulong, ulong> actualCounts)
        {
            var numberOfBoundsBreaches = 0;
            var expectedOverCountDelta = (ulong)Math.Round((double)sketch.error * (double)(actualCounts.Count));

            foreach (KeyValuePair<ulong, ulong> kvp in actualCounts)
            {
                var trueCount = kvp.Value;
                var observedCount = sketch.Query(kvp.Key);
                var expectedKMaxCount = (ulong)trueCount + expectedOverCountDelta;
                Assert.That(trueCount <= observedCount,
                     $"Failed at validating trueCount {trueCount} <= observedCount {observedCount}");
                if (observedCount > expectedKMaxCount)
                    ++numberOfBoundsBreaches;
            }
            double observedFreqOfBreaches = (double)numberOfBoundsBreaches / actualCounts.Count;
            Assert.That(observedFreqOfBreaches <= sketch.probabilityOneMinusDelta,
                         $" Failed at validating observedFreqOfBreaches <= sketch.probabilityOneMinusDelta ,observedFreqOfOverCount {observedFreqOfBreaches}, expectedOverCountDelta: {expectedOverCountDelta} found freq of breaches: {observedFreqOfBreaches} which is greater than expectedBoundsProbability: {sketch.probabilityOneMinusDelta}  ");
        }


        [TestCase(100, 2)]
        [TestCase(10, 1)]
        public void validate_number_of_buckets(int buckets, int hashFunctions = 1, int numberOfUpdates = 1)
        {

            CMSketch sketch = new CMSketch(hashFunctions, buckets);
            ulong totalItems = 0;
            for (int i = 0; i < buckets; i++)
            {
                for (int j = 0; j < numberOfUpdates; j++)
                    sketch.Update(totalItems);
                totalItems++;
            }

            for (ulong _item = 0; _item < totalItems; _item++)
            {
                Assert.That(sketch.Query(_item) >= (ulong)numberOfUpdates,
                    $"Failed at all insertions check for {totalItems}. Expected minimum count: {1}, found: {sketch.Query(_item)} ");
            }

        }
    }
}

