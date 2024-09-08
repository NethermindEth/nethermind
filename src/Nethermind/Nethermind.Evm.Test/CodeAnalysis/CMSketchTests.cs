// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
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
        [TestCase(3, 3, 20)]
        [TestCase(1, 1, 20)]
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

            validate_count_min_sketch_bounds(sketch, actualCounts, sketch.error, sketch.probabilityOneMinusDelta);
        }


        [TestCase(0.1, 0.99, 500)]
        [TestCase(0.01, 0.999, 50)]
        [TestCase(0.45, 0.7, 50)]
        [TestCase(0.1, 0.1, 100)]
        [TestCase(0.2, 0.9, 100)]
        public void validate_count_min_sketch_from_bounds_single_update(double e, double oneMinusdelta, int numberOfItemsInStream = 1)
        {

            CMSketch sketch = new CMSketch(e, oneMinusdelta);
            Dictionary<ulong, ulong> actualCounts = new Dictionary<ulong, ulong>();
            ulong totalItems = 0;

            for (int i = 0; i < numberOfItemsInStream; i++)
            {
                sketch.Update(totalItems);
                actualCounts[totalItems] = 1;
                totalItems++;
            }

            validate_count_min_sketch_bounds(sketch, actualCounts, e, oneMinusdelta);
        }


        // CMSketch bounds
        // Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= 1 - (2 ^ (-numberOfHashFunctions))
        private static void validate_count_min_sketch_bounds(CMSketch sketch, Dictionary<ulong, ulong> actualCounts, double error, double probabilityOneMinusDelta)
        {
            var numberOfBoundsBreaches = 0;
            var expectedOverCountDelta = (ulong)Math.Round((double)error * (double)(actualCounts.Count));

            foreach (KeyValuePair<ulong, ulong> kvp in actualCounts)
            {
                var trueCount = kvp.Value;
                var observedCount = sketch.Query(kvp.Key);
                var expectedMaxCount = (ulong)trueCount + expectedOverCountDelta;
                Assert.That(trueCount <= observedCount,
                     $"Failed at validating trueCount {trueCount} <= observedCount {observedCount}");
                if (observedCount > expectedMaxCount)
                    ++numberOfBoundsBreaches;
            }
            double observedFreqOfBreaches = (double)numberOfBoundsBreaches / actualCounts.Count;
            Assert.That(observedFreqOfBreaches <= probabilityOneMinusDelta,
                         $" Failed at validating observedFreqOfBreaches <= sketch.probabilityOneMinusDelta ,observedFreqOfOverCount {observedFreqOfBreaches}, expectedOverCountDelta: {expectedOverCountDelta} found freq of breaches: {observedFreqOfBreaches} which is greater than expectedBoundsProbability: {probabilityOneMinusDelta}  ");
        }


        [TestCase(10000)]
        public void validate_number_of_buckets(int buckets)
        {

            CMSketch sketch = new CMSketch(100, buckets);
            ulong totalItems = 0;
            for (int i = 0; i < buckets; i++)
            {
                Assert.That(sketch.Query(totalItems) == 0,
                    $"Expected not previously updated item  {totalItems} to be 0, found {sketch.Query((ulong)i)}");
                sketch.Update(totalItems);
                Assert.That(sketch.Query(totalItems) == 1,
                    $"Expected previously updated item  {totalItems} to be 1, found {sketch.Query((ulong)i)}");
                totalItems++;
            }

            for (ulong _item = 0; _item < totalItems; _item++)
            {
                Assert.That(sketch.Query(_item) >= 1,
                    $"Failed at all insertions check for {totalItems}. Expected minimum count: {1}, found: {sketch.Query(_item)} ");
            }

        }

        [Test]
        public void validate_reset()
        {

            CMSketch sketch = new CMSketch(10, 10);
            for (ulong i = 0; i <= 10; i++)
            {
                sketch.Update(i);
            }
            sketch.Reset();
            for (ulong i = 0; i <= 10; i++)
            {
                Assert.That(sketch.Query(i) == 0,
                    $"Found non empty sketch");
            }

        }
    }
}

