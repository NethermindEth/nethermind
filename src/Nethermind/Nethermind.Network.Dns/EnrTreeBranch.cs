// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Dns;

/// <summary>
/// enrtree-branch:[h₁],[h₂],...,[h] is an intermediate tree entry containing hashes of subtree entries.
/// </summary>
public class EnrTreeBranch : EnrTreeNode
{
    public string[] Hashes { get; set; } = Array.Empty<string>();

    public override string ToString()
    {
        return $"enrtree-branch:{string.Join(',', Hashes)}";
    }

    public override string[] Links => Array.Empty<string>();

    public override string[] Refs => Hashes;

    public override string[] Records => Array.Empty<string>();
}
