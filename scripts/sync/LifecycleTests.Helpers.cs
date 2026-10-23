// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json.Nodes;

sealed class LifecycleFixture : IDisposable
{
    internal readonly string Directory = System.IO.Directory.CreateTempSubdirectory("sync-lifecycle-").FullName;
    internal readonly Dictionary<string, string> Environment = new()
    {
        ["RUNNER_LABEL"] = "f-1-master-mainnet", ["GITHUB_RUN_ATTEMPT"] = "1", ["GITHUB_RUN_ID"] = "999", ["GITHUB_REPOSITORY"] = "o/r", ["GH_TOKEN"] = "x", ["RUNNER_GROUP_ID"] = "1", ["MACHINE_TYPE"] = "c2-standard-8", ["LOCAL_SSD_COUNT"] = "2", ["SPOT_FALLBACK_TO_STANDARD"] = "false", ["PROVISIONING_MODEL"] = "STANDARD", ["ZONES"] = "europe-west1-b", ["BOOT_TIMEOUT"] = "0"
    };
    internal readonly List<string[]> Commands = [];
    internal readonly List<string> Attempts = [];
    internal readonly List<string> Models = [];
    internal Func<string, string[], CommandResult>? Handler { get; set; }
    internal string SucceedZone = "";
    internal string SucceedModel = "";
    internal string NotOffered = "";
    internal string Capacity = "";
    internal bool GlobalQuota { get; set; }
    internal bool IsDestroy { get; set; }
    internal string InstanceZone = "";
    internal int LookupStatus { get; set; }
    internal string Operations = "compute.instances.preempted";
    internal string InsertTime = "";
    internal int DeleteStatus { get; set; }
    internal string OnlineStatus = "online";
    internal int HttpStatus = 200;
    internal string Transcript = "";
    internal LifecycleFixture()
    {
        Environment["RUNNER_TEMP"] = Directory;
        Environment["GITHUB_OUTPUT"] = Path.Combine(Directory, "outputs");
        Environment["GITHUB_STEP_SUMMARY"] = Path.Combine(Directory, "summary");
    }
    internal Dictionary<string, string> Outputs => File.Exists(Environment["GITHUB_OUTPUT"]) ? File.ReadAllLines(Environment["GITHUB_OUTPUT"]).Select(line => line.Split('=', 2)).ToDictionary(parts => parts[0], parts => parts[1]) : [];
    internal string Summary => File.Exists(Environment["GITHUB_STEP_SUMMARY"]) ? File.ReadAllText(Environment["GITHUB_STEP_SUMMARY"]) : "";
    internal async Task<int> Execute(CancellationToken token = default)
    {
        var lifecycle = new Lifecycle(Run, key => Environment.GetValueOrDefault(key), (_, _) => Task.CompletedTask);
        var original = Console.Out;
        using var output = new StringWriter();
        Console.SetOut(output);
        try { return IsDestroy ? await lifecycle.Destroy(token) : await lifecycle.Create(Path.Combine(Cases.Root(), ".github/actions/gcp-runner"), token); }
        finally { Transcript = output.ToString(); Console.SetOut(original); }
    }
    private Task<CommandResult> Run(string executable, string[] arguments, string? input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Commands.Add([executable, .. arguments]);
        if (Handler is { } handler) return Task.FromResult(handler(executable, arguments));
        if (executable == "gh")
        {
            if (arguments.Contains("POST"))
            {
                var request = JsonNode.Parse(input!)!;
                Cases.Equal("_work", request["work_folder"]!.ToString());
                return Task.FromResult(new CommandResult(0, """{"runner":{"id":42},"encoded_jit_config":"ZmFrZQ=="}"""));
            }
            return Task.FromResult(new CommandResult(0));
        }
        if (executable == "curl")
        {
            File.WriteAllText(Path.Combine(Directory, "runner.json"), new JsonObject { ["status"] = OnlineStatus }.ToJsonString());
            return Task.FromResult(new CommandResult(0, HttpStatus.ToString()));
        }
        if (arguments.Contains("instances") && arguments.Contains("list")) return Task.FromResult(new CommandResult(LookupStatus, InstanceZone));
        if (arguments.Contains("delete")) return Task.FromResult(new CommandResult(DeleteStatus));
        if (arguments.Contains("operations")) return Task.FromResult(new CommandResult(0, arguments.Contains("--format=value(insertTime)") ? InsertTime : Operations));
        if (!arguments.Contains("create")) return Task.FromResult(new CommandResult(0));
        var zone = arguments.First(arg => arg.StartsWith("--zone=", StringComparison.Ordinal))[7..];
        var model = arguments.First(arg => arg.StartsWith("--provisioning-model=", StringComparison.Ordinal))[21..];
        Attempts.Add(zone);
        Models.Add(model);
        if (zone == SucceedZone && (SucceedModel.Length == 0 || model == SucceedModel)) return Task.FromResult(new CommandResult(0, """[{"networkInterfaces":[{"accessConfigs":[{"natIP":"1.2.3.4"}]}]}]"""));
        var error = NotOffered.Split(',').Contains(zone) ? "Machine type with name 'c3d' does not exist in zone 'z'"
            : Capacity.Split(',').Contains(zone) ? "ZONE_RESOURCE_POOL_EXHAUSTED"
            : GlobalQuota ? "Quota 'CPUS_ALL_REGIONS' exceeded globally." : $"Quota 'LOCAL_SSD_TOTAL_GB' exceeded in region {Gcp.Region(zone)}.";
        return Task.FromResult(new CommandResult(1, Error: error));
    }
    public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
}
