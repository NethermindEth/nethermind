using HiveCompare.Models;
using Microsoft.Extensions.CommandLineUtils;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

internal class Program
{
    public static JsonSerializerOptions SERIALIZER_OPTIONS = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static void Main(string[] args)
    {
        var cli = CreateCommandLineInterface();
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
        var cli = new CommandLineApplication
        {
            Name = "HiveCompare"
        };
        cli.HelpOption("-?|-h|--help");
        var firstFileOption = cli.Option("-f|--first-file", "first file to be used for comparison", CommandOptionType.SingleValue);
        var secondFileOption = cli.Option("-s|--second-file", "second file to be used for comparison", CommandOptionType.SingleValue);
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
                    code = 0;
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
            var builder = new ContainerBuilder();
            builder.RegisterType<ConsoleLogger>().As<ILogger>();
            builder.RegisterType<BookEngine>().As<IBookEngine>();
            var container = builder.Build();
            var bookEngine = container.Resolve<IBookEngine>();
            bookEngine.Config = new BookEngineConfig()
            {
                FixArabicYeKe = fixArabicOption.HasValue(),
                FixVirtualSpaceAndPrefixSuffixes = true,
                PersianShape = !noOptOption.HasValue()
            };
            try
            {
                var book = bookEngine.ProcessBook(inputFile.Value());
                var outputPath = outputFile.HasValue() ? outputFile.Value() : null;
                var savedPath = book.SaveAs();
                if (outputPath != null)
                {
                    File.Copy(savedPath, outputPath, true);
                    File.Delete(savedPath);
                    savedPath = Path.GetFullPath(outputPath);
                }
                Console.WriteLine($"Congratulations, operation was successful, File saved at '{savedPath}'.");
            }
            catch (Base.Exceptions.EBookFormatNotSupported ex)
            {
                Console.WriteLine(ex.Message);
                return 0;
            }
            catch (FileNotFoundException)
            {
                Console.WriteLine($"Could not find file '{inputFile.Value()}'.");
                return 0;
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine($"File '{inputFile.Value()}' access denied.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error, unfortunatelly something went wrong. You can check log file next to your epub file");
                var logPath = FileHelper.LogFileName(inputFile.Value(), "errors.txt");
                if (File.Exists(logPath))
                    File.Delete(logPath);
                do
                {
                    File.AppendAllText(logPath, ex.Message + "\r\n" + ex.StackTrace + "\r\n--------------------------\r\n");
                    ex = ex.InnerException;
                } while (ex != null);
                return 0;
            }
            return 1;
            
        });
        return cli;

        
    }

    private static void ParseTests(string firstFile, string secondFile)
    {
        Dictionary<string, TestCase> testCases1;
        Dictionary<string, TestCase> testCases2;
        try
        {
            var file1 = File.ReadAllText(firstFile);
            var file2 = File.ReadAllText(secondFile);

            var hiveTest1 = JsonSerializer.Deserialize<HiveTestResult>(file1, SERIALIZER_OPTIONS);
            var hiveTest2 = JsonSerializer.Deserialize<HiveTestResult>(file2, SERIALIZER_OPTIONS);

            if (hiveTest1 is null || hiveTest2 is null)
            {
                Console.WriteLine("Could not parse one of the files!");
                return;
            }
            testCases1 = hiveTest1.TestCases.Values.DistinctBy(v => v.Name + v.Description).ToDictionary(v => v.Name + v.Description);
            testCases2 = hiveTest2.TestCases.Values.DistinctBy(v => v.Name + v.Description).ToDictionary(v => v.Name + v.Description);
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
            return;
        }

        var newInTest1 = new List<TestCase>();
        var newInTest2 = new List<TestCase>();

        foreach (var key in testCases1.Keys)
        {
            var testCase1 = testCases1[key];
            if (testCases2.TryGetValue(key, out var testCase2))
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
        foreach (var testCase in newInTest1)
        {
            Console.WriteLine(testCase);
        }
        Console.WriteLine("============== Tests that are only in 2 ==============");
        foreach (var testCase in newInTest2)
        {
            Console.WriteLine(testCase);
        }
        Console.ReadLine();
    }
}