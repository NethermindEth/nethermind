using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Ethereum.Blockchain.Test;
using Nevermind.Core;
using Nevermind.Evm;

namespace Nevermind.Blockchain.Test.Runner
{
    internal class FileLogger : ILogger
    {
        private readonly string _filePath;

        public FileLogger(string filePath)
        {
            _filePath = filePath;
        }

        private readonly StringBuilder _buffer = new StringBuilder();

        public void Log(string text)
        {
            try
            {
                _buffer.AppendLine(text);
                if (_buffer.Length > 1024 * 1024)
                {
                    Flush();
                }
            }
            catch (Exception e)
            {
                Console.WriteLine(e);
                throw;
            }
        }

        public void Flush()
        {
            File.AppendAllText(_filePath, _buffer.ToString());
            _buffer.Clear();
        }
    }

    public class BugHunter : BlockchainTestBase, ITestInRunner
    {
        public CategoryResult RunTests(string subset, int iterations = 1)
        {
            ConsoleColor defaultColor = Console.ForegroundColor;
            List<string> failingTests = new List<string>();

            string directoryName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FailingTests");
            IEnumerable<BlockchainTest> tests = LoadTests(subset);
            foreach (BlockchainTest test in tests)
            {
                Setup(null);
                try
                {
                    Console.Write($"{test.Name,-80} ");
                    RunTest(test);
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("PASS");
                    Console.ForegroundColor = defaultColor;
                }
                catch (Exception)
                {
                    failingTests.Add(test.Name);
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("FAIL");
                    Console.ForegroundColor = defaultColor;
                    FileLogger logger = new FileLogger(Path.Combine(directoryName, string.Concat(subset, "_", test.Name, ".txt")));
                    try
                    {
                        if (!Directory.Exists(directoryName))
                        {
                            Directory.CreateDirectory(directoryName);
                        }

                        Setup(logger);
                        RunTest(test);
                    }
                    catch (Exception ex)
                    {
                        logger.Log(ex.ToString());
                        logger.Flush();
                    }

                    // should not happend
                    logger.Flush();
                }
            }

            return new CategoryResult(0, failingTests.ToArray());
        }
    }
}