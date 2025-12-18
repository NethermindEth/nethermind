// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadEofTestFileStrategy : ITestLoadStrategy
    {
        public IEnumerable<EthereumTest> Load(string testName, string? wildcard = null)
        {
            //in case user wants to give a test file other than the ones in ethereum tests submodule
            if (File.Exists(testName))
            {
                FileTestsSource fileTestsSource = new(testName, wildcard);
                IEnumerable<EthereumTest> tests = fileTestsSource.LoadTests(TestType.Eof);

                return tests;
            }

            string testsDirectory = GetEofTestsDirectory();

            IEnumerable<string> testFiles = Directory.EnumerateFiles(testsDirectory, testName, SearchOption.AllDirectories);

            List<EthereumTest> eofTests = new();

            //load all tests from found test files in ethereum tests submodule
            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new(testFile, wildcard);
                IEnumerable<EthereumTest> tests = fileTestsSource.LoadTests(TestType.Eof);

                eofTests.AddRange(tests);
            }

            return eofTests;
        }

        private string GetEofTestsDirectory()
        {
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;

            return Path.Combine(currentDirectory.Remove(currentDirectory.LastIndexOf("src")), "src", "tests", "EOFTests");
        }
    }
}
