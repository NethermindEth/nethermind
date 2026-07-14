// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core.Test;
using Nethermind.Crypto;
using Nethermind.Serialization.Json;
using Nethermind.Specs;

namespace Nethermind.Test.Runner;

internal class Program
{
    private const int ProgressReportTestInterval = 100;
    private static readonly TimeSpan ProgressReportTimeInterval = TimeSpan.FromMinutes(1);

    public class Options
    {
        public static Option<string> Input { get; } =
            new("--input", "-i") { Description = "Set the test input file or directory." };

        public static Option<string> Filter { get; } =
            new("--run", "--filter", "-f") { Description = "Run only those tests matching the regular expression." };

        public static Option<bool> StateTest { get; } =
            new("--stateTest") { Description = "Run as state test." };

        public static Option<bool> BlockTest { get; } =
            new("--blockTest", "-b") { Description = "Run as blockchain test." };

        public static Option<bool> EngineTest { get; } =
            new("--engineTest", "-e") { Description = "Run as engine test (blockchain_test_engine fixtures)." };

        public static Option<bool> TxTest { get; } =
            new("--txTest") { Description = "Run as transaction test (transaction_tests fixtures: raw tx decoding + validation)." };

        public static Option<bool> ZkEvmTest { get; } =
            new("--zkevmTest") { Description = "Run as zkEVM stateless-execution test (tests-zkevm fixtures: witness input through StatelessExecutor vs expected output)." };

        public static Option<bool> TraceAlways { get; } =
            new("--trace", "-t") { Description = "Set to always trace (by default traces are only generated for failing tests)." };

        public static Option<bool> TraceNever { get; } =
            new("--neverTrace", "-n") { Description = "Set to never trace (by default traces are only generated for failing tests). [Only for State Test]" };

        public static Option<bool> ExcludeMemory { get; } =
            new("--memory", "-m") { Description = "Exclude memory trace." };

        public static Option<bool> ExcludeStack { get; } =
            new("--stack", "-s") { Description = "Exclude stack trace." };

        public static Option<bool> Wait { get; } =
            new("--wait", "-w") { Description = "Wait for input after the test run." };

        public static Option<bool> Stdin { get; } =
            new("--stdin", "-x") { Description = "If stdin is used, the runner will read inputs (filenames) from stdin, and continue executing until empty line is read." };

        public static Option<bool> GnosisTest { get; } =
            new("--gnosisTest", "-g") { Description = "Set test as gnosisTest. if not, it will be by default assumed a mainnet test." };

        public static Option<bool> EnableWarmup { get; } =
            new("--warmup", "-wu") { Description = "Enable warmup for benchmarking purposes." };

        public static Option<bool> JsonOutput { get; } =
            new("--jsonout", "-j") { Description = "Output results as JSON array instead of human-readable format." };

        public static Option<int> Workers { get; } =
            new("--workers", "-p") { Description = "Number of parallel workers for processing fixture files.", DefaultValueFactory = _ => 1 };

        public static Option<string> Chunk { get; } =
            new("--chunk", "-c") { Description = "Run only the Nth of M interleaved chunks of the collected fixture files, e.g. '2of8'. Used to split a large fixture set across CI jobs." };

        public static Option<bool> FlatDb { get; } =
            new("--flatdb") { Description = "Run with the flat state layout (equivalent to setting TEST_USE_FLAT=1)." };

        public static Option<bool?> ParallelExecution { get; } =
            new("--parallelExecution") { Description = "Force BAL parallel execution on or off; when omitted, the client config default is used. [Only for Blockchain/Engine Test]" };

        public static Option<bool?> BatchRead { get; } =
            new("--batchRead") { Description = "Force BAL batch-read prewarming on or off; when omitted, the client config default is used. [Only for Blockchain/Engine Test]" };
    }

    private static readonly IJsonSerializer _serializer = new EthereumJsonSerializer();

