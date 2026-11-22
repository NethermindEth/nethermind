// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

#:include script-process.helpers.cs
#:include ../rpc-bench/workflow-test.helpers.cs
#:include ../rpc-bench/rpc-script-tests.helpers.cs
using YamlDotNet.RepresentationModel;

static class CiWorkflow
{
    internal static (int Code, string Output, string Error) Run(string script, Dictionary<string, string> environment, string directory)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, script);
            return ScriptProcess.Run("env", [.. environment.Select(pair => $"{pair.Key}={pair.Value}"),
                "bash", "--noprofile", "--norc", "-eo", "pipefail", file], timeout.Token, workingDirectory: directory).GetAwaiter().GetResult();
        }
        finally { File.Delete(file); }
    }

    internal static string[] Bodies(WorkflowTest workflow, string name) => ((YamlMappingNode)WorkflowTest.Get(workflow.Document, "jobs")!).Children.Values
        .Select(job => WorkflowTest.Get(job, "steps")).OfType<YamlSequenceNode>().SelectMany(steps => steps.Children)
        .Where(step => WorkflowTest.Text(step, "name") == name).Select(step => WorkflowTest.Text(step, "run")).ToArray();

    internal static Dictionary<string, string> Outputs(string path)
    {
        var result = new Dictionary<string, string>();
        var lines = File.ReadAllLines(path);
        for (var index = 0; index < lines.Length; index++)
        {
            var separator = lines[index].IndexOf("<<", StringComparison.Ordinal);
            if (separator >= 0)
            {
                var key = lines[index][..separator];
                var delimiter = lines[index][(separator + 2)..];
                var block = new List<string>();
                while (++index < lines.Length && lines[index] != delimiter) block.Add(lines[index]);
                result[key] = string.Join('\n', block);
            }
            else if ((separator = lines[index].IndexOf('=')) >= 0) result[lines[index][..separator]] = lines[index][(separator + 1)..];
        }
        return result;
    }

    internal static void Executable(string path, string body)
    {
        File.WriteAllText(path, "#!/bin/bash\n" + body + "\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
