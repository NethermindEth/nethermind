// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Dns;

/// <summary>
/// enr:[node-record] is a leaf containing a node record. The node record is encoded as a URL-safe base64 string.
/// Note that this type of entry matches the canonical ENR text encoding. It may only appear in the enr-root subtree.
/// </summary>
public class EnrLinkedTree : EnrTreeNode
{
    public string Link { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"enrtree://{Link}";
    }

    public override string[] Links => new[] { Link };

    public override string[] Refs => Array.Empty<string>();

    public override string[] Records => Array.Empty<string>();
}
