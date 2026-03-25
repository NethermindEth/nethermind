// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.Amsterdam;

/// <summary>
/// Generic base for Amsterdam EIP blockchain tests.
/// Wildcard is read from <see cref="EipWildcardAttribute"/> on <typeparamref name="TSelf"/>.
/// Linux x64 only: blockchain tests are heavy and timeout on slower CI runners.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Platform("Linux")]
public abstract class AmsterdamBlockChainTestFixture<TSelf> : BlockchainTestBase
{
    [SetUp]
    public void SkipOnArm()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            Assert.Ignore("Skipped on ARM — exceeds CI timeout");
    }

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test);

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
/// Linux x64 only: engine tests are heavy (full DI + Engine API) and timeout on slower CI runners.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Platform("Linux")]
public abstract class AmsterdamEngineBlockChainTestFixture<TSelf> : BlockchainTestBase
{
    [SetUp]
    public void SkipOnArm()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            Assert.Ignore("Skipped on ARM — exceeds CI timeout");
    }

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => await RunTest(test);

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
