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
    public static readonly NodeView Empty = new(NodeViewKind.Empty, null, null, Keccak.EmptyTreeHash.ValueHash256);

    private NodeView(NodeViewKind kind, byte[]? rlp, ChildVector? children, in ValueHash256 hash)
    {
        Kind = kind;
        Rlp = rlp;
        Children = children;
        Hash = hash;
    }

    public NodeViewKind Kind { get; }

    public byte[]? Rlp { get; }

    public ChildVector? Children { get; }

    public ValueHash256 Hash { get; }

    public bool IsInline => Rlp is not null && Rlp.Length < Hash256.Size;

    public void WriteReference(ChildVector vector, int index)
    {
        if (Rlp is null) vector.Clear(index);
        else if (Rlp.Length < Hash256.Size) vector.Set(index, Rlp);
        else vector.SetHash(index, Hash);
    }

    public int ReferenceLength => Rlp is null ? 0 : Rlp.Length < Hash256.Size ? Rlp.Length : Hash256.Size;

    public void CopyReferenceTo(Span<byte> destination)
    {
        if (Rlp is null) return;

        if (Rlp.Length < Hash256.Size) Rlp.CopyTo(destination);
        else Hash.Bytes.CopyTo(destination);
    }

    public static NodeView Branch(ChildVector children)
    {
        byte[] rlp = BranchRlp.Encode(children);
        return new NodeView(NodeViewKind.Branch, rlp, children, Keccak.Compute(rlp));
    }

    public static NodeView Whole(byte[] rlp) => new(NodeViewKind.Whole, rlp, null, Keccak.Compute(rlp));

    public void Release()
    {
        if (Children is not null) ChildVector.Return(Children);
    }
}
