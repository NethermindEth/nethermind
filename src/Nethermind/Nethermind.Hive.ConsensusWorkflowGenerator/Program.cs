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

        int jobsCreated = 0;
        foreach (string directory in directories)
        {
            string parentDirectory = Directory.GetParent(directory).ToString();
            string prefix = Path.GetFileName(parentDirectory)[..2];
            if (!prefix.Equals("st") && !prefix.Equals("bc"))
            {
                CreateContent(fileContent, directory, ref jobsCreated);
            }
        }

        File.WriteAllText("hive-consensus-tests.yml", fileContent.ToString());
    }

    private static void CreateContent(StringBuilder fileContent, string directory, ref int jobsCreated)
    {
        string directoryName = Path.GetFileName(directory);

        fileContent.AppendLine($"  test{++jobsCreated}:");
        fileContent.AppendLine($"    name: {directoryName}");
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
        fileContent.AppendLine("      - name: Run Hive");
        fileContent.AppendLine("        continue-on-error: true");
        fileContent.AppendLine("        working-directory: hive");
        fileContent.AppendLine($"        run: ./hive --client nethermind --sim ethereum/consensus --sim.limit /{directoryName} --sim.parallelism 16");
        fileContent.AppendLine("      - name: Upload results");
        fileContent.AppendLine("        uses: actions/upload-artifact@v3");
        fileContent.AppendLine("        with:");
        fileContent.AppendLine($"          name: results-{directoryName}-${{ github.run_number }}-${{ github.run_attempt }}");
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
