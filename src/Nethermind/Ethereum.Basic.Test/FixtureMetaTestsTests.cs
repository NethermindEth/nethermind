// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Ethereum.Test.Base;
using NUnit.Framework;

namespace Ethereum.Basic.Test;

/// <summary>
/// The fixture meta tests must match a directory to its class by the exact name, and must flag a test class whose case
/// source yields nothing.
/// </summary>
public class FixtureMetaTestsTests
{
    private static readonly Dictionary<Type, string> NoneEmptyOnPurpose = [];

    [Test]
    public void Category_matches_a_class_with_the_same_name() =>
        Assert.That(FindUntestedCategory("st" + nameof(FixtureMetaTestsTests)), Is.Empty);

    [Test]
    public void Category_does_not_match_a_class_whose_name_differs_only_in_case() =>
        Assert.That(FindUntestedCategory("st" + nameof(FixtureMetaTestsTests).ToLowerInvariant()), Has.Count.EqualTo(1));

    [Test]
    public void Source_with_cases_passes() =>
        Assert.That(FixtureCaseMetaTests.FindSourcesWithoutCases([typeof(WithCases)], NoneEmptyOnPurpose), Is.Empty);

    [TestCase(typeof(WithoutCases), "the source yields no cases")]
    [TestCase(typeof(Throwing), "the source threw System.InvalidOperationException: unreadable")]
    public void Source_without_cases_is_reported(Type testClass, string problem)
    {
        List<string> problems = FixtureCaseMetaTests.FindSourcesWithoutCases([typeof(WithCases), testClass], NoneEmptyOnPurpose);

        Assert.That(problems, Has.Count.EqualTo(1));
        Assert.That(problems[0], Does.StartWith($"{testClass.FullName}.Test (Cases): {problem}"));
    }

    [Test]
    public void Class_listed_as_empty_on_purpose_passes_while_it_is_empty() =>
        Assert.That(FixtureCaseMetaTests.FindSourcesWithoutCases([typeof(WithCases), typeof(WithoutCases)],
            new Dictionary<Type, string> { [typeof(WithoutCases)] = "reason" }), Is.Empty);

    [TestCase(typeof(WithCases), "is listed as empty on purpose but has no empty case source")]
    [TestCase(typeof(FixtureMetaTestsTests), "is listed as empty on purpose but is not a test class here")]
    public void Stale_entry_of_the_empty_on_purpose_list_is_reported(Type listed, string problem) =>
        Assert.That(FixtureCaseMetaTests.FindSourcesWithoutCases([typeof(WithCases)], new Dictionary<Type, string> { [listed] = "reason" }),
            Is.EqualTo(new[] { $"{listed.FullName} {problem}" }));

    private static List<string> FindUntestedCategory(string category) =>
        TestDirectoryHelper.FindUntestedCategories(typeof(FixtureMetaTestsTests), [category], static d => TestDirectoryHelper.GetClassNameFromDirectory(d, 2));

    // Abstract, so that NUnit does not run them; the meta tests read their sources without an instance.
    public abstract class WithCases
    {
        [TestCaseSource(nameof(Cases))]
        public void Test(int value) => Assert.That(value, Is.Positive);

        public static IEnumerable<int> Cases() => [1];
    }

    public abstract class WithoutCases
    {
        [TestCaseSource(nameof(Cases))]
        public void Test(int value) => Assert.That(value, Is.Positive);

        public static IEnumerable<int> Cases() => [];
    }

    public abstract class Throwing
    {
        [TestCaseSource(nameof(Cases))]
        public void Test(int value) => Assert.That(value, Is.Positive);

        public static IEnumerable<int> Cases() => throw new InvalidOperationException("unreadable");
    }
}
