// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using NUnit.Framework;

namespace Nethermind.Tools.ProfilingMcp.Test;

public class WorkspacePathsTests
{
    [Test]
    public void Tells_how_to_copy_a_file_out_of_the_sidecar() =>
        Assert.That(WorkspacePaths.WithCopyCommand("/profmcp-home/snapshots/a.html", "profmcp-node-42", "docker --context node1"),
            Does.EndWith("docker --context node1 cp profmcp-node-42:/profmcp-home/snapshots/a.html ."));

    [TestCase("/profmcp-home/my report.html", ExpectedResult = "docker cp 'profmcp-node-42:/profmcp-home/my report.html' .")]
    [TestCase("/profmcp-home/a;rm -rf ~.html", ExpectedResult = "docker cp 'profmcp-node-42:/profmcp-home/a;rm -rf ~.html' .")]
    [TestCase("/profmcp-home/it's.html", ExpectedResult = null)]
    public string? Quotes_the_copy_command_or_leaves_it_out(string path)
    {
        string text = WorkspacePaths.WithCopyCommand(path, "profmcp-node-42", "docker");
        int command = text.IndexOf("with: ", StringComparison.Ordinal);
        return command < 0 ? null : text[(command + "with: ".Length)..];
    }

    [Test]
    public void Names_files_of_the_same_process_apart_within_a_second()
    {
        HashSet<string> names = [.. Enumerable.Range(0, 100).Select(static _ => WorkspacePaths.NewFileName("pid1", ".nettrace"))];

        Assert.That(names, Has.Count.EqualTo(100));
    }
}
