// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.State.Flat.History.Proofs;

namespace Nethermind.State.Flat.History.Walk;

internal enum NodeViewKind : byte
{
    Empty,
    Branch,
    Whole,
}

internal readonly struct NodeView
{
    public static readonly NodeView Empty = new(NodeViewKind.Empty, null, null, Keccak.EmptyTreeHash.ValueHash256, null);

    private NodeView(NodeViewKind kind, byte[]? rlp, byte[]?[]? children, in ValueHash256 hash, byte[]? reference)
    {
        Kind = kind;
        Rlp = rlp;
        Children = children;
        Hash = hash;
        Reference = reference;
    }

    public NodeViewKind Kind { get; }

    public byte[]? Rlp { get; }

    public byte[]?[]? Children { get; }

    public ValueHash256 Hash { get; }

    public byte[]? Reference { get; }

    public static NodeView Branch(byte[]?[] children) => Of(NodeViewKind.Branch, BranchRlp.Encode(children), children);

    public static NodeView Whole(byte[] rlp) => Of(NodeViewKind.Whole, rlp, null);

    private static NodeView Of(NodeViewKind kind, byte[] rlp, byte[]?[]? children)
    {
        ValueHash256 hash = Keccak.Compute(rlp);
        return new NodeView(kind, rlp, children, hash, rlp.Length < Hash256.Size ? rlp : hash.ToByteArray());
    }
}
