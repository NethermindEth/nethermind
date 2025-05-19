// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadEipTestsStrategy : ITestLoadStrategy
    {
        public IEnumerable<EthereumTest> Load(string testsDirectoryName, string wildcard = null)
        {
            IEnumerable<string> testDirs;
            if (!Path.IsPathRooted(testsDirectoryName))
            {
                string testsDirectory = GetGeneralStateTestsDirectory();


                testDirs = Directory.EnumerateDirectories(testsDirectory, testsDirectoryName, new EnumerationOptions { RecurseSubdirectories = true });
            }
            else
            {
                testDirs = new[] { testsDirectoryName };
            }

            List<EthereumTest> testJsons = new();
            foreach (string testDir in testDirs)
            {
                testJsons.AddRange(LoadTestsFromDirectory(testDir, wildcard));
            }

            return testJsons;
        }

        private string GetGeneralStateTestsDirectory()
        {
            char pathSeparator = Path.AltDirectorySeparatorChar;
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;

            return Path.Combine(currentDirectory.Remove(currentDirectory.LastIndexOf("src")), "src", "tests", "EIPTests", "StateTests");
        }

        private IEnumerable<EthereumTest> LoadTestsFromDirectory(string testDir, string wildcard)
        {
            List<EthereumTest> testsByName = new();
            IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir);

            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new(testFile, wildcard);
                var tests = fileTestsSource.LoadTests(TestType.State);
                foreach (EthereumTest blockchainTest in tests)
                {
                    blockchainTest.Category = testDir;
                }

                testsByName.AddRange(tests);
            }

            return testsByName;
        }
    }
}
