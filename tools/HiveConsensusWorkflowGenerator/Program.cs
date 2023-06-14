namespace HiveConsensusWorkflowGenerator;

public static class Program
{
    const long MaxSizeWithoutSplitting = 35_000_000;
    const long TargetSize = 23_000_000;
    const long PenaltyForAdditionalInit = 300_000;

    static void Main(string[] args)
    {
        string path = args.FirstOrDefault() is not null ? args.First() : "src/tests";

        IEnumerable<string> directories = GetTestsDirectories(path);
        Dictionary<string, long> pathsToBeTested = GetPathsToBeTested(directories);
        IEnumerable<List<string>> accumulatedJobs = GetTestsSplittedToJobs(pathsToBeTested);

        TextWriter fileContent = CreateTextWriter();

        WriteInitialLines(fileContent);

        int jobsCreated = 0;
        foreach (List<string> job in accumulatedJobs)
        {
            WriteJob(fileContent, job, ++jobsCreated);
        }

        fileContent.Dispose();
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

    private static TextWriter CreateTextWriter()
    {
        string fileDirectory = $"{FindDirectory(".github")}/workflows/hive-consensus-tests.yml";
        FileStream file = File.Open(fileDirectory, FileMode.Create, FileAccess.Write);
        return new StreamWriter(file);
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

    private static IEnumerable<List<string>> GetTestsSplittedToJobs(Dictionary<string, long> pathsToBeTested)
    {
        List<string> accumulator = new();
        long sum = 0;

        foreach (KeyValuePair<string, long> directory in pathsToBeTested)
        {
            string dirName = Path.GetFileName(directory.Key);

            if (directory.Value > TargetSize)
            {
                yield return new List<string>() { dirName };
                continue;
            }

            if (sum + directory.Value > TargetSize)
            {
                yield return new List<string>(accumulator);
                accumulator.Clear();
                sum = 0;
            }

            accumulator.Add(dirName);
            sum += directory.Value;
            sum += PenaltyForAdditionalInit;
        }

        if (accumulator.Count > 0)
        {
            yield return accumulator;
        }
    }

    private static void WriteInitialLines(TextWriter fileContent)
    {
        fileContent.WriteLine("name: 'Hive consensus tests' ");
        fileContent.WriteLine("");
        fileContent.WriteLine("on:");
        fileContent.WriteLine("  push:");
        fileContent.WriteLine("    tags: ['*']");
        fileContent.WriteLine("  workflow_dispatch:");
        fileContent.WriteLine("    inputs:");
        fileContent.WriteLine("      parallelism:");
        fileContent.WriteLine("        description: 'Number of concurrently running tests in each job. With 1 or 2 timeout is likely. With 4 or more false-positive fails are likely. Recommended is 3 to avoid timeouts and reduce false-positives'");
        fileContent.WriteLine("        required: true");
        fileContent.WriteLine("        default: '3'");
        fileContent.WriteLine("        type: choice");
        fileContent.WriteLine("        options: ['1', '2', '3', '4', '8', '16']");
        fileContent.WriteLine("");
        fileContent.WriteLine("jobs:");
    }

    private static void WriteJob(TextWriter fileContent, List<string> tests, int jobNumber)
    {
        string jobName = tests.Count > 1 ? $"{jobNumber}. Combined tests (e.g. {tests.First().Split('.').First()})" : $"{jobNumber}. {tests.First().Split('.').First()}";

        fileContent.WriteLine($"  test_{jobNumber}:");
        fileContent.WriteLine($"    name: {jobName}");
        fileContent.WriteLine("    runs-on: ubuntu-latest");
        fileContent.WriteLine("    steps:");
        fileContent.WriteLine("      - name: Set up parameters");
        fileContent.WriteLine("        run: |");
        fileContent.WriteLine("          echo \"PARALLELISM=${{ github.event.inputs.parallelism || '3' }}\" >> $GITHUB_ENV");
        fileContent.WriteLine("      - name: Check out Nethermind repository");
        fileContent.WriteLine("        uses: actions/checkout@v3");
        fileContent.WriteLine("        with:");
        fileContent.WriteLine("          path: nethermind");
        fileContent.WriteLine("      - name: Set up QEMU");
        fileContent.WriteLine("        uses: docker/setup-qemu-action@v2");
        fileContent.WriteLine("      - name: Set up Docker Buildx");
        fileContent.WriteLine("        uses: docker/setup-buildx-action@v2");
        fileContent.WriteLine("      - name: Build Docker image");
        fileContent.WriteLine("        uses: docker/build-push-action@v3");
        fileContent.WriteLine("        with:");
        fileContent.WriteLine("          context: nethermind");
        fileContent.WriteLine("          file: nethermind/Dockerfile");
        fileContent.WriteLine("          tags: nethermind:test-${{ github.sha }}");
        fileContent.WriteLine("          outputs: type=docker,dest=/tmp/image.tar");
        fileContent.WriteLine("      - name: Install Linux packages");
        fileContent.WriteLine("        run: |");
        fileContent.WriteLine("          sudo apt-get update");
        fileContent.WriteLine("          sudo apt-get install libsnappy-dev libc6-dev libc6 build-essential");
        fileContent.WriteLine("      - name: Set up Go environment");
        fileContent.WriteLine("        uses: actions/setup-go@v3.0.0");
        fileContent.WriteLine("        with:");
        fileContent.WriteLine("          go-version: '>=1.17.0'");
        fileContent.WriteLine("      - name: Check out Hive repository");
        fileContent.WriteLine("        uses: actions/checkout@v3");
        fileContent.WriteLine("        with:");
        fileContent.WriteLine("          repository: ethereum/hive");
        fileContent.WriteLine("          ref: master");
        fileContent.WriteLine("          path: hive");
        fileContent.WriteLine("      - name: Patch Hive Dockerfile");
        fileContent.WriteLine("        run: sed -i 's#FROM $baseimage:$tag#FROM nethermind:test-${{ github.sha }}#g' hive/clients/nethermind/Dockerfile");
        fileContent.WriteLine("      - name: Build Hive");
        fileContent.WriteLine("        working-directory: hive");
        fileContent.WriteLine("        run: go build .");
        fileContent.WriteLine("      - name: Load Docker image");
        fileContent.WriteLine("        run: docker load --input /tmp/image.tar");

        foreach (string test in tests)
        {
            string testWithoutJson = test.Split('.').First();
            fileContent.WriteLine($"      - name: Run {testWithoutJson}");
            fileContent.WriteLine("        continue-on-error: true");
            fileContent.WriteLine("        working-directory: hive");
            fileContent.WriteLine($"        run: ./hive --client nethermind --sim ethereum/consensus --sim.limit /{testWithoutJson} --sim.parallelism $PARALLELISM");
        }

        fileContent.WriteLine("      - name: Print results");
        fileContent.WriteLine("        run: |");
        fileContent.WriteLine("          chmod +x nethermind/scripts/hive-results.sh");
        fileContent.WriteLine("          nethermind/scripts/hive-results.sh \"hive/workspace/logs/*.json\"");
    }
}
