// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Diagnostics;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

internal sealed partial class WorkflowTest
{
    internal string Root { get; }
    internal YamlMappingNode Document { get; }
    internal WorkflowTest(string file)
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "global.json"))) directory = directory.Parent;
        Root = directory?.FullName ?? throw new IOException("repository not found");
        using var reader = File.OpenText(Path.Combine(Root, ".github", "workflows", file));
        var yaml = new YamlStream(); yaml.Load(reader);
        Document = (YamlMappingNode)yaml.Documents[0].RootNode;
    }
    internal static YamlNode? Get(YamlNode? node, string key) => node is YamlMappingNode mapping && mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) ? value : null;
    internal static string Text(YamlNode? node, string key) => (Get(node, key) as YamlScalarNode)?.Value ?? "";
    internal YamlMappingNode Job(string name) => (YamlMappingNode)Get(Get(Document, "jobs"), name)!;
    internal YamlMappingNode Step(string job, string name) => ((YamlSequenceNode)Get(Job(job), "steps")!).Children.Cast<YamlMappingNode>().Single(step => Text(step, "name") == name);
    internal static string Expand(string script, Dictionary<string, string>? expressions = null) => ExpressionRegex().Replace(script, match => expressions?.GetValueOrDefault(match.Groups[1].Value.Trim(), "x") ?? "x");

    internal (int Code, string Output, string Error) Run(string script, Dictionary<string, string> environment, string temporaryDirectory, Dictionary<string, string>? expressions = null)
    {
        var path = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".sh");
        File.WriteAllText(path, Expand(script, expressions));
        var start = new ProcessStartInfo("bash") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root };
        foreach (var argument in new[] { "--noprofile", "--norc", "-eo", "pipefail", path }) start.ArgumentList.Add(argument);
        foreach (var (name, value) in environment) start.Environment[name] = value;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try
        {
            if (!process.WaitForExit(120_000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("workflow step timed out"); }
            return (process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
        }
        finally { File.Delete(path); }
    }

    internal (int Code, Dictionary<string, string> Values, string Log) Resolve(Dictionary<string, string> inputs, string temporaryDirectory)
    {
        var step = Step("resolve", "Resolve configuration");
        var defaults = (YamlMappingNode)Get(Get(Get(Document, "on"), "workflow_dispatch"), "inputs")!;
        var environment = new Dictionary<string, string> { ["EVENT_NAME"] = "workflow_dispatch", ["PUSH_BRANCH"] = "feature/test", ["PR_HEAD_BRANCH"] = "", ["WR_EVENT"] = "", ["WR_CONCLUSION"] = "", ["WR_HEAD_BRANCH"] = "", ["WR_HEAD_SHA"] = "" };
        foreach (var (key, value) in ((YamlMappingNode)Get(step, "env")!).Children)
        {
            var match = InputRegex().Match(value.ToString());
            if (match.Success) environment[key.ToString()] = Text(Get(defaults, match.Groups[1].Value), "default");
        }
        foreach (var (key, value) in inputs) environment[key] = value;
        var outputPath = Path.Combine(temporaryDirectory, Guid.NewGuid().ToString("N") + ".outputs");
        environment["GITHUB_OUTPUT"] = outputPath; File.WriteAllText(outputPath, "");
        var result = Run(Text(step, "run"), environment, temporaryDirectory);
        var lines = File.ReadAllLines(outputPath); File.Delete(outputPath);
        var values = new Dictionary<string, string>();
        for (var index = 0; index < lines.Length; index++)
        {
            var separator = lines[index].IndexOf("<<", StringComparison.Ordinal);
            if (separator >= 0)
            {
                var key = lines[index][..separator]; var delimiter = lines[index][(separator + 2)..]; var block = new List<string>();
                while (++index < lines.Length && lines[index] != delimiter) block.Add(lines[index]);
                values[key] = string.Join('\n', block);
            }
            else if ((separator = lines[index].IndexOf('=')) >= 0) values[lines[index][..separator]] = lines[index][(separator + 1)..];
        }
        return (result.Code, values, result.Output + result.Error);
    }
    [GeneratedRegex(@"\$\{\{\s*(.*?)\s*\}\}")]
    private static partial Regex ExpressionRegex();
    [GeneratedRegex(@"inputs\.([a-z_]+)")]
    private static partial Regex InputRegex();
}
