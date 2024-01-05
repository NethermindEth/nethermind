using System.Diagnostics;
using System.Text.Json;

namespace HiveConsensusWorkflowGenerator;

public static class Program
{
    const long MaxSizeWithoutSplitting = 35_000_000;
    const long TargetSize = 23_000_000;
    const long PenaltyForAdditionalInit = 300_000;
    const int MaxJobsCount = 256;

    static void Main(string[] args)
    {
        string path = args.FirstOrDefault() is not null ? args.First() : "src/tests";

        IEnumerable<string> directories = GetTestsDirectories(path);
        Dictionary<string, long> pathsToBeTested = GetPathsToBeTested(directories);

        var testNames = pathsToBeTested.Select(y => Path.GetFileName(y.Key))
                                       .Select(x => x.Split('.').First())
                                       .ToList();

        var groupedTestNames = new List<List<string>>();
        for (int i = 0; i < MaxJobsCount; i++)
        {
            groupedTestNames.Add(new List<string>());
        }

        int groupIndex = 0;
        foreach (var testName in testNames)
        {
            groupedTestNames[groupIndex].Add(testName);
            groupIndex = (groupIndex + 1) % MaxJobsCount;
        }

        var jsonGroups = groupedTestNames.Select(group => new { testNames = group.ToArray() })
                                         .Where(group => group.testNames.Any())
                                         .ToList();

        string jsonString = JsonSerializer.Serialize(jsonGroups, new JsonSerializerOptions { WriteIndented = true });

        Console.WriteLine(jsonString);

        File.WriteAllText("matrix.json", jsonString);
    }


    private static IEnumerable<string> GetTestsDirectories(string path)
    {
        string testsDirectory = string.Concat(FindDirectory("nethermind"), "/", path, "/BlockchainTests");

        foreach (string directory in Directory.GetDirectories(testsDirectory, "st*", SearchOption.AllDirectories))
        {
            yield return directory;
        }

        foreach (string directory in Directory.GetDirectories(testsDirectory, "bc*", SearchOption.AllDirectories))
        {
            yield return directory;
        }
    }

    private static string FindDirectory(string searchPattern)
    {
        string? currentDir = Environment.CurrentDirectory;
        do
        {
            if (currentDir == null)
            {
                return "";
            }

            string? dir = Directory
                .EnumerateDirectories(currentDir, searchPattern, SearchOption.TopDirectoryOnly)
                .SingleOrDefault();

            if (dir != null)
            {
                return dir;
            }

            currentDir = Directory.GetParent(currentDir)?.FullName;
        } while (true);
    }

    private static Dictionary<string, long> GetPathsToBeTested(IEnumerable<string> directories)
    {
        Dictionary<string, long> pathsToBeTested = new();

        foreach (string directory in directories)
        {
            long sum = 0;

            string parentDirectory = Directory.GetParent(directory)?.ToString() ?? "";
            string prefix = Path.GetFileName(parentDirectory)[..2];
            if (!prefix.Equals("st") && !prefix.Equals("bc"))
            {
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    long fileSize = (new FileInfo(file)).Length;
                    sum += fileSize;
                }

                if (sum < MaxSizeWithoutSplitting)
                {
                    pathsToBeTested.Add(directory, sum);
                }
                else
                {
                    foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        string fileName = Path.GetFileName(file);
                        long fileSize = (new FileInfo(file)).Length;
                        if (pathsToBeTested.TryGetValue(fileName, out long size))
                        {
                            size += fileSize;
                        }
                        else
                        {
                            pathsToBeTested.Add(fileName, fileSize);
                        }
                    }
                }
            }
        }

        return pathsToBeTested;
    }
}
