// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
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

[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamZkEvmBlockChainTestFixture<TSelf> : BlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = ZkEvmConstants.ZkEvmArchiveVersion,
            ArchiveName = ZkEvmConstants.ZkEvmArchiveName
        }, "fixtures/blockchain_tests", typeof(TSelf).GetCustomAttribute<EipWildcardAttribute>()!.Wildcard).LoadTests<BlockchainTest>()
        .Where(t => t.EngineNewPayloads?.Any(p => p.ExecutionWitnessMutated) != true);
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamZkEvmEngineBlockChainTestFixture<TSelf> : BlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = ZkEvmConstants.ZkEvmArchiveVersion,
            ArchiveName = ZkEvmConstants.ZkEvmArchiveName
        }, "fixtures/blockchain_tests_engine", typeof(TSelf).GetCustomAttribute<EipWildcardAttribute>()!.Wildcard).LoadTests<BlockchainTest>()
        // executionWitnessMutated is a per-payload field; filter tests where any payload is mutated.
        .Where(t => t.EngineNewPayloads?.Any(p => p.ExecutionWitnessMutated) != true);
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class AmsterdamZkEvmWitnessEngineBlockChainTestFixture<TSelf> : WitnessBlockchainTestBase
{
    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy
        {
            ArchiveVersion = ZkEvmConstants.ZkEvmArchiveVersion,
            ArchiveName = ZkEvmConstants.ZkEvmArchiveName
        }, "fixtures/blockchain_tests_engine", typeof(TSelf).GetCustomAttribute<EipWildcardAttribute>()!.Wildcard).LoadTests<BlockchainTest>()
        .Where(t => t.EngineNewPayloads?.Any(p => p.ExecutionWitnessMutated) != true)
        .Where(t => t.EngineNewPayloads?.Any(p => p.ExecutionWitness.HasValue) == true);
}

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
