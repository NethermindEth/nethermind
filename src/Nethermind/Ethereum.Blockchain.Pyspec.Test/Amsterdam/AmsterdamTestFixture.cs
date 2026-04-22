// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.Amsterdam;

/// <summary>
/// Generic base for Amsterdam EIP blockchain tests.
/// Wildcard is read from <see cref="EipWildcardAttribute"/> on <typeparamref name="TSelf"/>.
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
        new TestsSourceLoader(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = Constants.BalArchiveVersion,
            ArchiveName = Constants.BalArchiveName
        }, "fixtures/blockchain_tests/for_amsterdam", typeof(TSelf).GetCustomAttribute<EipWildcardAttribute>()!.Wildcard).LoadTests<BlockchainTest>();
}

/// <summary>
/// Generic base for Amsterdam EIP engine blockchain tests.
/// Wildcard is read from <see cref="EipWildcardAttribute"/> on <typeparamref name="TSelf"/>.
/// In CI, only runs on Linux x64 to stay within the job timeout budget.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamEngineBlockChainTestFixture<TSelf> : BlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = Constants.BalArchiveVersion,
            ArchiveName = Constants.BalArchiveName
        }, "fixtures/blockchain_tests_engine/for_amsterdam", typeof(TSelf).GetCustomAttribute<EipWildcardAttribute>()!.Wildcard).LoadTests<BlockchainTest>();
}

/// <summary>
/// Generic base for Amsterdam EIP state tests.
/// Wildcard is read from <see cref="EipWildcardAttribute"/> on <typeparamref name="TSelf"/>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamStateTestFixture<TSelf> : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => RunTest(test).Pass.Should().BeTrue();

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = Constants.BalArchiveVersion,
            ArchiveName = Constants.BalArchiveName
        }, "fixtures/state_tests/for_amsterdam", typeof(TSelf).GetCustomAttribute<EipWildcardAttribute>()!.Wildcard).LoadTests<GeneralStateTest>();
}
