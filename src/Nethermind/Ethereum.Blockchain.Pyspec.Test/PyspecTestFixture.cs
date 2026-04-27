// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.Json;
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

    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(BlockchainTest test)
    {
        LegacyStateTestFixtureGuard.SkipIfBuggyEelsConversion(test);
        Assert.That((await RunTest(test)).Pass, Is.True);
    }

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/blockchain_tests/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("BlockchainTests")}").LoadTests<BlockchainTest>();
}

/// <summary>
/// Generic base for pyspec engine blockchain tests using <see cref="LoadPyspecTestsStrategy"/>.
/// Directory is derived by convention: strip "EngineBlockchainTests" suffix, lowercase.
/// In CI (TEST_CHUNK set), only runs on Linux x64 to stay within the job timeout budget.
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
    public async Task Test(BlockchainTest test)
    {
        LegacyStateTestFixtureGuard.SkipIfBuggyEelsConversion(test);
        Assert.That((await RunTest(test)).Pass, Is.True);
    }

    public static IEnumerable<BlockchainTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/blockchain_tests_engine/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("EngineBlockchainTests")}").LoadTests<BlockchainTest>();
}

/// <summary>
/// Skips pyspec blockchain tests whose fixtures were produced by EELS's legacy state-test
/// conversion path (<c>ported_static/.../*_from_state_test*</c>). For Amsterdam, that path
/// emits a BAL that omits the EIP-2935 / EIP-4788 system pre-block SSTORE entries that the
/// real client actually executes. Telltale signal in the fixture is the legacy difficulty
/// value <c>0x20000</c> baked into the post-merge mixHash field — real prevRandao is
/// effectively random and would never be exactly <c>0x...020000</c>.
/// Track upstream EELS fix; remove when bal@&gt;v5.7.0 ships with the SSTOREs included.
/// </summary>
internal static class LegacyStateTestFixtureGuard
{
    private const string LegacyDifficultySentinelMixHashSuffix = "0000000000000000000000000000000000000000000000000000000000020000";

    public static void SkipIfBuggyEelsConversion(BlockchainTest test)
    {
        if (!HasLegacyDifficultySentinel(test)) return;
        Assert.Ignore($"Skipped — EELS bal@v5.7.0 ported_static fixture for '{test.Name}' omits system pre-block SSTOREs from the BAL (legacy state-test conversion bug).");
    }

    private static bool HasLegacyDifficultySentinel(BlockchainTest test)
    {
        if (test.Blocks is not null)
        {
            foreach (TestBlockJson block in test.Blocks)
            {
                if (Matches(block.BlockHeader?.MixHash))
                    return true;
            }
        }

        if (test.EngineNewPayloads is not null)
        {
            // Engine variant: the post-merge prevRandao field replaces mixHash on the wire,
            // and EELS preserves the same legacy 0x...020000 sentinel through the conversion.
            foreach (TestEngineNewPayloadsJson payload in test.EngineNewPayloads)
            {
                if (payload.Params is null || payload.Params.Length == 0) continue;
                JsonElement param = payload.Params[0];
                if (param.ValueKind != JsonValueKind.Object) continue;
                if (!param.TryGetProperty("prevRandao", out JsonElement prevRandao)) continue;
                if (Matches(prevRandao.GetString()))
                    return true;
            }
        }

        return false;

        // Match `0x` + 64 hex chars ending with the legacy difficulty value 0x20000.
        static bool Matches(string value)
        {
            if (value is null) return false;
            string hex = value.StartsWith("0x", StringComparison.Ordinal) ? value[2..] : value;
            return hex.Length == 64
                && hex.EndsWith(LegacyDifficultySentinelMixHashSuffix, StringComparison.OrdinalIgnoreCase);
        }
    }
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
    public void Test(GeneralStateTest test) => Assert.That(RunTest(test).Pass, Is.True);

    public static IEnumerable<GeneralStateTest> LoadTests() =>
        new TestsSourceLoader(new LoadPyspecTestsStrategy(),
            $"fixtures/state_tests/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>("StateTests")}").LoadTests<GeneralStateTest>();
}

/// <summary>
/// Skips heavy tests in CI on runners that are too slow or running variant builds.
/// Only active when TEST_CHUNK is set (CI). Local runs always execute.
/// Set TEST_SKIP_HEAVY=1 to unconditionally skip (used by checked/no-intrinsics variants).
/// </summary>
internal static class CiRunnerGuard
{
    private static readonly bool s_isCi = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TEST_CHUNK"));
    private static readonly bool s_isLinuxX64 = OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static readonly bool s_skipHeavy = Environment.GetEnvironmentVariable("TEST_SKIP_HEAVY") == "1";

    public static void SkipIfNotLinuxX64()
    {
        if (s_skipHeavy)
            Assert.Ignore("Skipped — TEST_SKIP_HEAVY is set");
        if (s_isCi && !s_isLinuxX64)
            Assert.Ignore("Skipped in CI — engine/Amsterdam tests only run on Linux x64");
    }
}
