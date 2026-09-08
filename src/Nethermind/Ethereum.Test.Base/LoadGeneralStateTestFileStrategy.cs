// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;

namespace Ethereum.Test.Base
{
    public class LoadGeneralStateTestFileStrategy : ITestLoadStrategy
    {
        public IEnumerable<EthereumTest> Load(string testName, string? wildcard = null)
        {
            //in case user wants to give test file other than the ones in ethereum tests submodule
            if (File.Exists(testName))
            {
                FileTestsSource fileTestsSource = new(testName, wildcard);
                IEnumerable<EthereumTest> tests = fileTestsSource.LoadTests(TestType.State);

                return WithTypedLoadFailures(tests);
            }

            string testsDirectory = GetGeneralStateTestsDirectory();

            IEnumerable<string> testFiles = Directory.EnumerateFiles(testsDirectory, testName, SearchOption.AllDirectories);

            List<EthereumTest> generalStateTests = [];

            //load all tests from found test files in ethereum tests submodule
            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new(testFile, wildcard);

                IEnumerable<EthereumTest> tests = fileTestsSource.LoadTests(TestType.State);
                generalStateTests.AddRange(WithTypedLoadFailures(tests));
            }

            return generalStateTests;
        }

        /// <summary>Restates a file-level load failure as a <see cref="GeneralStateTest"/>.</summary>
        /// <remarks>
        /// <see cref="FileTestsSource"/> reports an unreadable file as a <see cref="FailedToLoadTest"/>,
        /// which does not derive from <see cref="GeneralStateTest"/>, so the <c>OfType</c> filter in
        /// <see cref="TestsSourceLoader.LoadTests{TTestType}"/> discards it and the caller sees a file
        /// that failed to parse as no tests at all rather than as a failure.
        /// </remarks>
        private static List<EthereumTest> WithTypedLoadFailures(IEnumerable<EthereumTest> tests)
        {
            List<EthereumTest> typed = [];

            foreach (EthereumTest test in tests)
            {
                typed.Add(test is FailedToLoadTest
                    ? new GeneralStateTest { Name = test.Name, LoadFailure = test.LoadFailure }
                    : test);
            }

            return typed;
        }

        private string GetGeneralStateTestsDirectory()
        {
            char pathSeparator = Path.AltDirectorySeparatorChar;
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;

            return Path.Combine(currentDirectory.Remove(currentDirectory.LastIndexOf("src", StringComparison.Ordinal)), "src", "tests", "GeneralStateTests");
        }
    }
}
