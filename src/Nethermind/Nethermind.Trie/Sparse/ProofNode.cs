// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Sparse;

public enum ProofNodeKind : byte
{
    Empty = 0,
    Branch = 1,
    Extension = 2,
    Leaf = 3,
}

/// <summary>
/// A decoded trie node from a Merkle proof, ready to be revealed into the sparse trie.
/// </summary>
public sealed class ProofNode
{
    public TreePath Path { get; init; }
    public ProofNodeKind Kind { get; init; }

    /// <summary>Leaf: remaining key nibbles. Extension: extension key nibbles.</summary>
    public byte[]? Key { get; init; }

    /// <summary>Leaf: the leaf value.</summary>
    public byte[]? Value { get; init; }

    /// <summary>Branch: which nibbles have children.</summary>
    public TrieMask ChildMask { get; init; }

    /// <summary>Branch: per-nibble child RLP references (hash or inline). Indexed 0..15.</summary>
    public RlpNode[]? ChildRlps { get; init; }

    /// <summary>Extension: the nibble index of the single child.</summary>
    public int ChildNibble { get; init; } = -1;

    /// <summary>Original RLP encoding of this node (for Cached state).</summary>
    public byte[]? RawRlp { get; init; }

    public override string ToString() => Kind switch
    {
        ProofNodeKind.Leaf => $"ProofLeaf(path={Path}, key={Key?.Length ?? 0}nib)",
        ProofNodeKind.Branch => $"ProofBranch(path={Path}, children={ChildMask.CountBits()})",
        ProofNodeKind.Extension => $"ProofExt(path={Path}, key={Key?.Length ?? 0}nib)",
        _ => $"ProofEmpty(path={Path})"
    };
}
