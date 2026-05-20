// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using NUnit.Framework;

namespace Ethereum.Test.Base;

/// <summary>
/// Generic base for legacy general state tests using <see cref="LoadLegacyGeneralStateTestsStrategy"/>.
/// Directory is derived by convention: prefix + class name.
/// </summary>
[Parallelizable(ParallelScope.All)]
public abstract class LegacyStateTestFixture<TSelf, TPrefix> : GeneralStateTestBase
    where TPrefix : ITestDirectoryPrefix
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => Assert.That(RunTest(test).Pass, Is.True);

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(),
            TestDirectoryHelper.GetDirectoryByPrefix<TSelf>(TPrefix.Value)).LoadTests<GeneralStateTest>();
}

/// <summary>Convenience alias defaulting to "st" prefix.</summary>
public abstract class LegacyStateTestFixture<TSelf> : LegacyStateTestFixture<TSelf, StPrefix>;

/// <summary>
/// Variant of <see cref="LegacyStateTestFixture{TSelf,TPrefix}"/> with <c>[Retry(3)]</c> for flaky tests.
/// </summary>
[Parallelizable(ParallelScope.All)]
public abstract class LegacyRetryStateTestFixture<TSelf, TPrefix> : GeneralStateTestBase
    where TPrefix : ITestDirectoryPrefix
{
    [TestCaseSource(nameof(LoadTests))]
    [Retry(3)]
    public void Test(GeneralStateTest test) => Assert.That(RunTest(test).Pass, Is.True);

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        new TestsSourceLoader(new LoadLegacyGeneralStateTestsStrategy(),
            TestDirectoryHelper.GetDirectoryByPrefix<TSelf>(TPrefix.Value)).LoadTests<GeneralStateTest>();
}

/// <summary>Convenience alias defaulting to "st" prefix.</summary>
public abstract class LegacyRetryStateTestFixture<TSelf> : LegacyRetryStateTestFixture<TSelf, StPrefix>;
