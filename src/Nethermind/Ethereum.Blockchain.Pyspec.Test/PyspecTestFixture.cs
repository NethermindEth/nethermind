// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Test;
using NUnit.Framework;

// Each running case reparses its fixture file, which can be hundreds of megabytes.
[assembly: LevelOfParallelism(4)]

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
    public async Task Test(PyspecTestRef testRef) => Assert.That((await RunTest(PyspecLoader.LoadTest<BlockchainTest>(testRef))).Pass, Is.True);

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
    public async Task Test(PyspecTestRef testRef) => Assert.That((await RunTest(PyspecLoader.LoadTest<BlockchainTest>(testRef))).Pass, Is.True);

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<BlockchainTest, TSelf>("blockchain_tests_engine", "EngineBlockchainTests");
}

// Sync fixtures share the engine-payload format and additionally ship a `syncPayload` field
// exercising sync-mode validation; we run the engine payload through the standard harness
// here and leave the sync-specific payload for a follow-up.
public abstract class PyspecSyncBlockchainTestFixture<TSelf>() : PyspecLinuxX64BlockchainFixture(parallel: false, batchRead: false)
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(PyspecTestRef testRef) => Assert.That((await RunTest(PyspecLoader.LoadTest<BlockchainTest>(testRef))).Pass, Is.True);

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<BlockchainTest, TSelf>("blockchain_tests_sync", "SyncBlockchainTests");
}

// Bogota engine-payload fixtures ship in their own release archive, hence the override below.
public abstract class PyspecBogotaEngineBlockchainTestFixture() : PyspecLinuxX64BlockchainFixture(parallel: false, batchRead: false)
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(PyspecTestRef testRef) => Assert.That((await RunTest(PyspecLoader.LoadTest<BlockchainTest>(testRef))).Pass, Is.True);

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<BlockchainTest>(
            new LoadPyspecTestsStrategy
            {
                ArchiveVersion = Constants.FOCIL_ARCHIVE_VERSION,
                ArchiveName = Constants.FOCIL_ARCHIVE_NAME,
            },
            "fixtures/blockchain_tests_engine/for_bogota");
}

// Loads only `for_amsterdam` because parallel-BAL execution is gated on EIP-7928.
public abstract class PyspecAmsterdamBlockchainTestFixture(bool parallel, bool batchRead) : PyspecLinuxX64BlockchainFixture(parallel, batchRead)
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(PyspecTestRef testRef) => Assert.That((await RunTest(PyspecLoader.LoadTest<BlockchainTest>(testRef))).Pass, Is.True);

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<BlockchainTest>(new LoadPyspecTestsStrategy(), "fixtures/blockchain_tests/for_amsterdam");
}

// Engine-payload variant of the Amsterdam fixture; loads from `for_amsterdam` engine tree.
public abstract class PyspecAmsterdamEngineBlockchainTestFixture(bool parallel, bool batchRead) : PyspecLinuxX64BlockchainFixture(parallel, batchRead)
{
    [TestCaseSource(nameof(LoadTests))]
    public async Task Test(PyspecTestRef testRef) => Assert.That((await RunTest(PyspecLoader.LoadTest<BlockchainTest>(testRef))).Pass, Is.True);

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<BlockchainTest>(new LoadPyspecTestsStrategy(), "fixtures/blockchain_tests_engine/for_amsterdam");
}

[TestFixture]
[Parallelizable(ParallelScope.All)]
public abstract class PyspecStateTestFixture<TSelf> : GeneralStateTestBase
{
    [SetUp]
    public void SkipInCiOnUnsupportedRunners() => CiRunnerGuard.SkipIfNotLinuxX64Ci();

    [TestCaseSource(nameof(LoadTests))]
    public void Test(PyspecTestRef testRef) => Assert.That(RunTest(PyspecLoader.LoadTest<GeneralStateTest>(testRef)).Pass, Is.True);

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
    public void Test(PyspecTestRef testRef)
    {
        Result result = RunTest(PyspecLoader.LoadTest<TransactionTest>(testRef));
        Assert.That((bool)result, Is.True, result.Error);
    }

    public static IEnumerable<TestCaseData> LoadTests() =>
        PyspecLoader.LoadCases<TransactionTest, TSelf>("transaction_tests", "TransactionTests");
}

/// <summary>
/// NUnit-retained handle to a single fixture case. The full test object is materialized
/// on demand inside the test body via <see cref="PyspecLoader.LoadTest{T}"/>, so discovery
/// only retains these small handles instead of every parsed fixture for the whole run.
/// </summary>
public sealed record PyspecTestRef(string File, int Occurrence, TestType TestType);

