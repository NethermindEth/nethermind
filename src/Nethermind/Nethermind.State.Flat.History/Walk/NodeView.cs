// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
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
    public static readonly NodeView Empty = new(NodeViewKind.Empty, null, 0, null, Keccak.EmptyTreeHash.ValueHash256);

    private readonly byte[]? _rlp;
    private readonly int _length;

    private NodeView(NodeViewKind kind, byte[]? rlp, int length, ChildVector? children, in ValueHash256 hash)
    {
        Kind = kind;
        _rlp = rlp;
        _length = length;
        Children = children;
        Hash = hash;
    }

    public NodeViewKind Kind { get; }

    public ReadOnlySpan<byte> Rlp => _rlp is null ? ReadOnlySpan<byte>.Empty : _rlp.AsSpan(0, _length);

    public ChildVector? Children { get; }

    public ValueHash256 Hash { get; }

    public bool IsInline => _rlp is not null && _length < Hash256.Size;

    public void WriteReference(ChildVector vector, int index)
    {
        if (_rlp is null) vector.Clear(index);
        else if (_length < Hash256.Size) vector.Set(index, Rlp);
        else vector.SetHash(index, Hash);
    }

    public int ReferenceLength => _rlp is null ? 0 : _length < Hash256.Size ? _length : Hash256.Size;

    public void CopyReferenceTo(Span<byte> destination)
    {
        if (_rlp is null) return;

        if (_length < Hash256.Size) Rlp.CopyTo(destination);
        else Hash.Bytes.CopyTo(destination);
    }

    public static NodeView Branch(ChildVector children)
    {
        byte[] rlp = ArrayPool<byte>.Shared.Rent(BranchRlp.EncodedLength(children));
        int length = BranchRlp.Encode(children, rlp);
        return new NodeView(NodeViewKind.Branch, rlp, length, children, ValueKeccak.Compute(rlp.AsSpan(0, length)));
    }

    public static NodeView Branch(ChildVector children, ReadOnlySpan<byte> rlp, Hash256? knownHash)
    {
        byte[] copy = ArrayPool<byte>.Shared.Rent(rlp.Length);
        rlp.CopyTo(copy);
        return new NodeView(NodeViewKind.Branch, copy, rlp.Length, children, knownHash is null ? ValueKeccak.Compute(rlp) : knownHash.ValueHash256);
    }

    public static NodeView Whole(ReadOnlySpan<byte> rlp, Hash256? knownHash = null)
    {
        byte[] copy = ArrayPool<byte>.Shared.Rent(rlp.Length);
        rlp.CopyTo(copy);
        return new NodeView(NodeViewKind.Whole, copy, rlp.Length, null, knownHash is null ? ValueKeccak.Compute(rlp) : knownHash.ValueHash256);
    }

    public static NodeView Leaf(ReadOnlySpan<byte> nibbles, ReadOnlySpan<byte> value) => Short(nibbles, isLeaf: true, value, payloadIsString: true);

    public static NodeView Extension(ReadOnlySpan<byte> nibbles, ReadOnlySpan<byte> childReference) =>
        Short(nibbles, isLeaf: false, childReference, payloadIsString: childReference.Length == Hash256.Size);

    private static NodeView Short(ReadOnlySpan<byte> nibbles, bool isLeaf, ReadOnlySpan<byte> payload, bool payloadIsString)
    {
        Span<byte> hexPrefix = stackalloc byte[nibbles.Length / 2 + 1];
        WriteHexPrefix(hexPrefix, nibbles, isLeaf);
        int contentLength = Serialization.Rlp.Rlp.LengthOf(hexPrefix) + (payloadIsString ? Serialization.Rlp.Rlp.LengthOf(payload) : payload.Length);
        int total = Serialization.Rlp.Rlp.LengthOfSequence(contentLength);
        byte[] rlp = ArrayPool<byte>.Shared.Rent(total);
        int position = Serialization.Rlp.Rlp.StartSequence(rlp, 0, contentLength);
        position = Serialization.Rlp.Rlp.Encode(rlp, position, hexPrefix);
        if (payloadIsString) Serialization.Rlp.Rlp.Encode(rlp, position, payload);
        else payload.CopyTo(rlp.AsSpan(position));
        return new NodeView(NodeViewKind.Whole, rlp, total, null, ValueKeccak.Compute(rlp.AsSpan(0, total)));
    }

    private static void WriteHexPrefix(Span<byte> destination, ReadOnlySpan<byte> nibbles, bool isLeaf)
    {
        bool odd = (nibbles.Length & 1) == 1;
        destination[0] = (byte)((isLeaf ? 0x20 : 0) | (odd ? 0x10 | nibbles[0] : 0));
        int source = odd ? 1 : 0;
        for (int index = 1; index < destination.Length; index++, source += 2)
        {
            destination[index] = (byte)((nibbles[source] << 4) | nibbles[source + 1]);
        }
    }

    public void Release()
    {
        if (Children is not null) ChildVector.Return(Children);
        if (_rlp is not null) ArrayPool<byte>.Shared.Return(_rlp);
    }
}
