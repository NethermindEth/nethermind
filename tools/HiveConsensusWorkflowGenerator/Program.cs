using System.Diagnostics;
using System.Text.Json;

namespace HiveConsensusWorkflowGenerator;

public static class Program
{
    const long MaxSizeWithoutSplitting = 35_000_000;
    const int MaxJobsCount = 256;

    static void Main(string[] args)
    {
        string path = args.FirstOrDefault() is not null ? args.First() : "src/tests";

        IEnumerable<string> directories = GetTestsDirectories(path);
        Dictionary<string, long> pathsToBeTested = GetPathsToBeTested(directories);

        // Sort the tests by size in descending order
        var sortedTests = pathsToBeTested.OrderByDescending(kv => kv.Value).ToList();

        // Create empty list of 256 jobs
        var groupedTestNames = new List<List<(string fullPath, string fileName)>>(); // Store both full path and filename
        for (int i = 0; i < MaxJobsCount; i++)
        {
            groupedTestNames.Add(new List<(string, string)>());
        }

        // Initial distribution without size limit
        int groupIndex = 0;
        foreach (var test in sortedTests.Take(MaxJobsCount))
        {
            string fileName = Path.GetFileNameWithoutExtension(test.Key); // Strip the .json extension
            groupedTestNames[groupIndex].Add((test.Key, fileName));
            groupIndex = (groupIndex + 1) % MaxJobsCount;
        }

        // Subsequent distribution with size limit
        foreach (var test in sortedTests.Skip(MaxJobsCount))
        {
            string fileName = Path.GetFileNameWithoutExtension(test.Key); // Strip the .json extension
            groupIndex = FindSuitableGroupIndex(groupedTestNames, pathsToBeTested, MaxSizeWithoutSplitting, test.Value);
            if (groupIndex != -1)
            {
                groupedTestNames[groupIndex].Add((test.Key, fileName));
            }
        }

        // Calculate group sizes and include sizes of each test in JSON output
        var jsonGroups = groupedTestNames.Select(group => new
        {
            testNames = group.Select(t => new { name = t.fileName, size = pathsToBeTested.ContainsKey(t.fullPath) ? pathsToBeTested[t.fullPath] : 0 }).ToArray(),
            totalSize = group.Sum(t => pathsToBeTested.ContainsKey(t.fullPath) ? pathsToBeTested[t.fullPath] : 0)
        })
        .OrderBy(group => group.totalSize)
        .ToList();

        string jsonString = JsonSerializer.Serialize(jsonGroups, new JsonSerializerOptions { WriteIndented = true });

        Console.WriteLine(jsonString);

        // Log the number of groups created in the JSON file
        Console.WriteLine($"Number of groups created in JSON file: {jsonGroups.Count}");

        File.WriteAllText("matrix.json", jsonString);
    }

    private static int FindSuitableGroupIndex(List<List<(string fullPath, string fileName)>> groups, Dictionary<string, long> pathsToBeTested, long maxSize, long testSize)
    {
        int suitableIndex = -1;
        long smallestSize = long.MaxValue;

        for (int i = 0; i < groups.Count; i++)
        {
            long currentGroupSize = groups[i].Sum(t => pathsToBeTested.ContainsKey(t.fullPath) ? pathsToBeTested[t.fullPath] : 0);

            if (currentGroupSize + testSize <= maxSize && currentGroupSize < smallestSize)
            {
                smallestSize = currentGroupSize;
                suitableIndex = i;
            }
        }

        return suitableIndex;
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
