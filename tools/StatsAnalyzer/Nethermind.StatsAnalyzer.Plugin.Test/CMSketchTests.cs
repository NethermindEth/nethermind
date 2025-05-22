// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentAssertions;
using Nethermind.PatternAnalyzer.Plugin.Analyzer.Pattern;
using NUnit.Framework;

namespace Nethermind.StatsAnalyzer.Plugin.Test;

[TestFixture]
public class CmSketchTests
{
    [SetUp]
    public void SetUp()
    {
        _oneBucketOneHashFunction = new CmSketchBuilder().SetBuckets(1).SetHashFunctions(1).Build();
        _twoBucketsSixHashFunctions = new CmSketchBuilder().SetBuckets(2).SetHashFunctions(6).Build();
        _error01Confidence99 = new CmSketchBuilder().SetMaxError(0.01).SetMinConfidence(0.99).Build();
        _highAccuracyHighConfidence =
            new CmSketchBuilder().SetMaxError(0.0000001).SetMinConfidence(0.99999999).Build();
    }

    private CmSketch _oneBucketOneHashFunction;
    private CmSketch _twoBucketsSixHashFunctions;
    private CmSketch _error01Confidence99;
    private CmSketch _highAccuracyHighConfidence;

    private const ulong Ulong0 = 0UL;
    private const ulong Ulong1 = 234UL;
    private const ulong Ulong2 = 1000000UL;
    private const ulong Ulong3 = 999999999999UL;

    // n updates of the same item
    public static void make_n_updates(ulong value, int numberOfUpdates, CmSketch sketch)
    {
        for (var i = 0; i < numberOfUpdates; i++)
            sketch.Update(value);
    }

    [Test]
    public void test_error_single_bucket()
    {
        var sketch = _oneBucketOneHashFunction;
        sketch.ErrorPerItem.Should().Be(0d);
        // error = 2 / buckets;
        sketch.Error.Should().Be(2d);

        sketch.Update(Ulong0);
        sketch.Update(Ulong1);

        // we have made two updates error per item should be 4
        sketch.ErrorPerItem.Should().Be(4d);
        // querying an unseen item should give the total number of updates done;
        sketch.Query(Ulong2).Should().Be(2);
    }

    [Test]
    public void test_error_two_buckets()
    {
        var sketch = _twoBucketsSixHashFunctions;
        sketch.ErrorPerItem.Should().Be(0d);
        // error = 2 / buckets;
        sketch.Error.Should().Be(1d);

        sketch.Update(Ulong0);

        sketch.Update(Ulong1);
        sketch.Update(Ulong1);
        sketch.Update(Ulong1);

        // we have made four updates error per item should be 4
        sketch.ErrorPerItem.Should().Be(4d);

        sketch.Query(Ulong0).Should().BeInRange(1, 1 + 4);

        sketch.Query(Ulong1).Should().BeInRange(3, 3 + 4);

        // unseen item error
        sketch.Query(Ulong2).Should().BeLessThanOrEqualTo(4);
    }

    [Test]
    public void test_error_sketch_with_01_error()
    {
        var sketch = _error01Confidence99;
        sketch.Error.Should().BeLessThanOrEqualTo(.01d);
        sketch.Update(Ulong0);
        make_n_updates(Ulong1, 40, sketch);
        make_n_updates(Ulong2, 59, sketch);

        // we have made 100 (1 + 40 + 59) updates, expected max error per item should be 100 * 0.01
        sketch.ErrorPerItem.Should().BeLessThanOrEqualTo(1d);

        sketch.Query(Ulong0).Should().BeInRange(1, 2);

        sketch.Query(Ulong1).Should().BeInRange(40, 41);

        sketch.Query(Ulong2).Should().BeInRange(59, 60);

        // unseen item error
        sketch.Query(Ulong3).Should().BeLessThanOrEqualTo(1);
    }


    [Test]
    public void validate_reset()
    {
        var sketch = new CmSketch(10, 10);

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
        var sketch = _highAccuracyHighConfidence;
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
        var random = new Random();
        ulong randomUlong;


        var sketch = new CmSketchBuilder().SetHashFunctions(hashFunctions).SetBuckets(buckets).Build();
        Dictionary<ulong, ulong> actualCounts;

        double confidence = 100;
        var observedConfidence = 0d;

        for (var trials = 0; trials < confidence; trials++)
        {
            sketch.Reset();
            actualCounts = new Dictionary<ulong, ulong>();

            for (var i = 0; i < numberOfItemsInStream; i++)
            {
                // for each item in stream select a random ulong
                randomUlong = (ulong)random.NextInt64(long.MinValue, long.MaxValue);
                // for each item in stream select a random update Qty
                var randomUpdate = random.Next(1, 100);
                // make n-random updates for random ulong
                make_n_updates(randomUlong, randomUpdate, sketch);
                // store the actual update qty in the dictionary
                actualCounts[randomUlong] = (ulong)randomUpdate +
                                            CollectionsMarshal.GetValueRefOrAddDefault(actualCounts, randomUlong,
                                                out _);
            }

            observedConfidence += check_confidence(sketch, actualCounts, sketch.Error, sketch.Confidence) ? 1d : 0d;
        }

        // 99% certainity that the confidence holds
        observedConfidence.Should().BeGreaterThanOrEqualTo(confidence - 1);
    }

    // Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= confidence
    private static bool check_confidence(CmSketch sketch, Dictionary<ulong, ulong> actualCounts, double error,
        double confidence)
    {
        var countOfGreaterThanExpectedError = 0;

        // Iterate over every value and Qty added
        foreach (var kvp in actualCounts)
        {
            var trueCount = kvp.Value;
            var observedCount = sketch.Query(kvp.Key);

            // our upper error bound
            var expectedMaxCount = trueCount + (ulong)Math.Round(sketch.ErrorPerItem);
            //our lower bound should never be violated
            trueCount.Should().BeLessThanOrEqualTo(observedCount);
            // we count the number of times our upper bound was broken
            if (observedCount > expectedMaxCount)
                ++countOfGreaterThanExpectedError;
        }

        var observedFreqOfBreaches = countOfGreaterThanExpectedError / (double)actualCounts.Count;
        // if the freq of false results is not accounted by the confidence return false
        return observedFreqOfBreaches <= 1.0d - confidence;
    }
}
