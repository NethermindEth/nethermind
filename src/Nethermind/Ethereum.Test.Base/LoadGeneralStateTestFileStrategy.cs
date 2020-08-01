using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;

namespace Ethereum.Test.Base
{
    public class LoadGeneralStateTestFileStrategy : ITestLoadStrategy
    {
        public IEnumerable<IEthereumTest> Load(string testName)
        {
            //in case user wants to give test file other than the ones in ethereum tests submodule 
            if(File.Exists(testName))
            {
                var fileTestsSource = new FileTestsSource(testName);
                var tests = fileTestsSource.LoadGeneralStateTests();

                return tests;
            }

            string testsDirectory = GetGeneralStateTestsDirectory();

            IEnumerable<string> testFiles = Directory.EnumerateFiles(testsDirectory, testName, SearchOption.AllDirectories);

            List<GeneralStateTest> generalStateTests = new List<GeneralStateTest>();

            //load all tests from found test files in ethereum tests submodule 
            foreach (string testFile in testFiles)
            {
                FileTestsSource fileTestsSource = new FileTestsSource(testFile);
                try
                {
                    var tests = fileTestsSource.LoadGeneralStateTests();

                    generalStateTests.AddRange(tests);
                }
                catch (Exception e)
                {
                    generalStateTests.Add(new GeneralStateTest {Name = testFile, LoadFailure = $"Failed to load: {e.Message}"});
                }
            }

            return generalStateTests;
        }

        private string GetGeneralStateTestsDirectory()
        {
            char pathSeparator = Path.AltDirectorySeparatorChar;
            string currentDirectory = AppDomain.CurrentDomain.BaseDirectory;

            return currentDirectory.Remove(currentDirectory.LastIndexOf("src")) + $"src{pathSeparator}tests{pathSeparator}GeneralStateTests";
        }
    }
}