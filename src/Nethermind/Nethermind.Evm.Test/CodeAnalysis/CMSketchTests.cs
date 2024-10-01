// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Evm.CodeAnalysis.StatsAnalyzer;
using FluentAssertions;
using NUnit.Framework;
using System.Runtime.InteropServices;

namespace Nethermind.Evm.Test.CodeAnalysis.Stats
{


    [TestFixture]
    public class CMSketchTests
    {

        CMSketch oneBucketOneHashFunction;
        CMSketch twoBucketsSixHashFunctions;
        CMSketch error01confidence99;
        CMSketch highAccuracyHighConfidence;

        ulong ulong0 = 0UL;
        ulong ulong1 = 234UL;
        ulong ulong2 = 1000000UL;
        ulong ulong3 = 999999999999UL;



        [SetUp]
        public void SetUp()
        {
            oneBucketOneHashFunction = new CMSketchBuilder().SetBuckets(1).SetHashFunctions(1).Build();
            twoBucketsSixHashFunctions = new CMSketchBuilder().SetBuckets(2).SetHashFunctions(6).Build();
            error01confidence99 = new CMSketchBuilder().SetMaxError(0.01).SetMinConfidence(0.99).Build();
            highAccuracyHighConfidence = new CMSketchBuilder().SetMaxError(0.0000001).SetMinConfidence(0.99999999).Build();
        }

        // n updates of the same item
        public static void make_n_updates(ulong value, int numberOfUpdates, CMSketch sketch)
        {
            for (int i = 0; i < numberOfUpdates; i++)
                sketch.Update(value);
        }

        [Test]
        public void test_error_single_bucket()
        {
            var sketch = oneBucketOneHashFunction;
            sketch.errorPerItem.Should().Be(0d);
            // error = 2 / buckets;
            sketch.error.Should().Be(2d);

            sketch.Update(ulong0);
            sketch.Update(ulong1);

            // we have made two updates error per item should be 4
            sketch.errorPerItem.Should().Be(4d);
            // querying an unseen item should give the total number of updates done;
            sketch.Query(ulong2).Should().Be(2);
        }

        [Test]
        public void test_error_two_buckets()
        {
            var sketch = twoBucketsSixHashFunctions;
            sketch.errorPerItem.Should().Be(0d);
            // error = 2 / buckets;
            sketch.error.Should().Be(1d);

            sketch.Update(ulong0);

            sketch.Update(ulong1);
            sketch.Update(ulong1);
            sketch.Update(ulong1);

            // we have made four updates error per item should be 4
            sketch.errorPerItem.Should().Be(4d);

            sketch.Query(ulong0).Should().BeGreaterThanOrEqualTo(1);
            sketch.Query(ulong0).Should().BeLessThanOrEqualTo(1 + 4);

            sketch.Query(ulong1).Should().BeGreaterThanOrEqualTo(3);
            sketch.Query(ulong1).Should().BeLessThanOrEqualTo(3 + 4);

            // unseen item error
            sketch.Query(ulong2).Should().BeLessThanOrEqualTo(4);
        }

        [Test]
        public void test_error_sketch_with_01_error()
        {
            var sketch = error01confidence99;
            sketch.error.Should().BeLessThanOrEqualTo(.01d);
            sketch.Update(ulong0);
            make_n_updates(ulong1, 40, sketch);
            make_n_updates(ulong2, 59, sketch);

            // we have made 100 (1 + 40 + 59) updates, expected max error per item should be 100 * 0.01
            sketch.errorPerItem.Should().BeLessThanOrEqualTo(1d);

            sketch.Query(ulong0).Should().BeGreaterThanOrEqualTo(1);
            sketch.Query(ulong0).Should().BeLessThanOrEqualTo(2);

            sketch.Query(ulong1).Should().BeGreaterThanOrEqualTo(40);
            sketch.Query(ulong1).Should().BeLessThanOrEqualTo(41);

            sketch.Query(ulong2).Should().BeGreaterThanOrEqualTo(59);
            sketch.Query(ulong2).Should().BeLessThanOrEqualTo(60);

            // unseen item error
            sketch.Query(ulong3).Should().BeLessThanOrEqualTo(1);
        }


        [Test]
        public void validate_reset()
        {

            CMSketch sketch = new CMSketch(10, 10);

            for (ulong i = 0; i <= 10; i++)
                sketch.Update(i);

            sketch.Reset();

            for (ulong i = 0; i <= 10; i++)
                sketch.Query(i).Should().Be(0);

        }

        [Test]
        public void test_buckets()
        {

            var buckets = 1000;
            var sketch = highAccuracyHighConfidence;
            for (ulong i = 0; i < (ulong)buckets; i++)
            {
                sketch.Query(i).Should().Be(0UL);
                sketch.Update(i);
                sketch.Query(i).Should().Be(1UL);
            }

        }


        [TestCase(2, 100, 500)]
        [TestCase(2, 100, 50)]
        [TestCase(4, 1000, 1000)]
        [TestCase(4, 10, 1)]
        [TestCase(3, 10, 5)]
        [TestCase(1, 1, 20)]
        [TestCase(4, 10, 100)]
        public void validate_confidence(int hashFunctions, int buckets, int numberOfItemsInStream = 1)
        {
            Random random = new Random();
            ulong randomUlong;


            CMSketch sketch = new CMSketchBuilder().SetHashFunctions(hashFunctions).SetBuckets(buckets).Build();
            Dictionary<ulong, ulong> actualCounts;

            double confidence = 100;
            double observedConfidence = 0d;

            for (int trials = 0; trials < confidence; trials++)
            {
                sketch.Reset();
                actualCounts = new Dictionary<ulong, ulong>();

                for (int i = 0; i < numberOfItemsInStream; i++)
                {
                    // for each item in stream select a random ulong
                    randomUlong = (ulong)random.NextInt64(Int64.MinValue, Int64.MaxValue);
                    // for each item in stream select a random update Qty
                    var randomUpdate = random.Next(1, 100);
                    // make n-random updates for random ulong
                    make_n_updates(randomUlong, randomUpdate, sketch);
                    // store the actual update qty in the dictionary
                    actualCounts[randomUlong] = (ulong)randomUpdate + CollectionsMarshal.GetValueRefOrAddDefault(actualCounts, randomUlong, out bool _);
                }

                observedConfidence += check_confidence(sketch, actualCounts, sketch.error, sketch.confidence) ? 1d : 0d;
            }
            // 99% certainity that the confidence holds
            observedConfidence.Should().BeGreaterThanOrEqualTo(confidence - 1);

        }

        // Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= confidence
        private static bool check_confidence(CMSketch sketch, Dictionary<ulong, ulong> actualCounts, double error, double confidence)
        {
            var countOfGreaterThanExpectedError = 0;

            // Iterate over every value and Qty added
            foreach (KeyValuePair<ulong, ulong> kvp in actualCounts)
            {
                var trueCount = kvp.Value;
                var observedCount = sketch.Query(kvp.Key);

                // our upper error bound
                var expectedMaxCount = trueCount + (ulong)Math.Round(sketch.errorPerItem);
                //our lower bound should never be violated
                trueCount.Should().BeLessThanOrEqualTo(observedCount);
                // we count the number of times our upper bound was broken
                if (observedCount > expectedMaxCount)
                    ++countOfGreaterThanExpectedError;
            }

            double observedFreqOfBreaches = (double)countOfGreaterThanExpectedError / (double)actualCounts.Count;
            // if the freq of false results is not accounted by the confidence return false
            if (!(observedFreqOfBreaches <= (1.0d - confidence)))
                return false;
            return true;
        }




    }
}

