using System.Text.RegularExpressions;

bool doLog = true;

string hiveLogsDirectory = args.Length > 0 ? args[0] : "logs";

if(Directory.Exists(Path.Combine(hiveLogsDirectory, "logs")))
{
    hiveLogsDirectory = Path.Combine(hiveLogsDirectory, "logs");
}

string outputDirectory = args.Length > 1 ? args[1] : "output";
string jsonrpcUrl = args.Length > 2 ? args[2] : "http://localhost:8551";

string[] simulatorFilePaths = Directory.GetFiles(hiveLogsDirectory, "*-simulator-*.log").Select(f => Path.Combine(outputDirectory, f)).ToArray();

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

    string testListFile = Path.Combine(outputDirectory, "__tests.txt");
    HashSet<string> reportedContainers = [];

    Regex starter = new("^>>\\s+\\(([a-f0-9]+)\\)\\s+");

    string? line;
    int fileCounter = 0;

    // let's read line by line file that may be 2GB in size
    while ((line = simulatorFile.ReadLine()) is not null)
    {
        if (!line.StartsWith(">>"))
        {
            if(line.StartsWith("Start test"))
            {
                File.AppendAllText(testListFile, $"{line.Replace("Start test ", "")}\n");
            }
            continue;
        }

        string container = starter.Match(line).Groups[1].Value;
        if (!reportedContainers.Contains(container))
        {
            reportedContainers.Add(container);
            File.AppendAllText(testListFile, $"{container}\n");
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

            File.AppendAllText(string.Format(filename, container, "http"), @$"
###
POST {jsonrpcUrl}
Content-Type: application/json

<@ {dataFile}
");

            File.AppendAllText(string.Format(filename, container, "ps1"), @$"
$json = Get-Content {dataFile} -Raw
Invoke-WebRequest -Method POST -Uri {jsonrpcUrl} -Headers @{{'content-type'='application/json'}} -Body $json | Select-Object -expand RawContent
");


            File.AppendAllText(string.Format(filename, container, "sh"), @$"
curl -s --request POST --url {jsonrpcUrl} --header 'content-type: application/json' --data '@{dataFile}'
sleep 1; echo ""
");
        }
        else
        {
            File.AppendAllText(string.Format(filename, container, "http"), @$"
###
POST {jsonrpcUrl}
Content-Type: application/json

{json}
");
            json = json.Replace("\r", " ").Replace("\n", " ").Replace("'", "''");

            File.AppendAllText(string.Format(filename, container, "ps1"), @$"
Invoke-WebRequest -Method POST -Uri {jsonrpcUrl} -Headers @{{'content-type'='application/json'}} -Body '{json}' | Select-Object -expand RawContent
");

            File.AppendAllText(string.Format(filename, container, "sh"), @$"
curl -s --request POST --url {jsonrpcUrl} --header 'content-type: application/json' --data '{json}'
sleep 1; echo """"
");

        }

    }
}

if (doLog) Console.WriteLine($"Done!");
