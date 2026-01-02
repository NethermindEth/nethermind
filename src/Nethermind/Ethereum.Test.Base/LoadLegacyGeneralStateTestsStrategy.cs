// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadLegacyGeneralStateTestsStrategy : ITestLoadStrategy
    {
        public IEnumerable<EthereumTest> Load(string testsDirectoryName, string wildcard = null)
        {
            IEnumerable<string> testDirs;
            if (!Path.IsPathRooted(testsDirectoryName))
            {
                string legacyTestsDirectory = GetLegacyGeneralStateTestsDirectory();

                testDirs = Directory.EnumerateDirectories(legacyTestsDirectory, testsDirectoryName, new EnumerationOptions { RecurseSubdirectories = true });
            }
            else
            {
                testDirs = new[] { testsDirectoryName };
            }

            List<EthereumTest> tests = new();
            foreach (string testDir in testDirs)
            {
                tests.AddRange(LoadTestsFromDirectory(testDir, wildcard));
            }

            return tests;
        }

        private static string GetLegacyGeneralStateTestsDirectory()
        {
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            string rootDirectory = currentDirectory.Remove(currentDirectory.LastIndexOf("src"));

            return Path.Combine(rootDirectory, "src", "tests", "LegacyTests", "Cancun", "GeneralStateTests");
        }

        private IEnumerable<EthereumTest> LoadTestsFromDirectory(string testDir, string wildcard)
        {
            List<EthereumTest> testsByName = new();
            IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir);

            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new(testFile, wildcard);
                var tests = fileTestsSource.LoadTests(TestType.State);
                foreach (EthereumTest ethereumTest in tests)
                {
                    ethereumTest.Category = testDir;
                    // Mark legacy tests to use old coinbase behavior for backward compatibility
                    if (ethereumTest is GeneralStateTest generalStateTest)
                    {
                        generalStateTest.IsLegacy = true;
                    }
                }

                testsByName.AddRange(tests);
            }

            return testsByName;
        }
    }
}
