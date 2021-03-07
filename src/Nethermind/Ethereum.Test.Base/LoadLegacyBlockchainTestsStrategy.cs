using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadLegacyBlockchainTestsStrategy : ITestLoadStrategy
    {
        public IEnumerable<IEthereumTest> Load(string testsDirectoryName, string wildcard = null)
        {
            IEnumerable<string> testDirs;
            if (!Path.IsPathRooted(testsDirectoryName))
            {
                string legacyTestsDirectory = GetLegacyBlockchainTestsDirectory();

               testDirs = Directory.EnumerateDirectories(legacyTestsDirectory, testsDirectoryName, new EnumerationOptions { RecurseSubdirectories = true });
            }
            else
            {
                testDirs = new[] {testsDirectoryName};
            }

            List<BlockchainTest> testJsons = new();
            foreach (string testDir in testDirs)
            {
                testJsons.AddRange(LoadTestsFromDirectory(testDir, wildcard));
            }

            return testJsons;
        }

        private string GetLegacyBlockchainTestsDirectory()
        {
            char pathSeparator = Path.AltDirectorySeparatorChar;
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            
            return currentDirectory.Remove(currentDirectory.LastIndexOf("src")) + $"src{pathSeparator}tests{pathSeparator}LegacyTests{pathSeparator}Constantinople{pathSeparator}BlockchainTests";
        }

        private IEnumerable<BlockchainTest> LoadTestsFromDirectory(string testDir, string wildcard)
        {
            List<BlockchainTest> testsByName = new();
            IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir);

            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new(testFile, wildcard);
                try
                {
                    var tests = fileTestsSource.LoadBlockchainTests();
                    foreach (BlockchainTest blockchainTest in tests)
                    {
                        blockchainTest.Category = testDir;
                    }

                    testsByName.AddRange(tests);
                }
                catch (Exception e)
                {
                    testsByName.Add(new BlockchainTest {Name = testFile, LoadFailure = $"Failed to load: {e}"});
                }
            }

            return testsByName;
        }  
    }
}