/// <summary>
/// NUnit-retained handle to a single zkEVM stateless case (one block of one fixture).
/// </summary>
public sealed record PyspecStatelessRef(string File, int Occurrence, int BlockIndex);

internal static class PyspecLoader
{
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<RefEntry>>> s_refsCache = new();
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlyList<StatelessEntry>>> s_statelessCache = new();

    // Bounded parallel expansion over fixture files, concatenated in file order so chunking
    // and test names match a sequential pass. Parallelism is capped well below ProcessorCount:
    // each in-flight file transiently holds its raw JSON plus the expanded object graph, and
    // single fixture files reach hundreds of megabytes.
    private static readonly int s_discoveryParallelism = Math.Max(1, Math.Min(4, Environment.ProcessorCount));

    private sealed record RefEntry(PyspecTestRef Ref, string Name, string Category);
    private sealed record StatelessEntry(PyspecStatelessRef Ref, string DisplayName);

    public static IEnumerable<TestCaseData> LoadCases<T, TSelf>(string root, string suffix) where T : EthereumTest =>
        LoadCases<T>(new LoadPyspecTestsStrategy(),
            $"fixtures/{root}/for_{TestDirectoryHelper.GetDirectoryByConvention<TSelf>(suffix)}");

    public static IEnumerable<TestCaseData> LoadCases<T>(LoadPyspecTestsStrategy strategy, string testsDir) where T : EthereumTest
    {
        int index = 0;
        foreach (RefEntry entry in TestChunkFilter.FilterByChunk(GetRefs<T>(strategy, testsDir)))
        {
            yield return new TestCaseData(entry.Ref).SetName(GetTestCaseName(entry.Name, entry.Category, index++));
        }
    }

    /// <summary>
    /// Materializes the fixture case referenced by <paramref name="testRef"/> by re-parsing
    /// its file. The parsed objects become garbage once the test finishes, bounding memory
    /// by concurrent tests instead of the whole fixture set.
    /// </summary>
    public static T LoadTest<T>(PyspecTestRef testRef) where T : EthereumTest
    {
        List<T> tests = LoadFileTests<T>(testRef.File, Path.GetDirectoryName(testRef.File) ?? string.Empty, testRef.TestType, throwOnFailure: true);
        if ((uint)testRef.Occurrence >= (uint)tests.Count)
            throw new InvalidOperationException($"Pyspec fixture '{testRef.File}' holds {tests.Count} tests but case #{testRef.Occurrence} was requested.");
        return tests[testRef.Occurrence];
    }

    public static IEnumerable<TestCaseData> LoadZkEvmStatelessCases(LoadPyspecTestsStrategy strategy, string testsDir)
    {
        foreach (StatelessEntry entry in TestChunkFilter.FilterByChunk(GetStatelessRefs(strategy, testsDir)))
        {
            yield return new TestCaseData(entry.Ref).SetName(entry.DisplayName);
        }
    }

    public static (string InputBytes, string OutputBytes) LoadZkEvmStatelessBytes(PyspecStatelessRef statelessRef)
    {
        BlockchainTest test = LoadTest<BlockchainTest>(new PyspecTestRef(statelessRef.File, statelessRef.Occurrence, TestType.Blockchain));
        if (test.Blocks is not { Length: > 0 } blocks || (uint)statelessRef.BlockIndex >= (uint)blocks.Length)
            throw new InvalidOperationException($"Pyspec fixture '{statelessRef.File}' case #{statelessRef.Occurrence} has no block #{statelessRef.BlockIndex}.");
        TestBlockJson block = blocks[statelessRef.BlockIndex];
        if (block.StatelessInputBytes is null || block.StatelessOutputBytes is null)
            throw new InvalidDataException($"Incomplete stateless fixture data in {test.Name}, block {statelessRef.BlockIndex}.");
        return (block.StatelessInputBytes, block.StatelessOutputBytes);
    }

    public static BlockchainTest LoadZkEvmTest(PyspecTestRef testRef) =>
        ZkEvmFixtures.ZkEvmMutatedWitnessIndex.StampMutatedBlocks([LoadTest<BlockchainTest>(testRef)]).First();

