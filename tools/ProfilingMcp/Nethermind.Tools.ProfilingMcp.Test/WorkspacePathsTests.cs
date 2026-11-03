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
}
