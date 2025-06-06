using CommandLine;

namespace EngineRequestsGenerator;


public static class Program
{
    public class Options
    {
        [Option('c', "chainspecpath", Required = false, HelpText = "Path to chainspec used to generate tests")]
        public string? ChainspecPath { get; set; }

        [Option('t', "testcase", Required = false, HelpText = "Title of the test case")]
        public string? TestCaseName { get; set; }

        [Option('o', "outputPath", Required = false, HelpText = "Output folder path")]
        public string? OutputPath { get; set; }

        [Option('m', "generateMetadataOnly", Required = false, HelpText = "Generating only metadata for test cases")]
        public bool GenerateMetadata { get; set; }
    }

    static async Task Main(string[] args)
    {
        ParserResult<Options> result = Parser.Default.ParseArguments<Options>(args);
        if (result is Parsed<Options> options)
            await Run(options.Value);
    }

    private static async Task Run(Options options)
    {
        if (!options.GenerateMetadata)
        {
            var foundTestCase = Enum.TryParse(options.TestCaseName, out TestCase testCase);
            if (!foundTestCase)
                throw new ArgumentException($"Test case {options.TestCaseName} not found");

            var testCaseGenerator = new TestCaseGenerator(options.ChainspecPath!, testCase, options.OutputPath!);
            await testCaseGenerator.Generate();
        }

        if (options.GenerateMetadata)
        {
            MetadataGenerator metadataGenerator = new(options.OutputPath!);
            await metadataGenerator.GenerateAll();
        }
    }
}
