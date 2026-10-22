// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Nethermind.StatsAnalyzer.Plugin.Analyzer.Pattern;
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
        for (int i = 0; i < numberOfUpdates; i++)
            sketch.Update(value);
    }

    [Test]
    public void test_error_single_bucket()
    {
        CmSketch sketch = _oneBucketOneHashFunction;
        Assert.That(sketch.ErrorPerItem, Is.EqualTo(0d));
        // error = 2 / buckets;
        Assert.That(sketch.Error, Is.EqualTo(2d));

        sketch.Update(Ulong0);
        sketch.Update(Ulong1);

        // we have made two updates error per item should be 4
        Assert.That(sketch.ErrorPerItem, Is.EqualTo(4d));
        // querying an unseen item should give the total number of updates done;
        Assert.That(sketch.Query(Ulong2), Is.EqualTo(2UL));
    }

    [Test]
    public void test_error_two_buckets()
    {
        CmSketch sketch = _twoBucketsSixHashFunctions;
        Assert.That(sketch.ErrorPerItem, Is.EqualTo(0d));
        // error = 2 / buckets;
        Assert.That(sketch.Error, Is.EqualTo(1d));

        sketch.Update(Ulong0);

        sketch.Update(Ulong1);
        sketch.Update(Ulong1);
        sketch.Update(Ulong1);

        // we have made four updates error per item should be 4
        Assert.That(sketch.ErrorPerItem, Is.EqualTo(4d));

        Assert.That(sketch.Query(Ulong0), Is.InRange(1UL, 1UL + 4UL));

        Assert.That(sketch.Query(Ulong1), Is.InRange(3UL, 3UL + 4UL));

        // unseen item error
        Assert.That(sketch.Query(Ulong2), Is.LessThanOrEqualTo(4UL));
    }

    [Test]
    public void test_error_sketch_with_01_error()
    {
        CmSketch sketch = _error01Confidence99;
        Assert.That(sketch.Error, Is.LessThanOrEqualTo(.01d));
        sketch.Update(Ulong0);
        make_n_updates(Ulong1, 40, sketch);
        make_n_updates(Ulong2, 59, sketch);

        // we have made 100 (1 + 40 + 59) updates, expected max error per item should be 100 * 0.01
        Assert.That(sketch.ErrorPerItem, Is.LessThanOrEqualTo(1d));

        Assert.That(sketch.Query(Ulong0), Is.InRange(1UL, 2UL));

        Assert.That(sketch.Query(Ulong1), Is.InRange(40UL, 41UL));

        Assert.That(sketch.Query(Ulong2), Is.InRange(59UL, 60UL));

        // unseen item error
        Assert.That(sketch.Query(Ulong3), Is.LessThanOrEqualTo(1UL));
    }


    [Test]
    public void validate_reset()
    {
        CmSketch sketch = new(10, 10);

        for (ulong i = 0; i <= 10; i++)
            sketch.Update(i);

        sketch.Reset();

        for (ulong i = 0; i <= 10; i++)
            Assert.That(sketch.Query(i), Is.EqualTo(0UL));
    }

    [Test]
    public void test_buckets()
    {
        ulong buckets = 1000UL;
        CmSketch sketch = _highAccuracyHighConfidence;
        for (ulong i = 0; i < buckets; i++)
        {
            Assert.That(sketch.Query(i), Is.EqualTo(0UL));
            sketch.Update(i);
            Assert.That(sketch.Query(i), Is.EqualTo(1UL));
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
        // Seeded for reproducibility — empirical breach counts vary across runs.
        Random random = new(42);
        ulong randomUlong;


        CmSketch sketch = new CmSketchBuilder().SetHashFunctions(hashFunctions).SetBuckets(buckets).Build();
        Dictionary<ulong, ulong> actualCounts;

        const double trials = 100;
        double observedConfidence = 0d;

        for (int trial = 0; trial < trials; trial++)
        {
            sketch.Reset();
            actualCounts = [];

            for (int i = 0; i < numberOfItemsInStream; i++)
            {
                randomUlong = (ulong)random.NextInt64(long.MinValue, long.MaxValue);
                int randomUpdate = random.Next(1, 100);
                make_n_updates(randomUlong, randomUpdate, sketch);
                actualCounts[randomUlong] = (ulong)randomUpdate +
                                            CollectionsMarshal.GetValueRefOrAddDefault(actualCounts, randomUlong,
                                                out _);
            }

            observedConfidence += check_confidence(sketch, actualCounts, sketch.Error, sketch.Confidence) ? 1d : 0d;
        }

        // CM-Sketch only guarantees per-trial pass probability >= sketch.Confidence
        // (Markov bound; for k hashes the bound is 1 - 1/2^k). Over 100 trials we
        // expect at least 100 * sketch.Confidence successes. Allow a 10-trial slack
        // for marginal parameter sets like (3, 10, 5) where Confidence = 0.875,
        // so the asymptotic expected success count is only ~87.5.
        Assert.That(observedConfidence, Is.GreaterThanOrEqualTo(trials * sketch.Confidence - 10));
    }

    // Probability(ObservedFreq <= ActualFreq + error * numberOfItemsInStream) <= confidence
    private static bool check_confidence(CmSketch sketch, Dictionary<ulong, ulong> actualCounts, double error,
        double confidence)
    {
        int countOfGreaterThanExpectedError = 0;

        // Iterate over every value and Qty added
        foreach (KeyValuePair<ulong, ulong> kvp in actualCounts)
        {
            ulong trueCount = kvp.Value;
            ulong observedCount = sketch.Query(kvp.Key);

            // our upper error bound
            ulong expectedMaxCount = trueCount + (ulong)Math.Round(sketch.ErrorPerItem);
            //our lower bound should never be violated
            Assert.That(trueCount, Is.LessThanOrEqualTo(observedCount));
            // we count the number of times our upper bound was broken
            if (observedCount > expectedMaxCount)
                ++countOfGreaterThanExpectedError;
        }

        double observedFreqOfBreaches = countOfGreaterThanExpectedError / (double)actualCounts.Count;
        // if the freq of false results is not accounted by the confidence return false
        return observedFreqOfBreaches <= 1.0d - confidence;
    }
}
