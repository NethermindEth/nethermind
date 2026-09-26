// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using NUnit.Framework;

namespace Ethereum.ConsensusSpec.Test;

/// <summary>
/// Guards against the harness counting any exception at all as a correct rejection of an invalid vector,
/// or a not-implemented vector as one that runs.
/// </summary>
[TestFixture]
public class FuluDriverSupportTests
{
    [Test]
    public void AssertRejected_passes_only_the_pipelines_own_rejection_types()
    {
        Assert.DoesNotThrow(() => FuluDriverSupport.AssertRejected(new BeaconStateException("failed spec assertion"), "the operation"));
        Assert.DoesNotThrow(() => FuluDriverSupport.AssertRejected(new ForkChoiceException("failed spec assertion"), "the block"));
    }

    /// <summary>A vector that completes, or crashes on the way to rejecting, must not be recorded as a correct rejection.</summary>
    [Test]
    public void AssertRejected_fails_a_vector_that_completed_or_threw_for_the_wrong_reason()
    {
        Assert.That(() => FuluDriverSupport.AssertRejected(null, "the operation"),
            Throws.TypeOf<AssertionException>().With.Message.Contains("completed without error"));
        Assert.That(() => FuluDriverSupport.AssertRejected(new NullReferenceException(), "the operation"),
            Throws.TypeOf<AssertionException>().With.Message.Contains(nameof(NullReferenceException)));
    }

    private static readonly (string Key, string Name)[] KeyedCases = [("a", "a1"), ("a", "a2"), ("b", "b1")];

    [Test]
    public void AssertEveryKeyRunsAVector_runs_the_first_vector_of_every_key_and_passes_when_none_throws()
    {
        List<string> ran = [];

        FuluDriverSupport.AssertEveryKeyRunsAVector(KeyedCases, static c => c.Key, c => ran.Add(c.Name));

        Assert.That(ran, Is.EqualTo(new[] { "a1", "b1" }));
    }

    /// <summary>A not-implemented vector reports Inconclusive in its own suite, so this check must fail its key.</summary>
    [Test]
    public void AssertEveryKeyRunsAVector_fails_when_a_key_reports_its_vector_not_implemented() =>
        Assert.That(
            () => FuluDriverSupport.AssertEveryKeyRunsAVector(KeyedCases, static c => c.Key, static c =>
            {
                if (c.Key == "b")
                    throw new NotImplementedInDriverException("not modelled");
            }),
            Throws.TypeOf<AssertionException>().With.Message.Contains("'b' does not run its vector"));

    /// <summary>A suite whose keys mix runnable and not-implemented vectors must not pass or fail by which vector comes first.</summary>
    [Test]
    public void AssertEveryKeyRunsSomeVector_passes_when_a_later_vector_of_a_key_runs()
    {
        List<string> ran = [];

        FuluDriverSupport.AssertEveryKeyRunsSomeVector(KeyedCases, static c => c.Key, c =>
        {
            ran.Add(c.Name);
            if (c.Name == "a1")
                throw new NotImplementedInDriverException("not modelled");
        });

        Assert.That(ran, Is.EqualTo(new[] { "a1", "a2", "b1" }));
    }

    /// <summary>Only a not-implemented vector defers to the next one; a vector that really fails must not be hidden by a later pass.</summary>
    [Test]
    public void AssertEveryKeyRunsSomeVector_fails_at_once_when_an_earlier_vector_really_fails() =>
        Assert.That(
            () => FuluDriverSupport.AssertEveryKeyRunsSomeVector(KeyedCases, static c => c.Key, static c =>
            {
                if (c.Name == "a1")
                    throw new InvalidOperationException("wrong verdict");
            }),
            Throws.InvalidOperationException.With.Message.EqualTo("wrong verdict"));

    [Test]
    public void AssertEveryKeyRunsSomeVector_fails_when_every_vector_of_a_key_is_not_implemented() =>
        Assert.That(
            () => FuluDriverSupport.AssertEveryKeyRunsSomeVector(KeyedCases, static c => c.Key, static c =>
            {
                if (c.Key == "a")
                    throw new NotImplementedInDriverException("not modelled");
            }),
            Throws.TypeOf<AssertionException>().With.Message.Contains("'a' reports every vector not implemented"));

    [Test]
    public void AssertEveryKeyRunsAVector_fails_when_no_vectors_are_enumerated() =>
        Assert.That(
            () => FuluDriverSupport.AssertEveryKeyRunsAVector(Array.Empty<(string Key, string Name)>(), static c => c.Key, static _ => { }),
            Throws.TypeOf<AssertionException>().With.Message.Contains("no vectors are enumerated"));
}
