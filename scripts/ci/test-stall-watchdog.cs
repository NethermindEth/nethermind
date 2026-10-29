#!/usr/bin/env dotnet
// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include script-process.helpers.cs
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Xml.Linq;

if (args.FirstOrDefault()?.EndsWith(".py", StringComparison.Ordinal) == true)
{
    var call = new JsonArray();
    foreach (var argument in args) call.Add((JsonNode?)argument);
    File.AppendAllText(Path.Combine(Path.GetDirectoryName(args[0])!, "calls.jsonl"), call.ToJsonString() + "\n");
    return 0;
}
var root = new DirectoryInfo(Environment.CurrentDirectory);
while (root is not null && !File.Exists(Path.Combine(root.FullName, "scripts/ci/run-with-stall-watchdog.cs"))) root = root.Parent;
if (root is null) throw new DirectoryNotFoundException("Run tests from the repository.");
var dotnet = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(path => Path.Combine(path, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet")).First(File.Exists);
var script = Path.Combine(root.FullName, "scripts/ci/run-with-stall-watchdog.cs");
var passed = 0;
void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
async Task<int> ReadPid(string path, CancellationToken cancellationToken)
{
    while (true)
    {
        if (File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var pid) && pid > 0) return pid;
        await Task.Delay(10, cancellationToken);
    }
}
async Task CheckDead(int pid, CancellationToken cancellationToken)
{
    var elapsed = Stopwatch.StartNew();
    while (elapsed.Elapsed < TimeSpan.FromSeconds(5))
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            if (stat[(stat.LastIndexOf(')') + 1)..].TrimStart().StartsWith('Z')) return;
        }
        catch (FileNotFoundException) { return; }
        catch (DirectoryNotFoundException) { return; }
        await Task.Delay(20, cancellationToken);
    }
    throw new InvalidOperationException($"fixture process {pid} survived watchdog cleanup");
}
async Task<(int Code, string Output)> Run(string executable, string[] arguments, string cwd, Dictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
{
    if (environment is not null)
    {
        arguments = [.. environment.Select(pair => $"{pair.Key}={pair.Value}"), executable, .. arguments];
        executable = "env";
    }
    var result = await ScriptProcess.Run(executable, arguments, cancellationToken, workingDirectory: cwd);
    return (result.Code, result.Output + result.Error);
}
if (OperatingSystem.IsLinux())
{
    var built = Directory.CreateTempSubdirectory("watchdog build ");
    try
    {
        using var buildTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var build = await Run(dotnet, ["build", script, "--output", built.FullName], root.FullName, cancellationToken: buildTimeout.Token);
        Check(build.Code == 0, build.Output);
        var setsid = (await Run("which", ["setsid"], root.FullName)).Output.Trim();
        foreach (var scenario in new[] { "hung-install", "hung-stack", "healthy", "exit-zero", "exit-seven", "missing-command", "aggregate", "clamped-install", "missing-pid", "ignoring-child", "arguments", "cancel", "scheduler-tail", "scheduler-unreadable" })
        {
            var work = Directory.CreateTempSubdirectory("watchdog tests ");
            var tools = Directory.CreateDirectory(Path.Combine(work.FullName, "tools"));
            var diag = Path.Combine(work.FullName, "diagnostics");
            var scheduler = Directory.CreateDirectory(Path.Combine(work.FullName, "scheduler"));
            void Tool(string name, string body)
            {
                var path = Path.Combine(tools.FullName, name);
                File.WriteAllText(path, "#!/bin/bash\n" + body + "\n");
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var environment = new Dictionary<string, string>
            {
                ["PATH"] = tools.FullName + ":" + Environment.GetEnvironmentVariable("PATH"), ["DIAG_DIR"] = diag, ["MSBUILDDEBUGPATH"] = scheduler.FullName,
                ["STALL_SECONDS"] = "1", ["POLL_SECONDS"] = "0.25", ["DIAGNOSTIC_SECONDS"] = "3", ["INSTALL_TIMEOUT_SECONDS"] = "1", ["STACK_TIMEOUT_SECONDS"] = "1", ["TERMINATION_SECONDS"] = "0.5"
            };
            foreach (var tool in new[] { "dotnet", "dotnet-stack", "pgrep" }) Tool(tool, "exit 0");
            Tool("setsid", $"exec '{setsid}' --fork \"$@\"");
            var command = new[] { "sleep", "60" };
            if (scenario == "hung-install") Tool("dotnet", "echo install-evidence; sleep 60");
            if (scenario == "hung-stack") { Tool("dotnet-stack", "echo stack-evidence; sleep 60"); Tool("pgrep", "cat \"$DIAG_DIR/command.pid\"; echo $PPID; echo 999999"); }
            if (scenario is "hung-install" or "hung-stack") File.WriteAllText(Path.Combine(scheduler.FullName, "SchedulerState.txt"), "scheduler evidence");
            if (scenario == "healthy") { command = ["bash", "-c", "for i in {1..6}; do echo tick; sleep 0.15; done"]; environment["STALL_SECONDS"] = "2"; }
            if (scenario is "exit-zero" or "exit-seven") command = ["bash", "-c", scenario == "exit-zero" ? "exit 0" : "exit 7"];
            if (scenario == "missing-command") command = [Path.Combine(work.FullName, "missing-executable")];
            if (scenario == "aggregate") { Tool("pgrep", "seq 8"); Tool("dotnet-stack", "echo capture >> \"$DIAG_DIR/captures\"; sleep 60"); environment["DIAGNOSTIC_SECONDS"] = "3"; }
            if (scenario == "clamped-install") { Tool("dotnet", "sleep 60"); Tool("pgrep", "seq 8"); environment["INSTALL_TIMEOUT_SECONDS"] = "60"; environment["DIAGNOSTIC_SECONDS"] = "1"; }
            if (scenario == "missing-pid") { Tool("setsid", $"exec '{setsid}' \"$@\""); command = ["bash", "-c", "mv \"$DIAG_DIR/command.pid\" \"$DIAG_DIR/fallback.pid\"; exec sleep 60"]; }
            if (scenario == "ignoring-child") command = ["bash", "-c", "bash -c 'trap \"\" TERM; echo $BASHPID > \"$DIAG_DIR/child.pid\"; exec sleep 60' & wait"];
            if (scenario == "arguments") command = ["printf", "%s\\n", "space argument", "$(literal)", "a'b\"c"];
            if (scenario == "scheduler-tail") File.WriteAllText(Path.Combine(scheduler.FullName, "SchedulerState.txt"), new string('a', 5_000_010) + "evidence");
            if (scenario == "scheduler-unreadable")
            {
                File.CreateSymbolicLink(Path.Combine(scheduler.FullName, "missing.txt"), Path.Combine(work.FullName, "missing"));
                File.WriteAllText(Path.Combine(scheduler.FullName, "SchedulerState.txt"), "retained evidence");
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var run = Run(Path.Combine(built.FullName, "run-with-stall-watchdog"), command, root.FullName, environment, timeout.Token);
                if (scenario == "cancel")
                {
                    var group = await ReadPid(Path.Combine(diag, "command.pid"), timeout.Token);
                    // setsid --fork is the session's parent; its parent is the watchdog app.
                    int Parent(int pid) => int.Parse(File.ReadAllLines($"/proc/{pid}/status").First(line => line.StartsWith("PPid:", StringComparison.Ordinal))[5..].Trim());
                    var watchdog = Parent(Parent(group));
                    await Run("kill", ["-TERM", watchdog.ToString()], root.FullName, cancellationToken: timeout.Token);
                }
                var result = await run;
                var expected = scenario is "healthy" or "exit-zero" or "arguments" ? 0 : scenario == "exit-seven" ? 7 : scenario == "missing-command" ? 127 : scenario == "cancel" ? 143 : 1;
                Check(result.Code == expected, $"{scenario}: expected {expected}, got {result.Code}\n{result.Output}");
                if (scenario is "healthy" or "exit-zero" or "exit-seven" or "arguments") Check(!File.Exists(Path.Combine(diag, "system.txt")), "healthy command collected diagnostics");
                if (scenario == "healthy") Check(File.ReadAllText(Path.Combine(diag, "command.log")).Split("tick").Length - 1 == 6, "output missing");
                if (scenario == "arguments") Check(File.ReadAllText(Path.Combine(diag, "command.log")) == "space argument\n$(literal)\na'b\"c\n", "argument boundaries changed");
                if (scenario is "hung-install" or "hung-stack")
                {
                    Check(File.Exists(Path.Combine(diag, "msbuild-debug/SchedulerState.txt")), result.Output);
                    Check(File.ReadAllText(Path.Combine(diag, "msbuild-debug/SchedulerState.txt")) == "scheduler evidence", "scheduler evidence missing");
                    var diagnosticFile = scenario == "hung-install" ? Path.Combine(diag, "dotnet-stack-install.log") : Directory.GetFiles(diag, "stack-*.txt").Single();
                    if (scenario == "hung-stack") Check(Path.GetFileName(diagnosticFile) == "stack-999999.txt", "watchdog captured its own stack");
                    Check(File.ReadAllText(diagnosticFile).Contains("evidence", StringComparison.Ordinal), "timed-out diagnostic lost partial output");
                }
                if (scenario == "aggregate") Check(File.ReadAllLines(Path.Combine(diag, "captures")).Length is > 0 and <= 3, "aggregate deadline ignored");
                if (scenario == "clamped-install") Check(Directory.GetFiles(diag, "stack-*.txt").Length == 0, "stack captured after deadline");
                if (scenario == "scheduler-tail") Check(new FileInfo(Path.Combine(diag, "msbuild-debug/SchedulerState.txt")).Length == 5_000_000, "scheduler log unbounded");
                if (scenario == "scheduler-unreadable") Check(File.ReadAllText(Path.Combine(diag, "msbuild-debug/SchedulerState.txt")) == "retained evidence" && result.Output.Contains("Could not copy scheduler diagnostic", StringComparison.Ordinal), "unreadable scheduler file lost other diagnostics");
                foreach (var name in new[] { "command.pid", "fallback.pid", "child.pid" })
                    if (File.Exists(Path.Combine(diag, name))) await CheckDead(await ReadPid(Path.Combine(diag, name), timeout.Token), timeout.Token);
                Console.WriteLine($"PASS {scenario}"); passed++;
            }
            finally
            {
                foreach (var name in new[] { "command.pid", "fallback.pid" })
                    if (File.Exists(Path.Combine(diag, name)) && int.TryParse(File.ReadAllText(Path.Combine(diag, name)).Trim(), out var pid) && pid > 0)
                        await Run("kill", ["-KILL", "--", "-" + pid], root.FullName);
                work.Delete(recursive: true);
            }
        }
    }
    finally { built.Delete(recursive: true); }
}
foreach (var (count, designTime, existing) in new[] { (0, false, false), (1, false, false), (2, false, false), (0, true, false), (1, false, true) })
{
    var work = Directory.CreateTempSubdirectory("generator target ");
    try
    {
        var properties = new XElement("PropertyGroup", new XElement("GenerateRpcJsonContext", "true"), new XElement("DesignTimeBuild", designTime.ToString().ToLowerInvariant()));
        if (existing) properties.Add(new XElement("_DotnetHost", Environment.ProcessPath));
        var items = new XElement("ItemGroup");
        for (var index = 0; index < count; index++)
        {
            var name = existing ? $"generator {index}.py" : $"missing-{index}.dll";
            if (existing) File.WriteAllText(Path.Combine(work.FullName, name), "");
            items.Add(new XElement("_RpcJsonContextGeneratorTool", new XAttribute("Include", name)));
        }
        var project = new XElement("Project", properties, new XElement("Import", new XAttribute("Project", Path.Combine(root.FullName, "src/Nethermind/Directory.Build.targets"))), new XElement("Target", new XAttribute("Name", "ResolveReferences")), items);
        var path = Path.Combine(work.FullName, "test.proj");
        project.Save(path);
        var result = await Run(dotnet, ["msbuild", path, "-nologo", "-t:_GenerateRpcJsonSerializerContext"], work.FullName);
        Check((designTime || existing) == (result.Code == 0), result.Output);
        if (!designTime && !existing) Check(result.Output.Contains("RPC JSON context generator", StringComparison.Ordinal) && !result.Output.Contains("MSB3073", StringComparison.Ordinal), "missing actionable generator diagnostic");
        if (existing)
        {
            var calls = File.ReadAllLines(Path.Combine(work.FullName, "calls.jsonl"));
            Check(calls.Length == 1, "generator did not execute once");
            var call = JsonNode.Parse(calls[0])!.AsArray().Select(item => item!.ToString()).ToArray();
            Check(call[0] == Path.Combine(work.FullName, "generator 0.py") && call.Contains("--output") && call.Contains("--references"), "generator arguments changed");
        }
        Console.WriteLine($"PASS generator count={count} design-time={designTime} existing={existing}"); passed++;
    }
    finally { work.Delete(recursive: true); }
}
Console.WriteLine($"Passed {passed} tests.");
return 0;
