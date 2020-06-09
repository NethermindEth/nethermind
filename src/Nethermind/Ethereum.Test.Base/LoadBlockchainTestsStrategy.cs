using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadBlockchainTestsStrategy : ITestLoadStrategy
    {
        public IEnumerable<IEthereumTest> Load(string testsDirectoryName)
        {
            IEnumerable<string> testDirs;
            if (!Path.IsPathRooted(testsDirectoryName))
            {
                string testDirectory = GetBlockchainTestsDirectory();

               testDirs = Directory.EnumerateDirectories(testDirectory, testsDirectoryName, new EnumerationOptions { RecurseSubdirectories = true });
            }
            else
            {
                testDirs = new[] {testsDirectoryName};
            }

            List<BlockchainTest> testJsons = new List<BlockchainTest>();
            foreach (string testDir in testDirs)
            {
                testJsons.AddRange(LoadTestsFromDirectory(testDir));
            }

            return testJsons;
        }

        private string GetBlockchainTestsDirectory()
        {
            char pathSeparator = Path.AltDirectorySeparatorChar;
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            
            return currentDirectory.Remove(currentDirectory.LastIndexOf("src")) + $"src{pathSeparator}tests{pathSeparator}BlockchainTests";
        }

        private IEnumerable<BlockchainTest> LoadTestsFromDirectory(string testDir)
        {
            List<BlockchainTest> testsByName = new List<BlockchainTest>();
            IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir);

            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new FileTestsSource(testFile);
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
                    testsByName.Add(new BlockchainTest {Name = testFile, LoadFailure = $"Failed to load: {e.Message}"});
                }
            }

            return testsByName;
        }
    }
}