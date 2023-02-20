using System.Text;

namespace Nethermind.Hive.ConsensusWorkflowGenerator;

public static class Program
{
    static void Main(string[] args)
    {
        StringBuilder fileContent = new();

        List<string> directories = GetTestsDirectories();
        Dictionary<string, long> pathsToBeTested = GetPathsToBeTested(directories);
        List<List<string>> accumulatedJobs = SplitTestsToJobs(pathsToBeTested);

        // int i = 0;
        // foreach (List<string> list in accumulatedData)
        // {
        //     foreach (string s in list)
        //     {
        //         i++;
        //     }
        // }
        //
        // fileContent.AppendLine(i.ToString());

        WriteInitialLines(fileContent);

        int jobsCreated = 0;
        foreach (List<string> job in accumulatedJobs)
        {
            WriteJobs(fileContent, job, ++jobsCreated);
        }

        File.WriteAllText("testy11.txt", fileContent.ToString());
    }

    private static Dictionary<string, long> GetPathsToBeTested(List<string> directories)
    {
        const long maxSizeWithoutSplitting = 35_000_000;
        Dictionary<string, long> pathsToBeTested = new();

        foreach (string directory in directories)
        {
            long subsum = 0;

            string parentDirectory = Directory.GetParent(directory).ToString();
            string prefix = Path.GetFileName(parentDirectory)[..2];
            if (!prefix.Equals("st") && !prefix.Equals("bc"))
            {
                foreach (string file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
                {
                    long fileSize = (new FileInfo(file)).Length;
                    subsum += fileSize;
                }

                if (subsum < maxSizeWithoutSplitting)
                {
                    pathsToBeTested.Add(directory, subsum);
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

    private static List<List<string>> SplitTestsToJobs(Dictionary<string, long> pathsToBeTested)
    {
        const long targetSize = 23_000_000;
        const long penaltyForAdditionalInit = 300_000;

        List<List<string>> accumulatedJobs = new();
        List<string> accumulator = new();
        long sum = 0;

        foreach (KeyValuePair<string, long> directory in pathsToBeTested)
        {
            string dirName = Path.GetFileName(directory.Key);

            if (directory.Value > targetSize)
            {
                accumulatedJobs.Add(new List<string>() { dirName });
                continue;
            }

            if (sum + directory.Value > targetSize)
            {
                accumulatedJobs.Add(new List<string>(accumulator));
                accumulator.Clear();
                sum = 0;
            }

            accumulator.Add(dirName);
            sum += directory.Value;
            sum += penaltyForAdditionalInit;
        }

        if (accumulator.Count > 0)
        {
            accumulatedJobs.Add(new List<string>(accumulator));
        }

        return accumulatedJobs;
    }

    private static void WriteInitialLines(StringBuilder fileContent)
    {
        fileContent.AppendLine("name: 'Hive consensus tests' ");
        fileContent.AppendLine("");
        fileContent.AppendLine("on:");
        fileContent.AppendLine("  push:");
        fileContent.AppendLine("    tags: ['*']");
        fileContent.AppendLine("  workflow_dispatch:");
        fileContent.AppendLine("    inputs:");
        fileContent.AppendLine("      parallelism:");
        fileContent.AppendLine("        description: 'number of concurrently running tests in each job. With just 1 there will be timeout. With 4 or more false-positive fails are likely. Recommended is 2 to avoid timeouts and flakiness'");
        fileContent.AppendLine("        required: true");
        fileContent.AppendLine("        default: '2'");
        fileContent.AppendLine("        type: choice");
        fileContent.AppendLine("        options: ['1', '2', '3', '4', '8', '16']");
        fileContent.AppendLine("");
        fileContent.AppendLine("jobs:");
    }

    private static void WriteJobs(StringBuilder fileContent, List<string> tests, int jobNumber)
    {
        string jobName = tests.Count > 1 ? $"{jobNumber}: Combined tests" : $"{jobNumber}: {tests.First().Split('.').First()}";

        fileContent.AppendLine($"  test_{jobNumber}:");
        fileContent.AppendLine($"    name: {jobName}");
        fileContent.AppendLine("    runs-on: ubuntu-latest");
        fileContent.AppendLine("    steps:");
        fileContent.AppendLine("      - name: Set up parameters");
        fileContent.AppendLine("        run: |");
        fileContent.AppendLine("          echo \"PARALLELISM=${{ github.event.inputs.parallelism || '2' }}\" >> $GITHUB_ENV");
        fileContent.AppendLine("      - name: Check out Nethermind repository");
        fileContent.AppendLine("        uses: actions/checkout@v3");
        fileContent.AppendLine("        with:");
        fileContent.AppendLine("          path: nethermind");
        fileContent.AppendLine("      - name: Set up QEMU");
        fileContent.AppendLine("        uses: docker/setup-qemu-action@v2");
        fileContent.AppendLine("      - name: Set up Docker Buildx");
        fileContent.AppendLine("        uses: docker/setup-buildx-action@v2");
        fileContent.AppendLine("      - name: Build Docker image");
        fileContent.AppendLine("        uses: docker/build-push-action@v3");
        fileContent.AppendLine("        with:");
        fileContent.AppendLine("          context: nethermind");
        fileContent.AppendLine("          file: nethermind/Dockerfile");
        fileContent.AppendLine("          tags: nethermind:test-${{ github.sha }}");
        fileContent.AppendLine("          outputs: type=docker,dest=/tmp/image.tar");
        fileContent.AppendLine("      - name: Install Linux packages");
        fileContent.AppendLine("        run: |");
        fileContent.AppendLine("          sudo apt-get update");
        fileContent.AppendLine("          sudo apt-get install libsnappy-dev libc6-dev libc6 build-essential");
        fileContent.AppendLine("      - name: Set up Go environment");
        fileContent.AppendLine("        uses: actions/setup-go@v3.0.0");
        fileContent.AppendLine("        with:");
        fileContent.AppendLine("          go-version: '>=1.17.0'");
        fileContent.AppendLine("      - name: Check out Hive repository");
        fileContent.AppendLine("        uses: actions/checkout@v3");
        fileContent.AppendLine("        with:");
        fileContent.AppendLine("          repository: ethereum/hive");
        fileContent.AppendLine("          ref: master");
        fileContent.AppendLine("          path: hive");
        fileContent.AppendLine("      - name: Patch Hive Dockerfile");
        fileContent.AppendLine("        run: sed -i 's#FROM nethermindeth/hive:$branch#FROM nethermind:test-${{ github.sha }}#g' hive/clients/nethermind/Dockerfile");
        fileContent.AppendLine("      - name: Build Hive");
        fileContent.AppendLine("        working-directory: hive");
        fileContent.AppendLine("        run: go build .");
        fileContent.AppendLine("      - name: Load Docker image");
        fileContent.AppendLine("        run: docker load --input /tmp/image.tar");

        foreach (string test in tests)
        {
            string testWithoutJson = test.Split('.').First();
            fileContent.AppendLine($"      - name: Run {testWithoutJson}");
            fileContent.AppendLine("        continue-on-error: true");
            fileContent.AppendLine("        working-directory: hive");
            fileContent.AppendLine($"        run: ./hive --client nethermind --sim ethereum/consensus --sim.limit /{testWithoutJson} --sim.parallelism $PARALLELISM");
        }

        fileContent.AppendLine("      - name: Upload results");
        fileContent.AppendLine("        uses: actions/upload-artifact@v3");
        fileContent.AppendLine("        with:");
        fileContent.AppendLine($"          name: results-{jobNumber}-${{ github.run_number }}-${{ github.run_attempt }}");
        fileContent.AppendLine("          path: hive/workspace");
        fileContent.AppendLine("          retention-days: 7");
        fileContent.AppendLine("      - name: Print results");
        fileContent.AppendLine("        run: |");
        fileContent.AppendLine("          chmod +x nethermind/scripts/hive-results.sh");
        fileContent.AppendLine("          nethermind/scripts/hive-results.sh \"hive/workspace/logs/*.json\"");
    }

    private static List<string> GetTestsDirectories()
    {
        string testsDirectory = FindTestsMainDirectory();

        List<string> directories = Directory.GetDirectories(testsDirectory, "st*", SearchOption.AllDirectories).ToList();
        directories.AddRange(Directory.GetDirectories(testsDirectory, "bc*", SearchOption.AllDirectories).ToList());

        return directories;


        string FindTestsMainDirectory()
        {
            string currentDir = Environment.CurrentDirectory;
            do
            {
                if (currentDir == null)
                {
                    return null;
                }

                var dir = Directory
                    .EnumerateDirectories(currentDir, "tests", SearchOption.TopDirectoryOnly)
                    .SingleOrDefault();

                if (dir != null)
                {
                    return string.Concat(dir, "/BlockchainTests");
                }

                currentDir = Directory.GetParent(currentDir)?.FullName;
            } while (true);
        }
    }
}
