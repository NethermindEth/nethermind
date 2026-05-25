// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.Amsterdam;

/// <summary>
/// Generic base for Amsterdam EIP blockchain tests.
/// Fixture path is read from <see cref="AmsterdamFixturePathAttribute"/> on <typeparamref name="TSelf"/>.
/// In CI, only runs on Linux x64 to stay within the job timeout budget.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamBlockChainTestFixture<TSelf> : BlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    public static IEnumerable<BlockchainTest> LoadTests() =>
        AmsterdamLoader.LoadBlockChain<TSelf>();
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamEngineBlockChainTestFixture<TSelf> : BlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    public static IEnumerable<BlockchainTest> LoadTests() =>
        AmsterdamLoader.LoadEngineBlockChain<TSelf>();
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamStateTestFixture<TSelf> : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => RunTest(test).Pass.Should().BeTrue();

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        AmsterdamLoader.LoadStateTests<TSelf>();
}

/// <summary>
/// Loads Amsterdam EIP fixtures from the standard BAL archive.
/// Directory is derived from the fixture subdirectory declared on <typeparamref name="TSelf"/>
/// via <see cref="AmsterdamFixturePathAttribute"/>.
/// </summary>
internal static class AmsterdamLoader
{
    public static IEnumerable<BlockchainTest> LoadBlockChain<TSelf>() =>
        new TestsSourceLoader(Constants.Strategy,
            $"fixtures/blockchain_tests/for_amsterdam/{FixturePath<TSelf>()}")
            .LoadTests<BlockchainTest>();

    public static IEnumerable<BlockchainTest> LoadEngineBlockChain<TSelf>() =>
        new TestsSourceLoader(Constants.Strategy,
            $"fixtures/blockchain_tests_engine/for_amsterdam/{FixturePath<TSelf>()}")
            .LoadTests<BlockchainTest>();

    public static IEnumerable<GeneralStateTest> LoadStateTests<TSelf>() =>
        new TestsSourceLoader(Constants.Strategy,
            $"fixtures/state_tests/for_amsterdam/{FixturePath<TSelf>()}")
            .LoadTests<GeneralStateTest>();

    private static string FixturePath<TSelf>() =>
        (typeof(TSelf).GetCustomAttributes(typeof(AmsterdamFixturePathAttribute), false)
            is [AmsterdamFixturePathAttribute attr, ..])
            ? attr.Path
            : throw new InvalidOperationException(
                $"{typeof(TSelf).Name} must be annotated with [AmsterdamFixturePath(...)].");
}
