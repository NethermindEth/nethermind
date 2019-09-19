/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.IO;
using CommandLine;
using Ethereum.Test.Base;

namespace Nethermind.State.Test.Runner
{
    internal class Program
    {
        private static long _totalMs;

        public class Options
        {
            [Option('i', "input", Required = true, HelpText = "Set the state test input file or directory.")]
            public string Input { get; set; }
            
            [Option('t', "trace", Required = false, HelpText = "Set to always trace (by default traces are only generated for failing tests).")]
            public bool TraceAlways { get; set; }
            
            [Option('w', "wait", Required = false, HelpText = "Wait for input after the test run.")]
            public bool Wait { get; set; }
        }

        public static void Main(params string[] args)
        {
            ParserResult<Options> result = Parser.Default.ParseArguments<Options>(args);
            Parsed<Options> options = result as Parsed<Options>;
            if (options != null)
            {
                Run(options.Value);
            }
        }

        private static void Run(Options options)
        {
            if (!string.IsNullOrWhiteSpace(options.Input))
            {
                RunSingleTest(options.Input, source => new StateTestsRunner(source, options.TraceAlways));
            }

            if (options.Wait)
            {
                Console.ReadLine();
            }
        }

        private static void RunSingleTest(string path, Func<IBlockchainTestsSource, IStateTestRunner> testRunnerBuilder)
        {
            IBlockchainTestsSource source;
            if (Directory.Exists(path))
            {
                source = new DirectoryTestsSource(path);
            }
            else if (File.Exists(path))
            {
                source = new FileTestsSource(path);
            }
            else
            {
                throw new IOException("Input path could not be resolved.");
            }

            testRunnerBuilder(source).RunTests();
        }
    }
}