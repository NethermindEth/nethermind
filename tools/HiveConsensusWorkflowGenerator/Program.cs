using System.Text.Json;

namespace HiveConsensusWorkflowGenerator;

public static class Program
{
    const int DefaultMaxJobsCount = 256;

    static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : "src/tests";
        int maxJobsCount = DefaultMaxJobsCount;

        if (args.Length > 1)
        {
            if (!int.TryParse(args[1], out maxJobsCount) || maxJobsCount <= 0)
            {
                Console.Error.WriteLine($"Invalid shard count '{args[1]}'. Expected a positive integer.");
                return 1;
            }
        }

        IEnumerable<string> directories = GetTestsDirectories(path);
        Dictionary<string, long> pathsToBeTested = GetPathsToBeTested(directories);

        // Sort the tests by size in descending order
        List<KeyValuePair<string, long>> sortedTests = pathsToBeTested.OrderByDescending(kv => kv.Value).ToList();

        SortedList<long, List<string>> groupedTestNames = [];

        foreach (KeyValuePair<string, long> test in sortedTests)
        {
            long size = 0;
            List<string>? testsList = null;

            if (groupedTestNames.Count == maxJobsCount)
            {
                KeyValuePair<long, List<string>> smallestGroup = groupedTestNames.First();
                testsList = [.. smallestGroup.Value];
                size = smallestGroup.Key;
                testsList.Add(test.Key);
                size += test.Value;
                groupedTestNames.Remove(smallestGroup.Key);
            }
            else
            {
                size = test.Value;
                testsList = [test.Key];
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
        return 0;
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
            if (currentDir is null)
            {
                return "";
            }

            string? dir = Directory
                .EnumerateDirectories(currentDir, searchPattern, SearchOption.TopDirectoryOnly)
                .SingleOrDefault();

            if (dir is not null)
            {
                return dir;
            }

            currentDir = Directory.GetParent(currentDir)?.FullName;
        } while (true);
    }

    private static Dictionary<string, long> GetPathsToBeTested(IEnumerable<string> directories)
    {
        Dictionary<string, long> pathsToBeTested = [];

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