    private static IReadOnlyList<RefEntry> GetRefs<T>(LoadPyspecTestsStrategy strategy, string testsDir) where T : EthereumTest
    {
        string key = $"{strategy.ArchiveVersion}\0{strategy.ArchiveName}\0{testsDir}\0{typeof(T).FullName}";
        return s_refsCache.GetOrAdd(key, _ => new(() => BuildRefs<T>(strategy, testsDir), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    // One pass over the fixture set: parse each file, record a small handle per case,
    // then drop the parsed objects. Only the handles are retained, so discovery holds
    // megabytes instead of the whole parsed set, and fixture variants sharing a
    // directory share the cached result.
    private static IReadOnlyList<RefEntry> BuildRefs<T>(LoadPyspecTestsStrategy strategy, string testsDir) where T : EthereumTest
    {
        string rootDir = strategy.ResolveTestsRoot(testsDir);
        if (!Directory.Exists(rootDir))
            return [];

        TestType testType = LoadPyspecTestsStrategy.GetTestType(testsDir);
        return ExpandFiles(rootDir, testType, (file, directory, type) =>
        {
            List<RefEntry> refs = [];
            int occurrence = 0;
            foreach (T test in LoadFileTests<T>(file, directory, type))
            {
                string name = test.Name ?? test.ToString() ?? test.GetType().Name;
                refs.Add(new RefEntry(new PyspecTestRef(file, occurrence++, type), name, test.Category ?? string.Empty));
            }

            return refs;
        });
    }

    private static IReadOnlyList<StatelessEntry> GetStatelessRefs(LoadPyspecTestsStrategy strategy, string testsDir)
    {
        string key = $"{strategy.ArchiveVersion}\0{strategy.ArchiveName}\0{testsDir}\0stateless";
        return s_statelessCache.GetOrAdd(key, _ => new(() => BuildStatelessRefs(strategy, testsDir), LazyThreadSafetyMode.ExecutionAndPublication)).Value;
    }

    private static IReadOnlyList<StatelessEntry> BuildStatelessRefs(LoadPyspecTestsStrategy strategy, string testsDir)
    {
        string rootDir = strategy.ResolveTestsRoot(testsDir);
        if (!Directory.Exists(rootDir))
            return [];

        TestType testType = LoadPyspecTestsStrategy.GetTestType(testsDir);
        return ExpandFiles(rootDir, testType, (file, directory, type) =>
        {
            List<StatelessEntry> refs = [];
            int occurrence = 0;
            foreach (BlockchainTest test in LoadFileTests<BlockchainTest>(file, directory, type))
            {
                if (test.Blocks is { Length: > 0 } blocks)
                {
                    for (int i = 0; i < blocks.Length; i++)
                    {
                        TestBlockJson block = blocks[i];
                        if (block.StatelessInputBytes is null && block.StatelessOutputBytes is null)
                            continue;
                        if (block.StatelessInputBytes is null || block.StatelessOutputBytes is null)
                            throw new InvalidDataException($"Incomplete stateless fixture data in {test.Name}, block {i}.");
                        refs.Add(new StatelessEntry(new PyspecStatelessRef(file, occurrence, i), $"{test.Name}_stateless_block_{i}"));
                    }
                }
                occurrence++;
            }

            return refs;
        });
    }

    private static List<TOut> ExpandFiles<TOut>(string rootDir, TestType testType, Func<string, string, TestType, List<TOut>> expandFile)
    {
        List<(string File, string Directory)> files = [.. LoadPyspecTestsStrategy.EnumerateTestFiles(rootDir)];
        if (files.Count == 0)
            return [];
        if (files.Count == 1)
            return expandFile(files[0].File, files[0].Directory, testType);

        List<TOut>[] perFile = new List<TOut>[files.Count];
        ExceptionDispatchInfo[] failures = new ExceptionDispatchInfo[files.Count];
        Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = s_discoveryParallelism }, i =>
        {
            try
            {
                perFile[i] = expandFile(files[i].File, files[i].Directory, testType);
            }
            catch (Exception e)
            {
                perFile[i] = [];
                failures[i] = ExceptionDispatchInfo.Capture(e);
            }
        });

        for (int i = 0; i < failures.Length; i++)
            failures[i]?.Throw();

        List<TOut> result = [];
        foreach (List<TOut> list in perFile)
            result.AddRange(list);
        return result;
    }

    // Same per-file pipeline as TestLoadStrategy.LoadTestsFromDirectories: parse, apply
    // fixture exclusions (inside FileTestsSource), then default the category to the directory.
    private static List<T> LoadFileTests<T>(string file, string directory, TestType testType, bool throwOnFailure = false) where T : EthereumTest
    {
        List<T> tests = [];
        foreach (EthereumTest test in new FileTestsSource(file).LoadTests(testType))
        {
            if (throwOnFailure && test is FailedToLoadTest failed)
                throw new InvalidDataException($"Pyspec fixture '{file}': {failed.LoadFailure}");
            if (test is T typed)
            {
                typed.Category ??= directory;
                tests.Add(typed);
            }
        }

        return tests;
    }

    private static string GetTestCaseName(string name, string category, int index) =>
        string.IsNullOrEmpty(category) ? $"{name}#{index}" : $"{category}/{name}#{index}";
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
