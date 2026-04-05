// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
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
            return 0;
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
        bool traceStack = parseResult.GetValue(Options.ExcludeStack);
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
                bool forceJson = isEngineTest || jsonOutput;
                var results = await RunBlockTestFiles(files, filter, chainId, trace, traceMemory, traceStack, forceJson, workers);
                if (forceJson)
                    Console.Out.Write(_serializer.Serialize(results, true));
            }
            else if (isStateTest)
            {
                var results = RunStateTestFiles(files, whenTrace, traceMemory, traceStack, chainId, filter, enableWarmup, workers);
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
            return Directory.GetFiles(path, "*.json", SearchOption.AllDirectories)
                .Where(f => !f.Contains("/.meta/") && !f.Contains("\\.meta\\"))
                .OrderBy(f => f)
                .ToList();

        return [];
    }

    private static async Task<List<EthereumTestResult>> RunBlockTestFiles(
        List<string> files, string filter, ulong chainId,
        bool trace, bool traceMemory, bool traceStack,
        bool jsonOutput, int workers)
    {
        // Pre-initialize KZG once for all tests (avoids per-test initialization check)
        await KzgPolynomialCommitments.InitializeAsync();

        // Compile filter regex once (Compiled flag enables JIT compilation for faster matching)
        Regex? filterRegex = filter is not null ? new Regex($"^({filter})", RegexOptions.Compiled) : null;

        // Phase 1: Parse all files in parallel into per-file test lists
        int parseWorkers = Math.Min(workers, files.Count);
        var perFileResults = new List<BlockchainTest>[files.Count];

        if (parseWorkers > 1 && files.Count > 1)
        {
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = parseWorkers }, i =>
            {
                perFileResults[i] = ParseBlockchainTestFile(files[i], filterRegex);
            });
        }
        else
        {
            for (int i = 0; i < files.Count; i++)
            {
                perFileResults[i] = ParseBlockchainTestFile(files[i], filterRegex);
            }
        }

        // Flatten into indexed test case list
        var testCases = new List<(int index, BlockchainTest test)>();
        int idx = 0;
        for (int i = 0; i < perFileResults.Length; i++)
        {
            foreach (var test in perFileResults[i])
            {
                testCases.Add((idx++, test));
            }
        }

        if (workers <= 1)
        {
            List<EthereumTestResult> allResults = [];
            foreach (var (index, test) in testCases)
            {
                if (test is null || test.LoadFailure is not null)
                {
                    allResults.Add(new EthereumTestResult(test?.Name, test?.LoadFailure ?? "Failed to load test"));
                    continue;
                }

                try
                {
                    var runner = new BlockchainTestsRunner(filter, chainId, trace, traceMemory, traceStack, jsonOutput: jsonOutput, suppressOutput: true);
                    var result = await runner.RunSingleTestAsync(test);
                    allResults.Add(result);
                }
                catch (Exception)
                {
                    allResults.Add(new EthereumTestResult(test.Name, "Exception during test"));
                }
            }
            return allResults;
        }

        // Phase 2: Execute tests in parallel — use pre-allocated array instead of ConcurrentBag
        var results = new EthereumTestResult[testCases.Count];
        await Parallel.ForEachAsync(
            testCases,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            async (item, ct) =>
            {
                if (item.test is null || item.test.LoadFailure is not null)
                {
                    results[item.index] = new EthereumTestResult(item.test?.Name, item.test?.LoadFailure ?? "Failed to load test");
                    return;
                }

                try
                {
                    // Each parallel task creates its own runner since BlockchainTestBase has mutable state
                    var runner = new BlockchainTestsRunner(filter, chainId, trace: false, traceMemory, traceStack, jsonOutput: true, suppressOutput: true);
                    var result = await runner.RunSingleTestAsync(item.test);
                    results[item.index] = result;
                }
                catch (Exception)
                {
                    results[item.index] = new EthereumTestResult(item.test.Name, "Exception during test");
                }
            });

        return results.ToList();
    }

    private static List<BlockchainTest> ParseBlockchainTestFile(string file, Regex? filterRegex)
    {
        var tests = new List<BlockchainTest>();
        try
        {
            var source = new TestsSourceLoader(new LoadBlockchainTestFileStrategy(), file);
            foreach (EthereumTest loadedTest in source.LoadTests<EthereumTest>())
            {
                if (loadedTest is FailedToLoadTest)
                {
                    tests.Add(null);
                    continue;
                }

                if (loadedTest is not BlockchainTest bt) continue;

                if (filterRegex is not null && bt.Name is not null && !filterRegex.Match(bt.Name).Success)
                    continue;

                tests.Add(bt);
            }
        }
        catch (Exception)
        {
            tests.Add(null);
        }

        return tests;
    }

    private static List<EthereumTestResult> RunStateTestFiles(
        List<string> files, WhenTrace whenTrace, bool traceMemory, bool traceStack,
        ulong chainId, string filter, bool enableWarmup, int workers)
    {
        // Compile filter regex once
        Regex? filterRegex = filter is not null ? new Regex($"^({filter})", RegexOptions.Compiled) : null;

        // Phase 1: Parse files in parallel
        int parseWorkers = Math.Min(workers, files.Count);
        var perFileResults = new List<GeneralStateTest>[files.Count];

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

        // Flatten
        var testCases = new List<(int index, GeneralStateTest test)>();
        int idx = 0;
        for (int i = 0; i < perFileResults.Length; i++)
        {
            foreach (var test in perFileResults[i])
            {
                testCases.Add((idx++, test));
            }
        }

        if (workers <= 1)
        {
            List<EthereumTestResult> allResults = [];
            foreach (var (index, test) in testCases)
            {
                var runner = new StateTestsRunner(
                    new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), "dummy"),
                    whenTrace, traceMemory, traceStack, chainId, filter, enableWarmup, suppressOutput: true);
                var result = runner.RunSingleTest(test);
                allResults.Add(result);
            }
            return allResults;
        }

        // Phase 2: Execute in parallel with pre-allocated array
        var results = new EthereumTestResult[testCases.Count];
        Parallel.ForEach(
            testCases,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            item =>
            {
                var runner = new StateTestsRunner(
                    new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), "dummy"),
                    WhenTrace.Never, traceMemory, traceStack, chainId, filter, enableWarmup: false, suppressOutput: true);
                var result = runner.RunSingleTest(item.test);
                results[item.index] = result;
            });

        return results.ToList();
    }

    private static List<GeneralStateTest> ParseStateTestFile(string file, Regex? filterRegex)
    {
        var tests = new List<GeneralStateTest>();
        var source = new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), file);
        foreach (GeneralStateTest test in source.LoadTests<GeneralStateTest>())
        {
            if (filterRegex is not null && !filterRegex.Match(test.Name).Success)
                continue;
            tests.Add(test);
        }
        return tests;
    }
}
