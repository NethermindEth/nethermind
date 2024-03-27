using System.Text.RegularExpressions;

bool doLog = true;

string hiveLogsDirectory = args.Length > 0 ? args[0] : "T:\\logs";
string outputDirectory = args.Length > 1 ? args[1] : "T:\\output";
string jsonrpcUrl = args.Length > 2 ? args[2] : "http://localhost:8551";

string simulatorFilePath = Path.Combine(outputDirectory, Directory.GetFiles(hiveLogsDirectory, "*-simulator-*.log").Single());

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
HashSet<string> createdFiles = new();

Regex starter = new Regex("^>>\\s+\\(([a-f0-9]+)\\)\\s+");

string? line;
int fileCounter = 0;

// let's read line by line file that may be 2GB in size
while ((line = simulatorFile.ReadLine()) is not null)
{
    if (!line.StartsWith(">>"))
    {
        continue;
    }

    string container = starter.Match(line).Groups[1].Value;
    string value = starter.Replace(line, "");

    string filename = Path.Combine(outputDirectory, "hive.{0}.{1}");
    string cfgFilename = Path.Combine(outputDirectory, $"hive.{container}-config.cfg");
    string specFilename = Path.Combine(outputDirectory, $"hive.{container}-spec.json");

    if (!createdFiles.Contains(cfgFilename))
    {
        createdFiles.Add(cfgFilename);
        string elOutputFilenamePath = Path.Combine(hiveLogsDirectory, Directory.GetFiles(hiveLogsDirectory, $"nethermind/client-{container}*.log").Single());
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
    //var values = lines.Where(l => l.key == key).Select(x => x.value).ToList();

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
if (doLog) Console.WriteLine($"Done!");

