// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using FluentAssertions;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

/// <summary>
/// Generic base for pyspec blockchain tests using <see cref="LoadPyspecTestsStrategy"/>.
/// Directory is derived by convention: strip "BlockchainTests" suffix, lowercase.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecBlockchainTestFixture<TSelf> : BlockchainTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/blockchain_tests/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("BlockchainTests")}").LoadTests<BlockchainTest>();
}

/// <summary>
/// Generic base for pyspec engine blockchain tests using <see cref="LoadPyspecTestsStrategy"/>.
/// Directory is derived by convention: strip "EngineBlockchainTests" suffix, lowercase.
/// Linux x64 only: engine tests are heavy (full DI + Engine API per test) and timeout on slower CI runners.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
[Platform("Linux")]
public abstract class PyspecEngineBlockchainTestFixture<TSelf> : BlockchainTestBase
{
    [SetUp]
    public void SkipOnArm()
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            Assert.Ignore("Skipped on ARM — exceeds CI timeout");
    }

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => (await RunTest(test)).Pass.Should().BeTrue();

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/blockchain_tests_engine/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("EngineBlockchainTests")}").LoadTests<BlockchainTest>();
}

/// <summary>
/// Generic base for pyspec state tests using <see cref="LoadPyspecTestsStrategy"/>.
/// Directory is derived by convention: strip "StateTests" suffix, lowercase.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecStateTestFixture<TSelf> : GeneralStateTestBase
{
    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => RunTest(test).Pass.Should().BeTrue();

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/state_tests/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("StateTests")}").LoadTests<GeneralStateTest>();
}
