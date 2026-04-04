// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
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
        if (workers <= 1)
        {
            List<EthereumTestResult> allResults = [];
            foreach (string file in files)
            {
                try
                {
                    var source = new TestsSourceLoader(new LoadBlockchainTestFileStrategy(), file);
                    var runner = new BlockchainTestsRunner(source, filter, chainId, trace, traceMemory, traceStack, jsonOutput: jsonOutput, suppressOutput: true);
                    var results = await runner.RunTestsAsync();
                    allResults.AddRange(results);
                }
                catch (Exception ex)
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    allResults.Add(new EthereumTestResult(name, ex.Message));
                }
            }
            return allResults;
        }

        // Parallel execution
        var bag = new ConcurrentBag<(int index, List<EthereumTestResult> results)>();
        await Parallel.ForEachAsync(
            files.Select((file, index) => (file, index)),
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            async (item, ct) =>
            {
                try
                {
                    var source = new TestsSourceLoader(new LoadBlockchainTestFileStrategy(), item.file);
                    var runner = new BlockchainTestsRunner(source, filter, chainId, trace: false, traceMemory, traceStack, jsonOutput: true, suppressOutput: true);
                    var results = await runner.RunTestsAsync();
                    bag.Add((item.index, results.ToList()));
                }
                catch (Exception ex)
                {
                    // Test assertion failures (e.g. NUnit Assert) should be captured as failed results
                    var name = Path.GetFileNameWithoutExtension(item.file);
                    bag.Add((item.index, [new EthereumTestResult(name, ex.Message)]));
                }
            });

        return bag.OrderBy(b => b.index).SelectMany(b => b.results).ToList();
    }

    private static List<EthereumTestResult> RunStateTestFiles(
        List<string> files, WhenTrace whenTrace, bool traceMemory, bool traceStack,
        ulong chainId, string filter, bool enableWarmup, int workers)
    {
        if (workers <= 1)
        {
            List<EthereumTestResult> allResults = [];
            foreach (string file in files)
            {
                var source = new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), file);
                var runner = new StateTestsRunner(source, whenTrace, traceMemory, traceStack, chainId, filter, enableWarmup, suppressOutput: true);
                var results = runner.RunTests();
                allResults.AddRange(results);
            }
            return allResults;
        }

        // Parallel execution
        var bag = new ConcurrentBag<(int index, List<EthereumTestResult> results)>();
        Parallel.ForEach(
            files.Select((file, index) => (file, index)),
            new ParallelOptions { MaxDegreeOfParallelism = workers },
            item =>
            {
                var source = new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), item.file);
                var runner = new StateTestsRunner(source, WhenTrace.Never, traceMemory, traceStack, chainId, filter, enableWarmup: false, suppressOutput: true);
                var results = runner.RunTests();
                bag.Add((item.index, results.ToList()));
            });

        return bag.OrderBy(b => b.index).SelectMany(b => b.results).ToList();
    }
}
