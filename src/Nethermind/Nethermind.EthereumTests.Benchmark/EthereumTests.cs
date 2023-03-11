// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using Ethereum.Test.Base;
using Nethermind.Evm;

namespace Nethermind.EthereumTests.Benchmark
{
    [ShortRunJob()]
    public class EthereumTests : GeneralStateTestBase
    {
        public static IEnumerable<string> TestFileSource() => Directory.EnumerateFiles(@"EthereumTestFiles", "*.json", SearchOption.AllDirectories);

        [Benchmark]
        [ArgumentsSource(nameof(TestFileSource))]
        public void Run(string testFile)
        {
            FileTestsSource source = new(testFile);
            var tests = source.LoadGeneralStateTests();

            foreach (GeneralStateTest test in tests)
            {
                RunTest(test);
            }
        }
    }
}
