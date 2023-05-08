// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base;

// Loads tests from src/ethereum-tests
public class LoadLocalTestsStrategy : ITestLoadStrategy
{
    public IEnumerable<IEthereumTest> Load(string testDirectoryName, string wildcard = null)
    {
        List<BlockchainTest> testsByName = new();
        IEnumerable<string> testFiles = Directory.EnumerateFiles(GetLocalTestsDirectory() + testDirectoryName);

        foreach (string testFile in testFiles)
        {
            FileTestsSource fileTestsSource = new(testFile, wildcard);
            try
            {
                var tests = fileTestsSource.LoadBlockchainTests();
                foreach (BlockchainTest blockchainTest in tests)
                {
                    blockchainTest.Category = testDirectoryName;
                }

                testsByName.AddRange(tests);
            }
            catch (Exception e)
            {
                testsByName.Add(new BlockchainTest { Name = testFile, LoadFailure = $"Failed to load: {e}" });
            }
        }

        return testsByName;
    }

    private string GetLocalTestsDirectory()
    {
        char pathSeparator = Path.AltDirectorySeparatorChar;
        string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;

        return currentDirectory.Remove(currentDirectory.LastIndexOf("src")) + $"src{pathSeparator}ethereum-tests{pathSeparator}";
    }

}
