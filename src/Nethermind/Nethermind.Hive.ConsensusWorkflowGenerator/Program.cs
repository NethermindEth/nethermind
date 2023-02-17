using System.Text;

namespace Nethermind.Hive.ConsensusWorkflowGenerator;

public static class Program
{
    static void Main(string[] args)
    {
        StringBuilder fileContent = new();

        string testsDirectory = FindTestsDirectory();
        List<string> directories = Directory.GetDirectories(testsDirectory, "st*", SearchOption.AllDirectories).ToList();
        directories.AddRange(Directory.GetDirectories(testsDirectory, "bc*", SearchOption.AllDirectories).ToList());

        fileContent.AppendLine("name: 'Hive consensus tests' ");
        fileContent.AppendLine("");
        fileContent.AppendLine("on:");
        fileContent.AppendLine("  push:");
        fileContent.AppendLine("    tags: ['*']");
        fileContent.AppendLine("  workflow_dispatch:");
        fileContent.AppendLine("");
        fileContent.AppendLine("jobs:");

        Dictionary<string, long> filesToBeTested = new();
        Dictionary<string, long> directoriesToBeTested = new();
        const long targetSize = 12_000_000;


        List<List<string>> accumulatedData = new();
        List<string> accumulator = new();
        long sum = 0;

        int jobsCreated = 0;
        foreach (string directory in directories)
        {
            long subsum = 0;

            string parentDirectory = Directory.GetParent(directory).ToString();
            string prefix = Path.GetFileName(parentDirectory)[..2];
            if (!prefix.Equals("st") && !prefix.Equals("bc"))
            {
                //CreateContent(fileContent, directory, ref jobsCreated);

                foreach (string file in Directory.GetFiles(directory))
                {
                    long fileSize = (new FileInfo(file)).Length;
                    subsum += fileSize;
                }

                if (subsum < targetSize)
                {
                    directoriesToBeTested.Add(directory, subsum);
                }
                else
                {
                    foreach (string file in Directory.GetFiles(directory))
                    {
                        string fileName = Path.GetFileName(file);
                        long fileSize = (new FileInfo(file)).Length;
                        if (filesToBeTested.TryGetValue(fileName, out long size))
                        {
                            size += fileSize;
                        }
                        else
                        {
                            filesToBeTested.Add(fileName, fileSize);
                        }

                        // sum += fileSize;
                    }
                }
            }
        }


        foreach (var directory in directoriesToBeTested)
        {
            if (directory.Value > 0 && sum + directory.Value > targetSize)
            {
                accumulatedData.Add(new List<string>(accumulator));
                accumulator.Clear();
                sum = 0;
            }

            string dirName = Path.GetFileName(directory.Key);
            accumulator.Add(dirName);
            sum += directory.Value;
        }

        if (accumulator.Count > 0)
        {
            accumulatedData.Add(new List<string>(accumulator));
            accumulator.Clear();
            sum = 0;
        }


        sum = 0;

        foreach (var fileToBeTested in filesToBeTested)
        {
            if (fileToBeTested.Value > 0 && sum + fileToBeTested.Value > targetSize)
            {
                accumulatedData.Add(new List<string>(accumulator));
                accumulator.Clear();
                sum = 0;
            }

            accumulator.Add(fileToBeTested.Key);
            sum += fileToBeTested.Value;
        }

        if (accumulator.Count > 0)
        {
            accumulatedData.Add(new List<string>(accumulator));
            accumulator.Clear();
            sum = 0;
        }

        foreach (List<string> run in accumulatedData)
        {
            CreateContent(fileContent, run, ref jobsCreated);
        }

        File.WriteAllText("testy.txt", fileContent.ToString());
    }

    private static void CreateContent(StringBuilder fileContent, List<string> tests, ref int jobsCreated)
    {

        fileContent.AppendLine($"  test{++jobsCreated}:");
        fileContent.AppendLine($"    name: {jobsCreated}");
        fileContent.AppendLine("    runs-on: ubuntu-latest");
        fileContent.AppendLine("    steps:");
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
            fileContent.AppendLine($"        run: ./hive --client nethermind --sim ethereum/consensus --sim.limit /{testWithoutJson} --sim.parallelism 16");
        }

        fileContent.AppendLine("      - name: Upload results");
        fileContent.AppendLine("        uses: actions/upload-artifact@v3");
        fileContent.AppendLine("        with:");
        fileContent.AppendLine($"          name: results-{jobsCreated}-${{ github.run_number }}-${{ github.run_attempt }}");
        fileContent.AppendLine("          path: hive/workspace");
        fileContent.AppendLine("          retention-days: 7");
        fileContent.AppendLine("      - name: Print results");
        fileContent.AppendLine("        run: |");
        fileContent.AppendLine("          chmod +x nethermind/scripts/hive-results.sh");
        fileContent.AppendLine("          nethermind/scripts/hive-results.sh \"hive/workspace/logs/*.json\"");
    }

    private static string FindTestsDirectory()
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
