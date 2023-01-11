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
        CommandLineApplication cli = new()
        {
            Name = "HiveCompare"
        };
        cli.HelpOption("-?|-h|--help");
        CommandOption firstFileOption = cli.Option("-f|--first-file", "first file to be used for comparison", CommandOptionType.SingleValue);
        CommandOption secondFileOption = cli.Option("-s|--second-file", "second file to be used for comparison", CommandOptionType.SingleValue);
        cli.OnExecute(() =>
        {
            bool HasRequiredOption(CommandOption option, [NotNullWhen(false)]out int? code)
            {
                if (!option.HasValue() || string.IsNullOrEmpty(option.Value()))
                {
                    cli!.ShowHelp();
                    code = 1;
                    return false;
                }
                code = null;
                return true;
            }
            bool RequiredFileExists(CommandOption option, [NotNullWhen(false)] out int? code)
            {
                if (!File.Exists(option.Value()))
                {
                    Console.WriteLine($"Could not find file '{option.Value()}'.");
                    code = 2;
                    return false;
                }
                code = null;
                return true;
            }
            if (!HasRequiredOption(firstFileOption, out int? code))
                return code.Value;
            if (!HasRequiredOption(secondFileOption, out code))
                return code.Value;
            if (!RequiredFileExists(firstFileOption, out code))
                return code.Value;
            if (!RequiredFileExists(secondFileOption, out code))
                return code.Value;
            if (!ParseTests(firstFileOption.Value(), secondFileOption.Value(), out code))
                return code.Value;
            return 0;
        });
        return cli;
    }

    private static bool ParseTests(string firstFile, string secondFile, [NotNullWhen(false)]out int? code)
    {
        code = null;
        Dictionary<string, TestCase> testCases1;
        Dictionary<string, TestCase> testCases2;
        try
        {
            using Stream file1 = File.OpenRead(firstFile);
            using Stream file2 = File.OpenRead(secondFile);

            HiveTestResult? hiveTest1 = JsonSerializer.Deserialize<HiveTestResult>(file1, SERIALIZER_OPTIONS);
            HiveTestResult? hiveTest2 = JsonSerializer.Deserialize<HiveTestResult>(file2, SERIALIZER_OPTIONS);

            if (hiveTest1 is null || hiveTest2 is null)
            {
                Console.WriteLine("Could not parse one of the files!");
                code = 3;
                return false;
            }
            testCases1 = hiveTest1.TestCases.Values.DistinctBy(v => v.Name + v.Description).ToDictionary(v => v.Name + v.Description);
            testCases2 = hiveTest2.TestCases.Values.DistinctBy(v => v.Name + v.Description).ToDictionary(v => v.Name + v.Description);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            code = 4;
            return false;
        }

        List<TestCase> newInTest1 = new();
        List<TestCase> newInTest2 = new();

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
            }
            else
            {
                newInTest1.Add(testCase1);
            }
        }
        newInTest2 = testCases2
            .Where(kv => !testCases1.ContainsKey(kv.Key))
            .Select(kv => kv.Value)
            .ToList();
        Console.WriteLine("============== Tests that are only in 1 ==============");
        foreach (TestCase testCase in newInTest1)
        {
            Console.WriteLine(testCase);
        }
        Console.WriteLine("============== Tests that are only in 2 ==============");
        foreach (TestCase testCase in newInTest2)
        {
            Console.WriteLine(testCase);
        }
        Console.ReadLine();
        return true;
    }
}
