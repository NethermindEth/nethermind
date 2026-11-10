// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using Ethereum.Test.Base;

namespace Nethermind.EthereumTests.Benchmark
{
    [ShortRunJob()]
    public class EthereumTests : GeneralStateTestBase
    {
        public static IEnumerable<string> TestFileSource() => Directory.EnumerateFiles(@"EthereumTestFiles", "*.json", SearchOption.AllDirectories);

        [Benchmark]
        [ArgumentsSource(nameof(TestFileSource))]
        public async Task Run(string testFile)
        {
            FileTestsSource source = new(testFile);
            IEnumerable<EthereumTest> tests = source.LoadTests(TestType.State);

            foreach (GeneralStateTest test in tests)
            {
                await RunTest(test);
            }
        }
    }
}
