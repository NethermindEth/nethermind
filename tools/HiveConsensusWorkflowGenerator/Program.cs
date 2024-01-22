using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace HiveConsensusWorkflowGenerator;

public static class Program
{
    const int MaxJobsCount = 256;

    static void Main(string[] args)
    {
        string path = args.FirstOrDefault() is not null ? args.First() : "src/tests";

        IEnumerable<string> directories = GetTestsDirectories(path);
        Dictionary<string, long> pathsToBeTested = GetPathsToBeTested(directories);

        // Sort the tests by size in descending order
        var sortedTests = pathsToBeTested.OrderByDescending(kv => kv.Value).ToList();

        var groupedTestNames = new SortedList<long, List<string>>();

        foreach (var test in sortedTests)
        {
            long size = 0;
            List<string> testsList = null;

            if (groupedTestNames.Count == MaxJobsCount)
            {
                testsList = new List<string>(groupedTestNames.First().Value);
                size = groupedTestNames.First().Key;
                testsList.Add(test.Key);
                size += test.Value;
                groupedTestNames.Remove(groupedTestNames.First().Key);
            }
            else
            {
                size = test.Value;
                testsList = new List<string> { test.Key };
            }

            //Hack to use SortedList
            while (groupedTestNames.ContainsKey(size))
            {
                size++;
            }

            groupedTestNames.Add(size, testsList);
        }

        // Calculate group sizes and include sizes of each test in JSON output
        var jsonGroups = groupedTestNames.Select(group => new
        {
            testNames = group.Value.ToArray()
        })
        .ToList();

        string jsonString = JsonSerializer.Serialize(jsonGroups, new JsonSerializerOptions { WriteIndented = true });
        
        File.WriteAllText("matrix.json", jsonString);
    }

    private static IEnumerable<string> GetTestsDirectories(string path)
    {
        string testsDirectory = Path.Combine(FindDirectory("nethermind"), path, "BlockchainTests");
        yield return testsDirectory;
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
            // Recursively find all JSON files
            foreach (string file in Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories))
            {
                long fileSize = (new FileInfo(file)).Length;

                string[] pathSplitted = file.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                string key = $"{pathSplitted[^3]}/{pathSplitted[^2]}/{Path.GetFileName(file)}";


                pathsToBeTested.Add(key.Split('.')[0], fileSize);
            }
        }

        return pathsToBeTested;
    }
}