    public static async Task<int> Main(params string[] args)
    {
        RootCommand rootCommand =
        [
            Options.Input,
            Options.Filter,
            Options.StateTest,
            Options.BlockTest,
            Options.EngineTest,
            Options.TxTest,
            Options.ZkEvmTest,
            Options.TraceAlways,
            Options.TraceNever,
            Options.ExcludeMemory,
            Options.ExcludeStack,
            Options.Wait,
            Options.Stdin,
            Options.GnosisTest,
            Options.EnableWarmup,
            Options.JsonOutput,
            Options.Workers,
            Options.Chunk,
            Options.FlatDb,
            Options.ParallelExecution,
            Options.BatchRead,
        ];
        rootCommand.SetAction(Run);

        return await rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task<int> Run(ParseResult parseResult, CancellationToken cancellationToken)
    {
        bool isStateTest = parseResult.GetValue(Options.StateTest);
        bool isBlockTest = parseResult.GetValue(Options.BlockTest);
        bool isEngineTest = parseResult.GetValue(Options.EngineTest);
        bool isTxTest = parseResult.GetValue(Options.TxTest);
        bool isZkEvmTest = parseResult.GetValue(Options.ZkEvmTest);

        int testTypeCount = (isStateTest ? 1 : 0) + (isBlockTest ? 1 : 0) + (isEngineTest ? 1 : 0) + (isTxTest ? 1 : 0) + (isZkEvmTest ? 1 : 0);
        if (testTypeCount != 1)
        {
            Console.WriteLine("Please specify one of: --stateTest, --blockTest, --engineTest, --txTest, or --zkevmTest");
            return 1;
        }

        WhenTrace whenTrace = WhenTrace.WhenFailing;
        if (parseResult.GetValue(Options.TraceNever)) whenTrace = WhenTrace.Never;
        if (parseResult.GetValue(Options.TraceAlways)) whenTrace = WhenTrace.Always;

        string input = parseResult.GetValue(Options.Input);
        if (parseResult.GetValue(Options.Stdin)) input = Console.ReadLine();

        // Rlp's and the decoders' static initializers reach into each other via RegisterDecoders;
        // racing the first RLP use across parse workers can deadlock class init. Warm it up here.
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Nethermind.Serialization.Rlp.Rlp).TypeHandle);

        ulong chainId = parseResult.GetValue(Options.GnosisTest) ? GnosisSpecProvider.Instance.ChainId : MainnetSpecProvider.Instance.ChainId;
        bool jsonOutput = parseResult.GetValue(Options.JsonOutput);
        int workers = Math.Max(1, parseResult.GetValue(Options.Workers));
        string filter = parseResult.GetValue(Options.Filter);
        string chunk = parseResult.GetValue(Options.Chunk);
        bool trace = parseResult.GetValue(Options.TraceAlways);
        bool traceMemory = !parseResult.GetValue(Options.ExcludeMemory);
        bool excludeStack = parseResult.GetValue(Options.ExcludeStack);
        bool enableWarmup = parseResult.GetValue(Options.EnableWarmup);
        bool? parallelExecution = parseResult.GetValue(Options.ParallelExecution);
        bool? batchRead = parseResult.GetValue(Options.BatchRead);

        if (parseResult.GetValue(Options.FlatDb))
        {
            // The test fixture bases read TEST_USE_FLAT per test, so setting it here covers
            // both blockchain/engine and state test runs without plumbing a flag through.
            Environment.SetEnvironmentVariable("TEST_USE_FLAT", "1");
        }

        // Pre-warm the thread pool to avoid ramp-up delay (default adds 1 thread/500ms).
        // Cap at processor count to avoid thermal throttling on laptops.
        if (workers > 1)
        {
            ThreadPool.GetMinThreads(out int currentMinWorker, out int currentMinIO);
            int cpuCap = Environment.ProcessorCount;
            int desiredMin = Math.Min(Math.Max(currentMinWorker, workers * 2), cpuCap);
            int desiredMinIO = Math.Min(Math.Max(currentMinIO, workers), cpuCap);
            ThreadPool.SetMinThreads(desiredMin, desiredMinIO);
        }

        while (!string.IsNullOrWhiteSpace(input))
        {
            List<string> files = CollectFiles(input, chunk);

            if (isEngineTest || isBlockTest)
            {
                // Compiled once and shared by the per-file runner instances.
                Regex? filterRegex = filter is not null ? new Regex($"^({filter})", RegexOptions.Compiled) : null;
                BlockchainTestsRunnerOptions runnerOptions = new(
                    Filter: filterRegex,
                    ChainId: chainId,
                    Trace: trace,
                    TraceMemory: traceMemory,
                    ExcludeStack: excludeStack,
                    JsonOutput: true,
                    SuppressOutput: true,
                    ParallelExecution: parallelExecution,
                    ParallelExecutionBatchRead: batchRead);
                List<EthereumTestResult> results = await RunBlockTestFiles(files, runnerOptions, workers);
                Console.Out.Write(_serializer.Serialize(results, true));
            }
            else if (isStateTest)
            {
                List<EthereumTestResult> results = RunStateTestFiles(files, whenTrace, traceMemory, !excludeStack, chainId, filter, enableWarmup, workers);
                Console.Out.Write(_serializer.Serialize(results, true));
            }
            else if (isTxTest)
            {
                List<EthereumTestResult> results = RunTransactionTestFiles(files, filter, workers);
                Console.Out.Write(_serializer.Serialize(results, true));
            }
            else if (isZkEvmTest)
            {
                List<EthereumTestResult> results = RunZkEvmTestFiles(files, filter, workers);
                Console.Out.Write(_serializer.Serialize(results, true));
            }

            if (!parseResult.GetValue(Options.Stdin)) break;
            input = Console.ReadLine();
        }

