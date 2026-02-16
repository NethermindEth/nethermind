// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using BenchmarkDotNet.Attributes;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Blockchain;
using Nethermind.Evm.State;
using Nethermind.Evm.Tracing;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;

namespace Nethermind.Evm.Benchmark.GasBenchmarks;

/// <summary>
/// Benchmarks that replay gas-benchmark payload files via TransactionProcessor.
/// Test cases are auto-discovered from the gas-benchmarks submodule.
/// </summary>
[Config(typeof(GasBenchmarkConfig))]
public class GasPayloadBenchmarks
{
    private static readonly string s_repoRoot = FindRepoRoot();
    private static readonly string s_gasBenchmarksRoot = Path.Combine(s_repoRoot, "tools", "gas-benchmarks");
    private static readonly string s_testingDir = Path.Combine(s_gasBenchmarksRoot, "eest_tests", "testing");
    private static readonly string s_setupDir = Path.Combine(s_gasBenchmarksRoot, "eest_tests", "setup");
    internal static readonly string s_genesisPath = Path.Combine(s_gasBenchmarksRoot, "scripts", "genesisfiles", "nethermind", "zkevmgenesis.json");
    private static bool s_missingSubmoduleWarned;

    private IWorldState _state;
    private IDisposable _stateScope;
    private ITransactionProcessor _txProcessor;
    private Transaction[] _testTransactions;
    private BlockHeader _testHeader;

    [ParamsSource(nameof(GetTestCases))]
    public TestCase Scenario { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        IReleaseSpec pragueSpec = Prague.Instance;
        ISpecProvider specProvider = new SingleReleaseSpecProvider(pragueSpec, 1, 1);

        // Load genesis state once (shared across all test cases)
        PayloadLoader.EnsureGenesisInitialized(s_genesisPath, pragueSpec);

        // Create a fresh WorldState and open scope at genesis
        _state = PayloadLoader.CreateWorldState();
        BlockHeader genesisBlock = new(Keccak.Zero, Keccak.OfAnEmptySequenceRlp, Address.Zero, 0, 0, 0, 0, Array.Empty<byte>())
        {
            StateRoot = PayloadLoader.GenesisStateRoot
        };
        _stateScope = _state.BeginScope(genesisBlock);

        // Set up EVM infrastructure
        TestBlockhashProvider blockhashProvider = new();
        EthereumCodeInfoRepository codeInfoRepo = new(_state);
        EthereumVirtualMachine vm = new(blockhashProvider, specProvider, LimboLogs.Instance);

        _txProcessor = new EthereumTransactionProcessor(
            BlobBaseFeeCalculator.Instance,
            specProvider,
            _state,
            vm,
            codeInfoRepo,
            LimboLogs.Instance);

        // Execute setup payload if one exists for this scenario
        string setupFile = FindSetupFile(Scenario.FileName);
        if (setupFile is not null)
        {
            (BlockHeader setupHeader, Transaction[] setupTxs) = PayloadLoader.LoadPayload(setupFile);
            _txProcessor.SetBlockExecutionContext(setupHeader);
            for (int i = 0; i < setupTxs.Length; i++)
            {
                _txProcessor.Execute(setupTxs[i], NullTxTracer.Instance);
            }
            _state.Commit(pragueSpec);
        }

        // Parse the test payload
        (BlockHeader header, Transaction[] txs) = PayloadLoader.LoadPayload(Scenario.FilePath);
        _testHeader = header;
        _testTransactions = txs;
        _txProcessor.SetBlockExecutionContext(_testHeader);

        // Warm up: execute once via CallAndRestore to prime code caches
        for (int i = 0; i < _testTransactions.Length; i++)
        {
            _txProcessor.CallAndRestore(_testTransactions[i], NullTxTracer.Instance);
        }
    }

    [Benchmark]
    public void ExecutePayload()
    {
        for (int i = 0; i < _testTransactions.Length; i++)
        {
            _txProcessor.BuildUp(_testTransactions[i], NullTxTracer.Instance);
        }

        _state.Reset();
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        _stateScope?.Dispose();
        _stateScope = null;
        _state = null;
        _txProcessor = null;
        _testTransactions = null;
    }

