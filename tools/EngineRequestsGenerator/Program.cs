using System.Text;
using FluentAssertions;
using Nethermind.Blockchain;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Test;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.TxPool;
using CommandLine;

namespace EngineRequestsGenerator;


public static class Program
{
    public class Options
    {
        [Option('c', "chainspecpath", Required = false, HelpText = "Path to chainspec used to generate tests")]
        public string ChainspecPath { get; set; }

        [Option('t', "testcase", Required = false, HelpText = "Name of the test case")]
        public string TestCaseName { get; set; }
    }

    static async Task Main(string[] args)
    {
        ParserResult<Options> result = Parser.Default.ParseArguments<Options>(args);
        if (result is Parsed<Options> options)
            await Run(options.Value);
    }

    private static async Task Run(Options options)
    {
        string chainSpecPath = options.ChainspecPath;
        var foundTestCase = Enum.TryParse(options.TestCaseName, out TestCase testCase);
        if (!foundTestCase)
            throw new ArgumentException($"Test case {options.TestCaseName} not found");

        var testCaseGenerator = new TestCaseGenerator(chainSpecPath, testCase);
        await testCaseGenerator.Generate();
    }
}
