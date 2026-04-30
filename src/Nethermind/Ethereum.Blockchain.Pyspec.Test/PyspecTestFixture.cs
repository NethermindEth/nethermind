// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ethereum.Test.Base;
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
    protected override bool? ParallelExecutionOverride => false;

    protected override bool? ParallelExecutionBatchReadOverride => false;

    [SetUp]
    public void SkipInCiOnUnsupportedRunners() => CiRunnerGuard.SkipIfNotLinuxX64Ci();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) =>
        Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/blockchain_tests/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("BlockchainTests")}").LoadTests<BlockchainTest>();
}

/// <summary>
/// Generic base for pyspec engine blockchain tests using <see cref="LoadPyspecTestsStrategy"/>.
/// Directory is derived by convention: strip "EngineBlockchainTests" suffix, lowercase.
/// In CI, only runs on Linux x64 to stay within the job timeout budget.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecEngineBlockchainTestFixture<TSelf> : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => false;

    protected override bool? ParallelExecutionBatchReadOverride => false;

    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) =>
        Assert.That((await RunTest(test)).Pass, Is.True);

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
    [SetUp]
    public void SkipInCiOnUnsupportedRunners() => CiRunnerGuard.SkipIfNotLinuxX64Ci();

    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => Assert.That(RunTest(test).Pass, Is.True);

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/state_tests/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("StateTests")}").LoadTests<GeneralStateTest>();
}

/// <summary>
/// Skips heavy tests in CI on runners that are too slow or running variant builds.
/// Local runs always execute, even when TEST_CHUNK is set.
/// Set TEST_SKIP_HEAVY=1 in CI to skip heavy tests (used by checked/no-intrinsics variants).
/// </summary>
internal static class CiRunnerGuard
{
    private static readonly bool s_isCi = IsCi();
    private static readonly bool s_isLinuxX64 = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static readonly bool s_skipHeavy = Environment.GetEnvironmentVariable("TEST_SKIP_HEAVY") == "1";

    public static void SkipIfNotLinuxX64Ci()
    {
        if (s_isCi && !s_isLinuxX64)
            Assert.Ignore("Skipped in CI — Pyspec generated fixture shards only run on Linux x64 runners");
    }

    public static void SkipIfNotLinuxX64()
    {
        if (s_isCi && s_skipHeavy)
            Assert.Ignore("Skipped — TEST_SKIP_HEAVY is set");
        if (s_isCi && !s_isLinuxX64)
            Assert.Ignore("Skipped in CI — engine/Amsterdam tests only run on Linux x64");
    }

    private static bool IsCi() =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
}