    /// <summary>
    /// Auto-discovers test cases from the gas-benchmarks testing directory.
    /// </summary>
    public static IEnumerable<TestCase> GetTestCases()
    {
        if (!Directory.Exists(s_testingDir))
        {
            if (!s_missingSubmoduleWarned)
            {
                s_missingSubmoduleWarned = true;
                string hint = "\u001b[33m[GasPayloadBenchmarks] No test cases found.\u001b[0m Initialize the gas-benchmarks submodule:\n" +
                    "  \u001b[36mgit lfs install && git submodule update --init tools/gas-benchmarks\u001b[0m";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    hint += "\n  On Windows, you may also need: \u001b[36mgit config --global core.longpaths true\u001b[0m";
                Console.Error.WriteLine(hint);
            }

            yield break;
        }

        string[] dirs = Directory.GetDirectories(s_testingDir);
        Array.Sort(dirs);

        int globalIndex = 0;
        int chunkIndex = GasBenchmarkConfig.ChunkIndex;
        int chunkTotal = GasBenchmarkConfig.ChunkTotal;

        for (int d = 0; d < dirs.Length; d++)
        {
            string[] files = Directory.GetFiles(dirs[d], "*.txt");
            Array.Sort(files);
            for (int f = 0; f < files.Length; f++)
            {
                if (chunkTotal > 0 && (globalIndex % chunkTotal) != (chunkIndex - 1))
                {
                    globalIndex++;
                    continue;
                }
                globalIndex++;
                yield return new TestCase(files[f]);
            }
        }
    }

    /// <summary>
    /// Finds the setup payload file matching a given test filename, if any.
    /// Setup files share the same filename as test files but live under setup/ directories.
    /// </summary>
    internal static string FindSetupFile(string testFileName)
    {
        if (!Directory.Exists(s_setupDir))
            return null;

        string[] setupDirs = Directory.GetDirectories(s_setupDir);
        for (int i = 0; i < setupDirs.Length; i++)
        {
            string candidate = Path.Combine(setupDirs[i], testFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string FindRepoRoot()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not find repository root (.git directory).");
    }

    /// <summary>
    /// Represents a single gas-benchmark test case with a short display name.
    /// </summary>
    public sealed class TestCase
    {
        public string FilePath { get; }
        public string FileName { get; }
        public string DisplayName { get; }

        public TestCase(string filePath)
        {
            FilePath = filePath;
            FileName = Path.GetFileName(filePath);
            DisplayName = ExtractShortName(FileName);
        }

        public override string ToString() => DisplayName;

        /// <summary>
        /// Extracts a short benchmark name from the long filename.
        /// Input: tests_benchmark_compute_instruction_test_foo.py__test_bar[fork_Prague-benchmark-blockchain_test_engine_x-param1-param2]-gas-value_100M.txt
        /// Output: bar[param1-param2]
        /// </summary>
        private static string ExtractShortName(string fileName)
        {
            // Find test method name after "__test_"
            int testMethodStart = fileName.IndexOf("__test_", StringComparison.Ordinal);
            if (testMethodStart < 0)
                return fileName;

            string afterTestPrefix = fileName.Substring(testMethodStart + 7);

            // Remove the "-gas-value_*" suffix
            int gasValueIdx = afterTestPrefix.IndexOf("-gas-value_", StringComparison.Ordinal);
            if (gasValueIdx >= 0)
                afterTestPrefix = afterTestPrefix.Substring(0, gasValueIdx);

            // Remove the "[fork_Prague-benchmark-blockchain_test_engine_x-" prefix from params
            const string forkPrefix = "[fork_Prague-benchmark-blockchain_test_engine_x-";
            int forkIdx = afterTestPrefix.IndexOf(forkPrefix, StringComparison.Ordinal);
            if (forkIdx >= 0)
            {
                string methodName = afterTestPrefix.Substring(0, forkIdx);
                string paramsStr = afterTestPrefix.Substring(forkIdx + forkPrefix.Length);

                // Remove trailing ']'
                if (paramsStr.EndsWith("]"))
                    paramsStr = paramsStr.Substring(0, paramsStr.Length - 1);

                return string.IsNullOrEmpty(paramsStr)
                    ? methodName
                    : methodName + "[" + paramsStr + "]";
            }

            return afterTestPrefix;
        }
    }
}
