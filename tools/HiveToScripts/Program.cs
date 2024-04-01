using System.Collections.Concurrent;
using System.Text.RegularExpressions;

bool doLog = true;

if (args.Intersect(["--help", "-h", "help", "?"]).Any())
{
    Console.WriteLine(
@"Transforms hive test execution output into sh/ps1/http scripts with big inputs dumped as separate files:

    ./HiveToScripts.exe [<path-to-hive-logs> [<output-path> [<json-rpc-url>]]]

Example:

    ./HiveToScripts.exe T:\logs T:\output http://localhost:8551
");
    return;
}

string hiveLogsDirectory = args.Length > 0 ? args[0] : "logs";

if (Directory.Exists(Path.Combine(hiveLogsDirectory, "logs")))
{
    hiveLogsDirectory = Path.Combine(hiveLogsDirectory, "logs");
}

string outputDirectory = args.Length > 1 ? args[1] : "output";
string jsonrpcUrl = args.Length > 2 ? args[2] : "http://localhost:8551";

string[] simulatorFilePaths = Directory.GetFiles(hiveLogsDirectory, "*-simulator-*.log").Select(f => Path.Combine(outputDirectory, f)).ToArray();
ConcurrentDictionary<string, TextWriter> fileDescriptors = new();

foreach (string simulatorFilePath in simulatorFilePaths)
{
    using StreamReader simulatorFile = File.OpenText(simulatorFilePath);

    if (Directory.Exists(outputDirectory))
    {
        foreach (string f in Directory.EnumerateFiles(outputDirectory))
        {
            File.Delete(f);
        }
    }
    Directory.CreateDirectory(outputDirectory);
    if (doLog) Console.WriteLine($"Running...");
    HashSet<string> createdFiles = [];

    using StreamWriter testListWriter = new(Path.Combine(outputDirectory, "__tests.txt"));
    HashSet<string> reportedContainers = [];

    Regex starter = new("^>>\\s+\\(([a-f0-9]+)\\)\\s+");

    string? line;
    int fileCounter = 0;

    // let's read line by line file that may be 2GB in size
    while ((line = simulatorFile.ReadLine()) is not null)
    {
        if (!line.StartsWith(">>"))
        {
            if (line.StartsWith("Start test"))
            {
                testListWriter.WriteLine(line.Replace("Start test ", ""));
            }
            continue;
        }

        string container = starter.Match(line).Groups[1].Value;
        if (!reportedContainers.Contains(container))
        {
            reportedContainers.Add(container);
            testListWriter.WriteLine(container);
        }

        string value = starter.Replace(line, "");

        string filename = Path.Combine(outputDirectory, "{0}.{1}");
        string cfgFilename = Path.Combine(outputDirectory, $"{container}.cfg");
        string specFilename = Path.Combine(outputDirectory, $"{container}.json");

        if (!createdFiles.Contains(cfgFilename))
        {
            createdFiles.Add(cfgFilename);
            string elOutputFilenamePath = Directory.GetFiles(hiveLogsDirectory, $"client-{container}*.log", SearchOption.AllDirectories).Single();
            if (elOutputFilenamePath is not null)
            {
                elOutputFilenamePath = Path.Combine(hiveLogsDirectory, elOutputFilenamePath);
                using StreamReader elOutputFile = File.OpenText(elOutputFilenamePath);
                string? elLine;

                elOutputFile.ReadLine();
                string spec = "";
                do
                {
                    elLine = elOutputFile.ReadLine();
                    spec += elLine + "\n";
                }
                while (elLine != "}");

                elOutputFile.ReadLine();
                string cfg = "";
                do
                {
                    elLine = elOutputFile.ReadLine();
                    cfg += elLine + "\n";
                }
                while (elLine != "}");

                File.WriteAllText(cfgFilename, cfg);

                File.WriteAllText(specFilename, spec);
            }
        }

        string json = value;

        if (json.Length > 7500)
        {
            string dataFile = string.Format(filename, container, $"data-{++fileCounter}.txt");
            File.WriteAllText(dataFile, json);

            fileDescriptors.GetOrAdd(string.Format(filename, container, "http"), OpenFile).Write(@$"
###
POST {jsonrpcUrl}
Content-Type: application/json

<@ {dataFile}
");

            fileDescriptors.GetOrAdd(string.Format(filename, container, "ps1"), OpenFile).Write(@$"
$json = Get-Content {dataFile} -Raw
Invoke-WebRequest -Method POST -Uri {jsonrpcUrl} -Headers @{{'content-type'='application/json'}} -Body $json | Select-Object -expand RawContent
");

            fileDescriptors.GetOrAdd(string.Format(filename, container, "sh"), OpenFile).Write(@$"
curl -s --request POST --url {jsonrpcUrl} --header 'content-type: application/json' --data '@{dataFile}'
sleep 1; echo ""
");
        }
        else
        {
            fileDescriptors.GetOrAdd(string.Format(filename, container, "http"), OpenFile).Write(@$"
###
POST {jsonrpcUrl}
Content-Type: application/json

{json}
");
            json = json.Replace("\r", " ").Replace("\n", " ").Replace("'", "''");

            fileDescriptors.GetOrAdd(string.Format(filename, container, "ps1"), OpenFile).Write(@$"
Invoke-WebRequest -Method POST -Uri {jsonrpcUrl} -Headers @{{'content-type'='application/json'}} -Body '{json}' | Select-Object -expand RawContent
");

            fileDescriptors.GetOrAdd(string.Format(filename, container, "sh"), OpenFile).Write(@$"
curl -s --request POST --url {jsonrpcUrl} --header 'content-type: application/json' --data '{json}'
sleep 1; echo """"
");

        }
    }
}

foreach (TextWriter tw in fileDescriptors.Values)
{
    tw.Dispose();
}
if (doLog) Console.WriteLine($"Done!");

static TextWriter OpenFile(string filePath) => new StreamWriter(filePath);