        if (parseResult.GetValue(Options.Wait)) Console.ReadLine();

        return 0;
    }

    private static List<string> CollectFiles(string path, string? chunk = null)
    {
        List<string> result = [];
        if (File.Exists(path))
        {
            result.Add(path);
        }
        else if (Directory.Exists(path))
        {
            foreach (string file in Directory.GetFiles(path, "*.json", SearchOption.AllDirectories))
            {
                if (!file.Contains("/.meta/") && !file.Contains("\\.meta\\"))
                    result.Add(file);
            }

            // Sorted before chunking so every chunk job sees the same order and the
            // interleaved NofM partition is deterministic across CI jobs.
            result.Sort(StringComparer.Ordinal);
        }

        return string.IsNullOrEmpty(chunk) ? result : [.. TestChunkFilter.FilterByChunk(result, chunk)];
    }

    private static async Task<List<EthereumTestResult>> RunBlockTestFiles(
        List<string> files, BlockchainTestsRunnerOptions baseOptions, int workers)
    {
        await KzgPolynomialCommitments.InitializeAsync();
        int effectiveWorkers = baseOptions.Trace ? 1 : workers;
        int completedTests = 0;
        long lastProgressReportTicks = DateTime.UtcNow.Ticks;
        string? UpdateProgress(bool forceReport)
        {
            int completed = Interlocked.Increment(ref completedTests);
            long nowTicks = DateTime.UtcNow.Ticks;
            bool timeToReport = nowTicks - Volatile.Read(ref lastProgressReportTicks) >= ProgressReportTimeInterval.Ticks;
            if (forceReport || timeToReport || completed % ProgressReportTestInterval == 0)
            {
                Interlocked.Exchange(ref lastProgressReportTicks, nowTicks);
                return $"[{completed}]";
            }

            return null;
        }

        void WriteFileExceptionStatus(string name, Exception ex)
        {
            Console.Error.WriteLine($"\x1b[31mEXCEPTION\x1b[0m {UpdateProgress(true)} {name} - {ex.Message}");
            Console.Error.Flush();
        }

        BlockchainTestsRunnerOptions runnerOptions = baseOptions with { ProgressUpdateFactory = UpdateProgress };

        if (effectiveWorkers <= 1)
        {
            List<EthereumTestResult> allResults = [];
            foreach (string file in files)
            {
                try
                {
                    TestsSourceLoader source = new(new LoadBlockchainTestFileStrategy(), file);
                    BlockchainTestsRunner runner = new(source, runnerOptions);
                    IEnumerable<EthereumTestResult> results = await runner.RunTestsAsync();
                    allResults.AddRange(results);
                }
                catch (Exception ex)
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    WriteFileExceptionStatus(name, ex);
                    allResults.Add(new EthereumTestResult(name, ex.Message));
                }
            }
            return allResults;
        }

        List<(string file, int index)> workItems = [];
        for (int index = 0; index < files.Count; index++)
        {
            workItems.Add((files[index], index));
        }

        IEnumerable<EthereumTestResult>[] resultsByFile = new IEnumerable<EthereumTestResult>[files.Count];
        await Parallel.ForEachAsync(
            workItems,
            new ParallelOptions { MaxDegreeOfParallelism = effectiveWorkers },
            async (item, ct) =>
            {
                try
                {
                    TestsSourceLoader source = new(new LoadBlockchainTestFileStrategy(), item.file);
                    BlockchainTestsRunner runner = new(source, runnerOptions);
                    IEnumerable<EthereumTestResult> results = await runner.RunTestsAsync();
                    resultsByFile[item.index] = results;
                }
                catch (Exception ex)
                {
                    string name = Path.GetFileNameWithoutExtension(item.file);
                    WriteFileExceptionStatus(name, ex);
                    resultsByFile[item.index] = [new EthereumTestResult(name, ex.Message)];
                }
            });

        List<EthereumTestResult> combinedResults = [];
        foreach (IEnumerable<EthereumTestResult> results in resultsByFile)
        {
            combinedResults.AddRange(results);
        }

        return combinedResults;
    }

    private static List<EthereumTestResult> RunStateTestFiles(
        List<string> files, WhenTrace whenTrace, bool traceMemory, bool traceStack,
        ulong chainId, string filter, bool enableWarmup, int workers)
    {
        // Compile filter regex once
        Regex? filterRegex = filter is not null ? new Regex($"^({filter})", RegexOptions.Compiled) : null;

        // Phase 1: Parse files in parallel
        int parseWorkers = Math.Min(workers, files.Count);
        List<GeneralStateTest>[] perFileResults = new List<GeneralStateTest>[files.Count];

        if (parseWorkers > 1 && files.Count > 1)
        {
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = parseWorkers }, i =>
            {
                perFileResults[i] = ParseStateTestFile(files[i], filterRegex);
            });
        }
        else
        {
            for (int i = 0; i < files.Count; i++)
            {
                perFileResults[i] = ParseStateTestFile(files[i], filterRegex);
            }
        }

        List<(int index, GeneralStateTest test)> testCases = [];
        int idx = 0;
        for (int i = 0; i < perFileResults.Length; i++)
        {
            foreach (GeneralStateTest test in perFileResults[i])
            {
                testCases.Add((idx++, test));
            }
        }

        int completedTests = 0;
        // Keeps stderr alive for CI idle watchdogs; also streams failures as they happen.
        void ReportProgress(EthereumTestResult result)
        {
            int completed = Interlocked.Increment(ref completedTests);
            if (!result.Pass)
            {
                Console.Error.WriteLine($"\x1b[31mFAIL\x1b[0m [{completed}/{testCases.Count}] {result.Name} - {result.Error}");
                Console.Error.Flush();
            }
            else if (completed % ProgressReportTestInterval == 0 || completed == testCases.Count)
            {
                Console.Error.WriteLine($"PROGRESS [{completed}/{testCases.Count}]");
                Console.Error.Flush();
            }
        }

        if (workers <= 1 || whenTrace != WhenTrace.Never || enableWarmup)
        {
            List<EthereumTestResult> allResults = [];
            foreach ((int index, GeneralStateTest test) in testCases)
            {
                StateTestsRunner runner = new(whenTrace, traceMemory, traceStack, chainId, filter, enableWarmup, suppressOutput: true);
                EthereumTestResult result = runner.RunSingleTest(test);
                ReportProgress(result);
                allResults.Add(result);
            }
            return allResults;
        }

        EthereumTestResult[] results = new EthereumTestResult[testCases.Count];
        Parallel.ForEach(
            testCases,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            item =>
            {
                StateTestsRunner runner = new(WhenTrace.Never, traceMemory, traceStack, chainId, filter, enableWarmup: false, suppressOutput: true);
                EthereumTestResult result = runner.RunSingleTest(item.test);
                results[item.index] = result;
                ReportProgress(result);
            });

        return [.. results];
    }

    private static List<EthereumTestResult> RunTransactionTestFiles(List<string> files, string? filter, int workers)
    {
        Regex? filterRegex = filter is not null ? new Regex($"^({filter})", RegexOptions.Compiled) : null;

        List<TransactionTest> tests = [];
        foreach (string file in files)
        {
            TestsSourceLoader source = new(new LoadTransactionTestFileStrategy(), file);
            foreach (TransactionTest test in source.LoadTests<TransactionTest>())
            {
                if (filterRegex is not null && test.Name is not null && !filterRegex.IsMatch(test.Name))
                    continue;
                tests.Add(test);
            }
        }

        int completedTests = 0;
        void ReportResult(EthereumTestResult result)
        {
            int completed = Interlocked.Increment(ref completedTests);
            if (!result.Pass)
            {
                Console.Error.WriteLine($"\x1b[31mFAIL\x1b[0m [{completed}/{tests.Count}] {result.Name} - {result.Error}");
                Console.Error.Flush();
            }
            else if (completed % ProgressReportTestInterval == 0 || completed == tests.Count)
            {
                Console.Error.WriteLine($"PROGRESS [{completed}/{tests.Count}]");
                Console.Error.Flush();
            }
        }

        TransactionTestsRunner runner = new();
        EthereumTestResult[] results = new EthereumTestResult[tests.Count];
        if (workers <= 1)
        {
            for (int i = 0; i < tests.Count; i++)
            {
                results[i] = runner.RunSingleTest(tests[i]);
                ReportResult(results[i]);
            }
        }
        else
        {
            Parallel.For(0, tests.Count, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
            {
                results[i] = runner.RunSingleTest(tests[i]);
                ReportResult(results[i]);
            });
        }

        return [.. results];
    }

    private static List<EthereumTestResult> RunZkEvmTestFiles(List<string> files, string? filter, int workers)
    {
        Regex? filterRegex = filter is not null ? new Regex($"^({filter})", RegexOptions.Compiled) : null;

        // Witness fixtures are large, so files are parsed one at a time and released instead of
        // materializing the whole set up front. The test-case total for progress reporting
        // therefore comes from a count-only pre-pass over the same files.
        int CountFile(int index)
        {
            try
            {
                int count = 0;
                TestsSourceLoader source = new(new LoadBlockchainTestFileStrategy(), files[index]);
                foreach (BlockchainTest test in source.LoadTests<BlockchainTest>())
                {
                    if (filterRegex is not null && test.Name is not null && !filterRegex.IsMatch(test.Name))
                        continue;

                    count += ZkEvmTestsRunner.CountCases(test);
                }

                return count;
            }
            catch (Exception)
            {
                // The execution pass reports the failure and yields exactly one EXCEPTION result.
                return 1;
            }
        }

        int[] casesPerFile = new int[files.Count];
        if (workers <= 1)
        {
            for (int i = 0; i < files.Count; i++)
            {
                casesPerFile[i] = CountFile(i);
            }
        }
        else
        {
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = workers }, i =>
            {
                casesPerFile[i] = CountFile(i);
            });
        }

        int totalCases = 0;
        foreach (int cases in casesPerFile)
        {
            totalCases += cases;
        }

        int completedCases = 0;
        List<EthereumTestResult>[] resultsByFile = new List<EthereumTestResult>[files.Count];

        void ProcessFile(int index)
        {
            List<EthereumTestResult> fileResults = [];
            try
            {
                TestsSourceLoader source = new(new LoadBlockchainTestFileStrategy(), files[index]);
                foreach (BlockchainTest test in source.LoadTests<BlockchainTest>())
                {
                    if (filterRegex is not null && test.Name is not null && !filterRegex.IsMatch(test.Name))
                        continue;

                    fileResults.AddRange(ZkEvmTestsRunner.RunTest(test));
                }
            }
            catch (Exception ex)
            {
                string name = Path.GetFileNameWithoutExtension(files[index]);
                Console.Error.WriteLine($"\x1b[31mEXCEPTION\x1b[0m {name} - {ex.Message}");
                Console.Error.Flush();
                fileResults.Add(new EthereumTestResult(name, ex.Message));
            }

            resultsByFile[index] = fileResults;

            if (fileResults.Count == 0)
                return;

            int done = Interlocked.Add(ref completedCases, fileResults.Count);
            foreach (EthereumTestResult result in fileResults)
            {
                if (!result.Pass)
                {
                    Console.Error.WriteLine($"\x1b[31mFAIL\x1b[0m [{done}/{totalCases}] {result.Name} - {result.Error}");
                    Console.Error.Flush();
                }
            }

            int previousDone = done - fileResults.Count;
            if (done == totalCases || done / ProgressReportTestInterval != previousDone / ProgressReportTestInterval)
            {
                Console.Error.WriteLine($"PROGRESS [{done}/{totalCases}]");
                Console.Error.Flush();
            }
        }

        if (workers <= 1)
        {
            for (int i = 0; i < files.Count; i++)
            {
                ProcessFile(i);
            }
        }
        else
        {
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = workers }, ProcessFile);
        }

        List<EthereumTestResult> combinedResults = [];
        foreach (List<EthereumTestResult> fileResults in resultsByFile)
        {
            combinedResults.AddRange(fileResults);
        }

        return combinedResults;
    }

    private static List<GeneralStateTest> ParseStateTestFile(string file, Regex? filterRegex)
    {
        List<GeneralStateTest> tests = [];
        TestsSourceLoader source = new(new LoadGeneralStateTestFileStrategy(), file);
        foreach (GeneralStateTest test in source.LoadTests<GeneralStateTest>())
        {
            if (filterRegex is not null && !filterRegex.IsMatch(test.Name))
                continue;
            tests.Add(test);
        }
        return tests;
    }
}
