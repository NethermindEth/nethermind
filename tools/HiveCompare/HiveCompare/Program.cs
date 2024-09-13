using HiveCompare.Models;
using Microsoft.Extensions.CommandLineUtils;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

internal class Program
{
    public static readonly JsonSerializerOptions SERIALIZER_OPTIONS = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static void Main(string[] args)
    {
        CommandLineApplication cli = CreateCommandLineInterface();
        try
        {
            cli.Execute(args);
        }
        catch (CommandParsingException)
        {
            cli.ShowHelp();
        }
    }

    static CommandLineApplication CreateCommandLineInterface()
    {
        CommandLineApplication cli = new() { Name = "HiveCompare" };
        cli.HelpOption("-?|-h|--help");
        CommandOption firstFileOption = cli.Option("-f|--first-file", "first file to be used for comparison", CommandOptionType.SingleValue);
        CommandOption secondFileOption = cli.Option("-s|--second-file", "second file to be used for comparison", CommandOptionType.SingleValue);

        cli.OnExecute(() =>
        {
            bool HasRequiredOption(CommandOption option)
            {
                if (option.HasValue() && !string.IsNullOrEmpty(option.Value())) return true;

                cli.ShowHelp();
                return false;
            }

            bool RequiredFileExists(CommandOption option)
            {
                if (File.Exists(option.Value())) return true;

                Console.WriteLine($"Could not find file '{option.Value()}'.");
                return false;

            }

            return HasRequiredOption(firstFileOption) && HasRequiredOption(secondFileOption)
                ? RequiredFileExists(firstFileOption) && RequiredFileExists(secondFileOption)
                    ? ParseTests(firstFileOption.Value(), secondFileOption.Value())
                        ? 0
                        : 4
                    : 2
                : 1;
        });

        return cli;
    }

    private static bool ParseTests(string firstFile, string secondFile)
    {
        static bool TryLoadTestCases(string file, [NotNullWhen(true)] out Dictionary<string, TestCase>? testCases)
        {
            bool Fail(out Dictionary<string, TestCase>? testCases)
            {
                Console.WriteLine("Could not parse one of the files!");
                testCases = null;
                return false;
            }

            try
            {
                using Stream fileStream = File.OpenRead(file);
                HiveTestResult? hiveTest = JsonSerializer.Deserialize<HiveTestResult>(fileStream, SERIALIZER_OPTIONS);

                if (hiveTest is null)
                {
                    return Fail(out testCases);
                }

                testCases = hiveTest.TestCases.Values.DistinctBy(v => v.Key).ToDictionary(v => v.Key);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
                return Fail(out testCases);
            }
        }

        return TryLoadTestCases(firstFile, out Dictionary<string, TestCase>? testCases1)
               && TryLoadTestCases(secondFile, out Dictionary<string, TestCase>? testCases2)
               && PrintOutDifferences(testCases1, testCases2);
    }

    private static bool PrintOutDifferences(Dictionary<string, TestCase> testCases1, Dictionary<string, TestCase> testCases2)
    {
        void PrintOutUniqueCases(ICollection<TestCase> testCases, int caseSet)
        {
            if (testCases.Count > 0)
            {
                Console.WriteLine($"============== Tests that are only in {caseSet} ==============");
                foreach (TestCase testCase in testCases)
                {
                    Console.WriteLine(testCase);
                }
            }
        }

        List<TestCase> newInTest1 = new();

        foreach (string key in testCases1.Keys)
        {
            TestCase testCase1 = testCases1[key];
            if (testCases2.TryGetValue(key, out TestCase? testCase2))
            {
                if (testCase2.SummaryResult.Pass != testCase1.SummaryResult.Pass)
                {
                    Console.WriteLine("============== testCase result change found! ==============");
                    Console.WriteLine("From 1:");
                    Console.WriteLine(testCase1);
                    Console.WriteLine("From 2:");
                    Console.WriteLine(testCase2);
                }

                testCases2.Remove(key);
            }
            else
            {
                newInTest1.Add(testCase1);
            }
        }

        PrintOutUniqueCases(newInTest1, 1);
        PrintOutUniqueCases(testCases2.Values, 2);

        Console.ReadLine();
        return true;
    }
}
