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

// Common base for pyspec blockchain fixtures. `heavy` toggles the skip policy:
// false (default) = skip only in CI on non-Linux-x64 (allows local macOS/Win runs);
// true            = also honor TEST_SKIP_HEAVY=1 for engine/sync/Amsterdam variants.
// Each derived class declares its own [TestCaseSource(nameof(LoadTests))] Test body because
// NUnit resolves the source on the test method's declaring type and rejects non-static sources.
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecBlockchainFixtureBase(bool parallel, bool batchRead, bool heavy) : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => parallel;
    protected override bool? ParallelExecutionBatchReadOverride => batchRead;

    [SetUp]
    public void SkipUnsupportedRunners()
    {
        if (heavy) CiRunnerGuard.SkipIfNotLinuxX64();
        else CiRunnerGuard.SkipIfNotLinuxX64Ci();
    }
}

// Standard pre/post-merge blockchain tests. Fixture dir derived from class name (strip "BlockchainTests").
public abstract class PyspecBlockchainTestFixture<TSelf>() : PyspecBlockchainFixtureBase(parallel: false, batchRead: false, heavy: false)
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<BlockchainTest, TSelf>("blockchain_tests", "BlockchainTests");
}

// Heavy/Linux-x64-only blockchain fixtures: engine payloads, sync-mode payloads, and the
// EIP-7928 (Amsterdam) parallel-BAL / batch-read prewarm matrix.
public abstract class PyspecLinuxX64BlockchainFixture(bool parallel, bool batchRead) : PyspecBlockchainFixtureBase(parallel, batchRead, heavy: true);

// Engine-payload variant. Linux x64 only - heavy job-time budget.
public abstract class PyspecEngineBlockchainTestFixture<TSelf>() : PyspecLinuxX64BlockchainFixture(parallel: false, batchRead: false)
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<BlockchainTest, TSelf>("blockchain_tests_engine", "EngineBlockchainTests");
}

// Sync fixtures share the engine-payload format and additionally ship a `syncPayload` field
// exercising sync-mode validation; we run the engine payload through the standard harness
// here and leave the sync-specific payload for a follow-up.
public abstract class PyspecSyncBlockchainTestFixture<TSelf>() : PyspecLinuxX64BlockchainFixture(parallel: false, batchRead: false)
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<BlockchainTest, TSelf>("blockchain_tests_sync", "SyncBlockchainTests");
}

// Loads only `for_amsterdam` because parallel-BAL execution is gated on EIP-7928.
public abstract class PyspecAmsterdamBlockchainTestFixture(bool parallel, bool batchRead) : PyspecLinuxX64BlockchainFixture(parallel, batchRead)
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.ToTestCases(new TestsSourceLoader(new LoadPyspecTestsStrategy(), "fixtures/blockchain_tests/for_amsterdam")
            .LoadTests<BlockchainTest>());
}

// Engine-payload variant of the Amsterdam fixture; loads from `for_amsterdam` engine tree.
public abstract class PyspecAmsterdamEngineBlockchainTestFixture(bool parallel, bool batchRead) : PyspecLinuxX64BlockchainFixture(parallel, batchRead)
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.ToTestCases(new TestsSourceLoader(new LoadPyspecTestsStrategy(), "fixtures/blockchain_tests_engine/for_amsterdam")
            .LoadTests<BlockchainTest>());
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecStateTestFixture<TSelf> : GeneralStateTestBase
{
    [SetUp]
    public void SkipInCiOnUnsupportedRunners() => CiRunnerGuard.SkipIfNotLinuxX64Ci();

    [TestCaseSource(nameof(LoadTests))]
    public void Test(GeneralStateTest test)
    {
        EthereumTestResult result = RunTest(test);
        Assert.That(result.Pass, Is.True, result.Error);
    }

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<GeneralStateTest, TSelf>("state_tests", "StateTests");
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

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<TransactionTest, TSelf>("transaction_tests", "TransactionTests");
}

internal static class PyspecLoader
{
    public static IEnumerable<T> Load<T, TSelf>(string root, string suffix) where T : EthereumTest =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/{root}/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>(suffix)}").LoadTests<T>();

    public static IEnumerable<TestCaseData> LoadCases<T, TSelf>(string root, string suffix) where T : EthereumTest =>
        ToTestCases(Load<T, TSelf>(root, suffix));

    public static IEnumerable<TestCaseData> ToTestCases<T>(IEnumerable<T> tests) where T : EthereumTest
    {
        int index = 0;
        foreach (T test in tests)
        {
            yield return new TestCaseData(test).SetName(GetTestCaseName(test, index++));
        }
    }

    private static string GetTestCaseName(EthereumTest test, int index)
    {
        string name = test.Name ?? test.ToString() ?? test.GetType().Name;
        return string.IsNullOrEmpty(test.Category)
            ? $"{name}#{index}"
            : $"{test.Category}/{name}#{index}";
    }
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
