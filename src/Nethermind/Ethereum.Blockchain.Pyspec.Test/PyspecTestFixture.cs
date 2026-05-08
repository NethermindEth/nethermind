// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test;

// Standard pre/post-merge blockchain tests. Fixture dir derived from class name (strip "BlockchainTests").
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecBlockchainTestFixture<TSelf> : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => false;
    protected override bool? ParallelExecutionBatchReadOverride => false;

    [SetUp]
    public void SkipInCiOnUnsupportedRunners() => CiRunnerGuard.SkipIfNotLinuxX64Ci();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        PyspecLoader.Load<BlockchainTest, TSelf>("blockchain_tests", "BlockchainTests");
}

// Engine-payload variant. Linux x64 only - heavy job-time budget.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecEngineBlockchainTestFixture<TSelf> : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => false;
    protected override bool? ParallelExecutionBatchReadOverride => false;

    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        PyspecLoader.Load<BlockchainTest, TSelf>("blockchain_tests_engine", "EngineBlockchainTests");
}

// Sync fixtures share the engine-payload format and additionally ship a `syncPayload` field
// exercising sync-mode validation; we run the engine payload through the standard harness
// here and leave the sync-specific payload for a follow-up.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecSyncBlockchainTestFixture<TSelf> : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => false;
    protected override bool? ParallelExecutionBatchReadOverride => false;

    [SetUp]
    public void SkipInCiOnSlowRunners() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        PyspecLoader.Load<BlockchainTest, TSelf>("blockchain_tests_sync", "SyncBlockchainTests");
}

// EIP-7928 (Amsterdam) parallel-BAL execution / batch-read prewarm matrix - Linux x64 only.
// Loads only `for_amsterdam` because parallel execution is gated on EIP-7928.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecAmsterdamBlockchainTestFixture(bool parallel, bool batchRead) : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => parallel;
    protected override bool? ParallelExecutionBatchReadOverride => batchRead;

    [SetUp]
    public void SkipUnlessLinuxX64() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(), "fixtures/blockchain_tests/for_amsterdam")
            .LoadTests<BlockchainTest>();
}

// Engine-payload variant of Amsterdam fixture; loads from `for_amsterdam` engine tree.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecAmsterdamEngineBlockchainTestFixture(bool parallel, bool batchRead) : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => parallel;
    protected override bool? ParallelExecutionBatchReadOverride => batchRead;

    [SetUp]
    public void SkipUnlessLinuxX64() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(), "fixtures/blockchain_tests_engine/for_amsterdam")
            .LoadTests<BlockchainTest>();
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecStateTestFixture<TSelf> : GeneralStateTestBase
{
    [SetUp]
    public void SkipInCiOnUnsupportedRunners() => CiRunnerGuard.SkipIfNotLinuxX64Ci();

    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test) => Assert.That(RunTest(test).Pass, Is.True);

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        PyspecLoader.Load<GeneralStateTest, TSelf>("state_tests", "StateTests");
}

// Tx-validation fixtures: decode raw txbytes, run TxValidator, compare against per-fork expected exception.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecTransactionTestFixture<TSelf> : TransactionTestBase
{
    [SetUp]
    public void SkipInCiOnUnsupportedRunners() => CiRunnerGuard.SkipIfNotLinuxX64Ci();

    [TestCaseSource(nameof(LoadTests))]
    public void Test(TransactionTest test)
    {
        Result result = RunTest(test);
        Assert.That((bool)result, Is.True, result.Error);
    }

    public static IEnumerable<TransactionTest> LoadTests() =>
        PyspecLoader.Load<TransactionTest, TSelf>("transaction_tests", "TransactionTests");
}

internal static class PyspecLoader
{
    public static IEnumerable<T> Load<T, TSelf>(string root, string suffix) where T : EthereumTest =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/{root}/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>(suffix)}").LoadTests<T>();
}

// Skips heavy tests in CI on runners that are too slow or running variant builds.
// Local runs always execute. Set TEST_SKIP_HEAVY=1 in CI for checked/no-intrinsics variants.
internal static class CiRunnerGuard
{
    private static readonly bool s_isCi = IsCi();
    private static readonly bool s_isLinuxX64 = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static readonly bool s_skipHeavy = Environment.GetEnvironmentVariable("TEST_SKIP_HEAVY") == "1";

    public static void SkipIfNotLinuxX64Ci()
    {
        if (s_isCi && !s_isLinuxX64)
            Assert.Ignore("Skipped in CI - Pyspec generated fixture shards only run on Linux x64 runners");
    }

    public static void SkipIfNotLinuxX64()
    {
        if (s_isCi && s_skipHeavy)
            Assert.Ignore("Skipped - TEST_SKIP_HEAVY is set");
        if (s_isCi && !s_isLinuxX64)
            Assert.Ignore("Skipped in CI - engine/Amsterdam tests only run on Linux x64");
    }

    private static bool IsCi() =>
        string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
}
