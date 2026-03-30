// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Ethereum.Test.Base;

/// <summary>
/// Generic base for blockchain tests using <see cref="LoadBlockchainTestsStrategy"/>.
/// Directory is derived by convention: prefix + class name.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class BlockchainTestFixture<TSelf, TPrefix> : BlockchainTestBase
    where TPrefix : ITestDirectoryPrefix
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadBlockchainTestsStrategy(),
            TestDirectoryHelper.GetDirectoryByPrefix<TSelf>(TPrefix.Value)).LoadTests<BlockchainTest>();
}

/// <summary>Convenience alias defaulting to "bc" prefix.</summary>
public abstract class BlockchainTestFixture<TSelf> : BlockchainTestFixture<TSelf, BcPrefix>;

/// <summary>
/// Generic base for legacy blockchain block tests using <see cref="LoadLegacyBlockchainTestsStrategy"/>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class LegacyBlockchainTestFixture<TSelf, TPrefix> : BlockchainTestBase
    where TPrefix : ITestDirectoryPrefix
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadLegacyBlockchainTestsStrategy(),
            TestDirectoryHelper.GetDirectoryByPrefix<TSelf>(TPrefix.Value)).LoadTests<BlockchainTest>();
}

/// <summary>Convenience alias defaulting to "bc" prefix.</summary>
public abstract class LegacyBlockchainTestFixture<TSelf> : LegacyBlockchainTestFixture<TSelf, BcPrefix>;

/// <summary>
/// Variant of <see cref="LegacyBlockchainTestFixture{TSelf,TPrefix}"/> that disables RLP validation.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class LegacyBlockchainTestFixtureNoRlpValidation<TSelf, TPrefix> : BlockchainTestBase
    where TPrefix : ITestDirectoryPrefix
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test, failOnInvalidRlp: false);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadLegacyBlockchainTestsStrategy(),
            TestDirectoryHelper.GetDirectoryByPrefix<TSelf>(TPrefix.Value)).LoadTests<BlockchainTest>();
}

/// <summary>Convenience alias defaulting to "bc" prefix.</summary>
public abstract class LegacyBlockchainTestFixtureNoRlpValidation<TSelf> : LegacyBlockchainTestFixtureNoRlpValidation<TSelf, BcPrefix>;
