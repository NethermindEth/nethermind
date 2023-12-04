// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Threading.Tasks;
using CommandLine;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;

namespace Nethermind.Test.Runner
{
    internal class Program
    {
        public class Options
        {
            [Option('i', "input", Required = false, HelpText = "Set the state test input file or directory. Either 'input' or 'stdin' is required")]
            public string Input { get; set; }

            [Option('f', "filter", Required = false, HelpText = "Set the state test name that you want to run.")]
            public string Filter { get; set; }

            [Option('b', "blockTest", Required = false, HelpText = "Set test as blockTest. if not, it will be by default assumed a state test.")]
            public bool BlockTest { get; set; }

            [Option('t', "trace", Required = false, HelpText = "Set to always trace (by default traces are only generated for failing tests). [Only for State Test]")]
            public bool TraceAlways { get; set; }

            [Option('n', "neverTrace", Required = false, HelpText = "Set to never trace (by default traces are only generated for failing tests). [Only for State Test]")]
            public bool TraceNever { get; set; }

            [Option('m', "memory", Required = false, HelpText = "Exclude memory trace. [Only for State Test]")]
            public bool ExcludeMemory { get; set; }

            [Option('s', "stack", Required = false, HelpText = "Exclude stack trace. [Only for State Test]")]
            public bool ExcludeStack { get; set; }

            [Option('w', "wait", Required = false, HelpText = "Wait for input after the test run.")]
            public bool Wait { get; set; }

            [Option('x', "stdin", Required = false, HelpText = "If stdin is used, the state runner will read inputs (filenames) from stdin, and continue executing until empty line is read.")]
            public bool Stdin { get; set; }
        }

        public static async Task Main(params string[] args)
        {
            ParserResult<Options> result = Parser.Default.ParseArguments<Options>(args);
            if (result is Parsed<Options> options)
                await Run(options.Value);
        }

        private static async Task Run(Options options)
        {
            WhenTrace whenTrace = WhenTrace.WhenFailing;
            if (options.TraceNever)
                whenTrace = WhenTrace.Never;

            if (options.TraceAlways)
                whenTrace = WhenTrace.Always;

            string input = options.Input;
            if (options.Stdin)
                input = Console.ReadLine();

            while (!string.IsNullOrWhiteSpace(input))
            {
                if (options.BlockTest)
                    await RunBlockTest(input, source => new BlockchainTestsRunner(source, options.Filter));
                else
                    RunStateTest(input, source => new StateTestsRunner(source, whenTrace, !options.ExcludeMemory, !options.ExcludeStack, options.Filter));
                if (!options.Stdin)
                    break;

                input = Console.ReadLine();
            }

            if (options.Wait)
                Console.ReadLine();
        }

        private static async Task RunBlockTest(string path, Func<ITestSourceLoader, IBlockchainTestRunner> testRunnerBuilder)
        {
            ITestSourceLoader source = Path.HasExtension(path)
                ? new TestsSourceLoader(new LoadBlockchainTestFileStrategy(), path)
                : new TestsSourceLoader(new LoadBlockchainTestsStrategy(), path);
            await testRunnerBuilder(source).RunTestsAsync();
        }

        private static void RunStateTest(string path, Func<ITestSourceLoader, IStateTestRunner> testRunnerBuilder)
        {
            ITestSourceLoader source = Path.HasExtension(path)
                ? new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), path)
                : new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), path);
            testRunnerBuilder(source).RunTests();
        }
    }
}
