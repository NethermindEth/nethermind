// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using CommandLine;
using Ethereum.Test.Base;
using Ethereum.Test.Base.Interfaces;

namespace Nethermind.State.Test.Runner
{
    internal class Program
    {
        public class Options
        {
            [Option('i', "input", Required = false, HelpText = "Set the state test input file or directory. Either 'input' or 'stdin' is required")]
            public string Input { get; set; }

            [Option('t', "trace", Required = false, HelpText = "Set to always trace (by default traces are only generated for failing tests).")]
            public bool TraceAlways { get; set; }

            [Option('n', "neverTrace", Required = false, HelpText = "Set to never trace (by default traces are only generated for failing tests).")]
            public bool TraceNever { get; set; }

            [Option('w', "wait", Required = false, HelpText = "Wait for input after the test run.")]
            public bool Wait { get; set; }

            [Option('m', "memory", Required = false, HelpText = "Exclude memory trace")]
            public bool ExcludeMemory { get; set; }

            [Option('s', "stack", Required = false, HelpText = "Exclude stack trace")]
            public bool ExcludeStack { get; set; }

            [Option('x', "stdin", Required = false, HelpText = "If stdin is used, the state runner will read inputs (filenames) from stdin, and continue executing until empty line is read.")]
            public bool Stdin { get; set; }
        }

        public static void Main(params string[] args)
        {
            ParserResult<Options> result = Parser.Default.ParseArguments<Options>(args);
            if (result is Parsed<Options> options)
            {
                Run(options.Value);
            }
        }

        private static void Run(Options options)
        {
            WhenTrace whenTrace = WhenTrace.WhenFailing;
            if (options.TraceNever)
            {
                whenTrace = WhenTrace.Never;
            }

            if (options.TraceAlways)
            {
                whenTrace = WhenTrace.Always;
            }

            string input = options.Input;
            if (options.Stdin)
            {
                input = Console.ReadLine();
            }

            while (!string.IsNullOrWhiteSpace(input))
            {
                RunSingleTest(input, source => new StateTestsRunner(source, whenTrace, !options.ExcludeMemory, !options.ExcludeStack));
                if (!options.Stdin)
                {
                    break;
                }

                input = Console.ReadLine();
            }

            if (options.Wait)
            {
                Console.ReadLine();
            }
        }

        private static void RunSingleTest(string path, Func<ITestSourceLoader, IStateTestRunner> testRunnerBuilder)
        {
            ITestSourceLoader source;

            if (Path.HasExtension(path))
            {
                source = new TestsSourceLoader(new LoadGeneralStateTestFileStrategy(), path);
            }
            else
            {
                source = new TestsSourceLoader(new LoadGeneralStateTestsStrategy(), path);
            }

            testRunnerBuilder(source).RunTests();
        }
    }
}
