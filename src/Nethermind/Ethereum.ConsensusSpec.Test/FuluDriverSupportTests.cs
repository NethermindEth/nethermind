// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;

namespace Ethereum.ConsensusSpec.Test;

[TestFixture]
public class FuluDriverSupportTests
{
    [Test]
    public void AssertRejected_passes_only_the_pipelines_own_rejection_types()
    {
        Assert.DoesNotThrow(() => FuluDriverSupport.AssertRejected(new BeaconStateException("failed spec assertion"), "the operation"));
        Assert.DoesNotThrow(() => FuluDriverSupport.AssertRejected(new ForkChoiceException("failed spec assertion"), "the block"));
    }

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

    [Test]
    public void AssertEveryKeyRunsAVector_fails_when_a_key_reports_its_vector_not_implemented() =>
        Assert.That(
            () => FuluDriverSupport.AssertEveryKeyRunsAVector(KeyedCases, static c => c.Key, static c =>
            {
                if (c.Key == "b")
                    throw new NotImplementedInDriverException("not modelled");
            }),
            Throws.TypeOf<AssertionException>().With.Message.Contains("'b' does not run its vector"));

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
