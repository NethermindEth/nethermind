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
/// Generic base for pyspec Amsterdam blockchain tests with the parallel BAL-driven
/// transaction executor enabled. Linux x64 only — these are heavy. Loads only the
/// <c>fixtures/blockchain_tests/for_amsterdam</c> directory because parallel execution is
/// gated on EIP-7928 (Amsterdam).
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecAmsterdamParallelBlockchainTestFixture : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => true;
    protected override bool? ParallelExecutionBatchReadOverride => false;

    [SetUp]
    public void SkipUnlessLinuxX64() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) =>
        Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            "fixtures/blockchain_tests/for_amsterdam").LoadTests<BlockchainTest>();
}

/// <summary>
/// Sequential Amsterdam blockchain tests with batch-read prewarming enabled.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecAmsterdamBatchReadBlockchainTestFixture : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => false;
    protected override bool? ParallelExecutionBatchReadOverride => true;

    [SetUp]
    public void SkipUnlessLinuxX64() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) =>
        Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            "fixtures/blockchain_tests/for_amsterdam").LoadTests<BlockchainTest>();
}

/// <summary>
/// Production-config Amsterdam blockchain tests: parallel BAL execution + batch-read prewarming.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecAmsterdamParallelFullBlockchainTestFixture : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => true;
    protected override bool? ParallelExecutionBatchReadOverride => true;

    [SetUp]
    public void SkipUnlessLinuxX64() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) =>
        Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            "fixtures/blockchain_tests/for_amsterdam").LoadTests<BlockchainTest>();
}

/// <summary>
/// Engine variant of <see cref="PyspecAmsterdamParallelBlockchainTestFixture"/>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecAmsterdamParallelEngineBlockchainTestFixture : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => true;
    protected override bool? ParallelExecutionBatchReadOverride => false;

    [SetUp]
    public void SkipUnlessLinuxX64() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) =>
        Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            "fixtures/blockchain_tests_engine/for_amsterdam").LoadTests<BlockchainTest>();
}

/// <summary>
/// Engine variant of <see cref="PyspecAmsterdamBatchReadBlockchainTestFixture"/>.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecAmsterdamBatchReadEngineBlockchainTestFixture : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => false;
    protected override bool? ParallelExecutionBatchReadOverride => true;

    [SetUp]
    public void SkipUnlessLinuxX64() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) =>
        Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            "fixtures/blockchain_tests_engine/for_amsterdam").LoadTests<BlockchainTest>();
}

/// <summary>
/// Engine variant of <see cref="PyspecAmsterdamParallelFullBlockchainTestFixture"/> — production config.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecAmsterdamParallelFullEngineBlockchainTestFixture : BlockchainTestBase
{
    protected override bool? ParallelExecutionOverride => true;
    protected override bool? ParallelExecutionBatchReadOverride => true;

    [SetUp]
    public void SkipUnlessLinuxX64() => CiRunnerGuard.SkipIfNotLinuxX64();

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test) =>
        Assert.That((await RunTest(test)).Pass, Is.True);

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            "fixtures/blockchain_tests_engine/for_amsterdam").LoadTests<BlockchainTest>();
}

/// <summary>
/// Generic base for pyspec sync blockchain tests using <see cref="LoadPyspecTestsStrategy"/>.
/// Sync fixtures share the engine-payload format and additionally ship a <c>syncPayload</c>
/// field exercising sync-mode validation; we run the engine payload through the standard
/// blockchain test harness here and leave the sync-specific payload for a follow-up.
/// Directory is derived by convention: strip "SyncBlockchainTests" suffix, lowercase.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecSyncBlockchainTestFixture<TSelf> : BlockchainTestBase
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
            $"fixtures/blockchain_tests_sync/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("SyncBlockchainTests")}").LoadTests<BlockchainTest>();
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
/// Generic base for pyspec transaction-validation tests using
/// <see cref="LoadPyspecTestsStrategy"/>. Each fixture validates that decoding the raw
/// <c>txbytes</c> against the per-fork expected outcome (success or a specific
/// <c>TransactionException</c> token) lines up with Nethermind's tx decoder + validator.
/// Directory is derived by convention: strip "TransactionTests" suffix, lowercase.
/// </summary>
[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecTransactionTestFixture<TSelf> : TransactionTestBase
{
    [SetUp]
    public void SkipInCiOnUnsupportedRunners() => CiRunnerGuard.SkipIfNotLinuxX64Ci();

    [TestCaseSource(nameof(LoadTests))]
    public void Test(TransactionTest test)
    {
        (bool pass, string failMessage) = RunTest(test);
        Assert.That(pass, Is.True, failMessage);
    }

    public static IEnumerable<TransactionTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/transaction_tests/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("TransactionTests")}").LoadTests<TransactionTest>();
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
