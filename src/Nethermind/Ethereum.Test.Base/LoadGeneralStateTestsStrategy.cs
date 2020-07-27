using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadGeneralStateTestsStrategy : ITestLoadStrategy
    {
        public IEnumerable<IEthereumTest> Load(string testsDirectoryName)
        {
            IEnumerable<string> testDirs;
            if (!Path.IsPathRooted(testsDirectoryName))
            {
                string testsDirectory  = GetGeneralStateTestsDirectory();


                testDirs = Directory.EnumerateDirectories(testsDirectory, testsDirectoryName, new EnumerationOptions { RecurseSubdirectories = true });
            }
            else
            {
                testDirs = new[] {testsDirectoryName};
            }

            List<GeneralStateTest> testJsons = new List<GeneralStateTest>();
            foreach (string testDir in testDirs)
            {
                testJsons.AddRange(LoadTestsFromDirectory(testDir));
            }

            return testJsons;
        }

        private string GetGeneralStateTestsDirectory()
        {
            char pathSeparator = Path.AltDirectorySeparatorChar;
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;
            
            return currentDirectory.Remove(currentDirectory.LastIndexOf("src")) + $"src{pathSeparator}tests{pathSeparator}GeneralStateTests";
        }

        private IEnumerable<GeneralStateTest> LoadTestsFromDirectory(string testDir)
        {
            List<GeneralStateTest> testsByName = new List<GeneralStateTest>();
            IEnumerable<string> testFiles = Directory.EnumerateFiles(testDir);

            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new FileTestsSource(testFile);
                try
                {
                    var tests = fileTestsSource.LoadGeneralStateTests();
                    foreach (GeneralStateTest blockchainTest in tests)
                    {
                        blockchainTest.Category = testDir;
                    }

                    testsByName.AddRange(tests);
                }
                catch (Exception e)
                {
                    testsByName.Add(new GeneralStateTest {Name = testFile, LoadFailure = $"Failed to load: {e.Message}"});
                }
            }

            return testsByName;
        }        
    }
}