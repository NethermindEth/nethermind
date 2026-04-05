// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Ethereum.Test.Base;
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
        // Parse all files into a flat list of individual test cases
        Regex? filterRegex = filter is not null ? new Regex($"^({filter})") : null;
        var testCases = new List<(int index, BlockchainTest test)>();
        int idx = 0;
        foreach (string file in files)
        {
            try
            {
                var source = new TestsSourceLoader(new LoadBlockchainTestFileStrategy(), file);
                foreach (EthereumTest loadedTest in source.LoadTests<EthereumTest>())
                {
                    if (loadedTest is FailedToLoadTest)
                    {
                        testCases.Add((idx++, null));
                        // Record the failure inline below during execution
                        continue;
                    }

                    if (loadedTest is not BlockchainTest bt) continue;

                    if (filterRegex is not null && bt.Name is not null && !filterRegex.Match(bt.Name).Success)
                        continue;

                    if (bt.LoadFailure is not null)
                    {
                        testCases.Add((idx++, bt));
                        continue;
                    }

                    testCases.Add((idx++, bt));
                }
            }
            catch (Exception)
            {
                var name = Path.GetFileNameWithoutExtension(file);
                testCases.Add((idx++, null));
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
                    var runner = new BlockchainTestsRunner(
                        new TestsSourceLoader(new LoadBlockchainTestFileStrategy(), "dummy"),
                        filter, chainId, trace, traceMemory, traceStack, jsonOutput: jsonOutput, suppressOutput: true);
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

        // Parallel execution by individual test case
        var bag = new ConcurrentBag<(int index, EthereumTestResult result)>();
        await Parallel.ForEachAsync(
            testCases,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            async (item, ct) =>
            {
                if (item.test is null || item.test.LoadFailure is not null)
                {
                    bag.Add((item.index, new EthereumTestResult(item.test?.Name, item.test?.LoadFailure ?? "Failed to load test")));
                    return;
                }

                try
                {
                    // Each parallel task creates its own runner since BlockchainTestBase has mutable state
                    var runner = new BlockchainTestsRunner(
                        new TestsSourceLoader(new LoadBlockchainTestFileStrategy(), "dummy"),
                        filter, chainId, trace: false, traceMemory, traceStack, jsonOutput: true, suppressOutput: true);
                    var result = await runner.RunSingleTestAsync(item.test);
                    bag.Add((item.index, result));
                }
                catch (Exception)
                {
                    bag.Add((item.index, new EthereumTestResult(item.test.Name, "Exception during test")));
                }
            });

        return bag.OrderBy(b => b.index).Select(b => b.result).ToList();
    }

    private static List<EthereumTestResult> RunStateTestFiles(
        List<string> files, WhenTrace whenTrace, bool traceMemory, bool traceStack,
        ulong chainId, string filter, bool enableWarmup, int workers)
    {
        // Parse all files into a flat list of individual test cases
        Regex? filterRegex = filter is not null ? new Regex($"^({filter})") : null;
        var testCases = new List<(int index, GeneralStateTest test)>();
        int idx = 0;
        foreach (string file in files)
        {
            var source = new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), file);
            foreach (GeneralStateTest test in source.LoadTests<GeneralStateTest>())
            {
                if (filterRegex is not null && !filterRegex.Match(test.Name).Success)
                    continue;

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

        // Parallel execution by individual test case
        var bag = new ConcurrentBag<(int index, EthereumTestResult result)>();
        Parallel.ForEach(
            testCases,
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            item =>
            {
                // Each parallel task creates its own runner since GeneralStateTestBase has mutable state
                var runner = new StateTestsRunner(
                    new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), "dummy"),
                    WhenTrace.Never, traceMemory, traceStack, chainId, filter, enableWarmup: false, suppressOutput: true);
                var result = runner.RunSingleTest(item.test);
                bag.Add((item.index, result));
            });

        return bag.OrderBy(b => b.index).Select(b => b.result).ToList();
    }
}
