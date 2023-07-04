// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Network.Dns;

/// <summary>
/// The root of the tree is a TXT record with the following content:
/// enrtree-root:v1 e=[enr-root] l=[link-root] seq=[sequence-number] sig=[signature]
/// </summary>
public class EnrTreeRoot : EnrTreeNode
{
    /// <summary>
    /// the root hashes of subtrees containing nodes and links subtrees
    /// </summary>
    public string EnrRoot { get; set; } = string.Empty;

    /// <summary>
    /// the root hashes of subtrees containing nodes and links subtrees
    /// </summary>
    public string LinkRoot { get; set; } = string.Empty;

    /// <summary>
    /// Updated each time the tree gets updated.
    /// </summary>
    public int Sequence { get; set; }

    /// <summary>
    /// Signature but need to learn where to take the public key from
    /// </summary>
    public string Signature { get; set; } = string.Empty;

    public override string ToString()
    {
        return $"enrtree-root:v1 e={EnrRoot} l={LinkRoot} seq={Sequence} sig={Signature}";
    }

    public override string[] Refs
    {
        get
        {
            return new[] { EnrRoot, LinkRoot };
        }
    }

    public override string[] Links => Array.Empty<string>();

    public override string[] Records => Array.Empty<string>();
}
