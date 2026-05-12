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
        ];
        rootCommand.SetAction(Run);

        return await rootCommand.Parse(args).InvokeAsync();
    }

    private static async Task<int> Run(ParseResult parseResult, CancellationToken cancellationToken)
    {
        bool isStateTest = parseResult.GetValue(Options.StateTest);
        bool isBlockTest = parseResult.GetValue(Options.BlockTest);
        bool isEngineTest = parseResult.GetValue(Options.EngineTest);

        int testTypeCount = (isStateTest ? 1 : 0) + (isBlockTest ? 1 : 0) + (isEngineTest ? 1 : 0);
        if (testTypeCount != 1)
        {
            Console.WriteLine("Please specify one of: --stateTest, --blockTest, or --engineTest");
            return 1;
        }

        WhenTrace whenTrace = WhenTrace.WhenFailing;
        if (parseResult.GetValue(Options.TraceNever)) whenTrace = WhenTrace.Never;
        if (parseResult.GetValue(Options.TraceAlways)) whenTrace = WhenTrace.Always;

        string input = parseResult.GetValue(Options.Input);
        if (parseResult.GetValue(Options.Stdin)) input = Console.ReadLine();

        ulong chainId = parseResult.GetValue(Options.GnosisTest) ? GnosisSpecProvider.Instance.ChainId : MainnetSpecProvider.Instance.ChainId;
        bool jsonOutput = parseResult.GetValue(Options.JsonOutput);
        int workers = Math.Max(1, parseResult.GetValue(Options.Workers));
        string filter = parseResult.GetValue(Options.Filter);
        bool trace = parseResult.GetValue(Options.TraceAlways);
        bool traceMemory = !parseResult.GetValue(Options.ExcludeMemory);
        bool excludeStack = parseResult.GetValue(Options.ExcludeStack);
        bool enableWarmup = parseResult.GetValue(Options.EnableWarmup);

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
            List<string> files = CollectFiles(input);

            if (isEngineTest || isBlockTest)
            {
                bool forceJson = isEngineTest || isBlockTest || jsonOutput;
                List<EthereumTestResult> results = await RunBlockTestFiles(files, filter, chainId, trace, traceMemory, excludeStack, forceJson, workers);
                if (forceJson)
                    Console.Out.Write(_serializer.Serialize(results, true));
            }
            else if (isStateTest)
            {
                List<EthereumTestResult> results = RunStateTestFiles(files, whenTrace, traceMemory, !excludeStack, chainId, filter, enableWarmup, workers);
                Console.Out.Write(_serializer.Serialize(results, true));
            }

            if (!parseResult.GetValue(Options.Stdin)) break;
            input = Console.ReadLine();
        }

        if (parseResult.GetValue(Options.Wait)) Console.ReadLine();

        return 0;
    }

    private static List<string> CollectFiles(string path)
    {
        if (File.Exists(path))
            return [path];

        if (Directory.Exists(path))
        {
            List<string> result = [];
            foreach (string file in Directory.GetFiles(path, "*.json", SearchOption.AllDirectories))
            {
                if (!file.Contains("/.meta/") && !file.Contains("\\.meta\\"))
                    result.Add(file);
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        return [];
    }

    private static async Task<List<EthereumTestResult>> RunBlockTestFiles(
        List<string> files, string filter, ulong chainId,
        bool trace, bool traceMemory, bool excludeStack,
        bool jsonOutput, int workers)
    {
        await KzgPolynomialCommitments.InitializeAsync();
        int effectiveWorkers = trace ? 1 : workers;
        int completedTests = 0;
        long lastProgressReportTicks = DateTime.UtcNow.Ticks;
        int totalTests = CountBlockTestCases(files, filter, effectiveWorkers);
        string? UpdateProgress(bool forceReport)
        {
            int completed = Interlocked.Increment(ref completedTests);
            long nowTicks = DateTime.UtcNow.Ticks;
            bool timeToReport = nowTicks - Volatile.Read(ref lastProgressReportTicks) >= ProgressReportTimeInterval.Ticks;
            if (forceReport || timeToReport || completed % ProgressReportTestInterval == 0 || completed == totalTests)
            {
                Interlocked.Exchange(ref lastProgressReportTicks, nowTicks);
                return $"[{completed}/{totalTests}]";
            }

            return null;
        }

        void WriteFileExceptionStatus(string name, Exception ex)
        {
            Console.Error.WriteLine($"\x1b[31mEXCEPTION\x1b[0m {UpdateProgress(true)} {name} - {ex.Message}");
            Console.Error.Flush();
        }

        if (effectiveWorkers <= 1)
        {
            List<EthereumTestResult> allResults = [];
            foreach (string file in files)
            {
                try
                {
                    TestsSourceLoader source = new(new LoadBlockchainTestFileStrategy(), file);
                    BlockchainTestsRunner runner = new(source, filter, chainId, trace, traceMemory, excludeStack, jsonOutput: jsonOutput, suppressOutput: true, progressUpdateFactory: UpdateProgress);
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
                    BlockchainTestsRunner runner = new(source, filter, chainId, trace, traceMemory, excludeStack, jsonOutput: true, suppressOutput: true, progressUpdateFactory: UpdateProgress);
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

    private static int CountBlockTestCases(List<string> files, string filter, int workers)
    {
        Regex? filterRegex = filter is not null ? new Regex($"^({filter})", RegexOptions.Compiled) : null;

        if (workers <= 1)
        {
            int total = 0;
            foreach (string file in files)
            {
                total += CountBlockTestCasesInFile(file, filterRegex);
            }
            return total;
        }

        int totalTests = 0;
        Parallel.ForEach(
            files,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            file =>
            {
                int testsInFile = CountBlockTestCasesInFile(file, filterRegex);
                Interlocked.Add(ref totalTests, testsInFile);
            });

        return totalTests;
    }

    private static int CountBlockTestCasesInFile(string file, Regex? filterRegex)
    {
        try
        {
            int count = 0;
            TestsSourceLoader source = new(new LoadBlockchainTestFileStrategy(), file);
            foreach (EthereumTest loadedTest in source.LoadTests<EthereumTest>())
            {
                if (loadedTest is FailedToLoadTest)
                {
                    count++;
                    continue;
                }

                if (loadedTest is not BlockchainTest test)
                    continue;

                if (filterRegex is not null && test.Name is not null && !filterRegex.IsMatch(test.Name))
                    continue;

                count++;
            }

            return count;
        }
        catch (Exception)
        {
            return 1;
        }
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

        if (workers <= 1 || whenTrace != WhenTrace.Never || enableWarmup)
        {
            List<EthereumTestResult> allResults = [];
            foreach ((int index, GeneralStateTest test) in testCases)
            {
                StateTestsRunner runner = new(
                    new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), "dummy"),
                    whenTrace, traceMemory, traceStack, chainId, filter, enableWarmup, suppressOutput: true);
                EthereumTestResult result = runner.RunSingleTest(test);
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
                StateTestsRunner runner = new(
                    new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), "dummy"),
                    WhenTrace.Never, traceMemory, traceStack, chainId, filter, enableWarmup: false, suppressOutput: true);
                EthereumTestResult result = runner.RunSingleTest(item.test);
                results[item.index] = result;
            });

        return [.. results];
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
