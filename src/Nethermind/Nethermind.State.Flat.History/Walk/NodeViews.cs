// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Flat.History.Proofs;
using Nethermind.Trie;
using Nethermind.Trie.Pruning;

namespace Nethermind.State.Flat.History.Walk;

internal static class NodeViews
{
    public static NodeView FromRoot(TrieNode? root, int depth, ITrieNodeResolver resolver)
    {
        if (root is null) return NodeView.Empty;

        byte[] rootRlp = root.FullRlp.ToArray()!;
        if (depth == 0) return root.IsBranch ? AsBranch(rootRlp) : NodeView.Whole(rootRlp);
        if (root.IsBranch) throw new InvalidOperationException("A partial trie holding one prefix cannot have a branch at its root.");

        byte[] key = root.Key!;
        if (key.Length < depth) throw new InvalidOperationException("A partial trie's root does not cover the prefix it was built for.");

        ReadOnlySpan<byte> rest = key.AsSpan(depth);
        if (root.IsLeaf) return NodeView.Whole(LeafRlp(rest, root.Value.AsSpan()));

        TreePath path = TreePath.Empty;
        TrieNode child = root.GetChild(resolver, ref path, 0) ?? throw new InvalidOperationException("An extension node lost its child between commit and view.");
        byte[] childRlp = child.FullRlp.ToArray()!;
        if (rest.IsEmpty) return child.IsBranch ? AsBranch(childRlp) : NodeView.Whole(childRlp);

        return NodeView.Whole(ExtensionRlp(rest, BranchRlp.ReferenceOf(childRlp)));
    }

    public static NodeView Combine(ReadOnlySpan<NodeView> children)
    {
        int present = 0;
        int only = -1;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (children[index].Kind == NodeViewKind.Empty) continue;

            present++;
            only = index;
        }

        if (present == 0) return NodeView.Empty;

        if (present >= 2)
        {
            byte[]?[] references = new byte[]?[BranchRlp.ChildCount];
            for (int index = 0; index < BranchRlp.ChildCount; index++) references[index] = children[index].Reference;
            return NodeView.Branch(references);
        }

        NodeView child = children[only];
        if (child.Kind == NodeViewKind.Branch) return NodeView.Whole(ExtensionRlp([(byte)only], child.Reference!));

        (byte[] nibbles, bool isLeaf, byte[] payload) = DecodeShortNode(child.Rlp!);
        byte[] merged = new byte[nibbles.Length + 1];
        merged[0] = (byte)only;
        nibbles.CopyTo(merged, 1);
        return NodeView.Whole(isLeaf ? LeafRlp(merged, payload) : ExtensionRlp(merged, payload));
    }

    public static ushort ChangedChildren(byte[]?[]? previous, byte[]?[] current)
    {
        ushort changed = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            byte[]? before = previous?[index];
            byte[]? after = current[index];
            bool same = before is null ? after is null : after is not null && before.AsSpan().SequenceEqual(after);
            if (!same) changed |= (ushort)(1 << index);
        }

        return changed;
    }

    public static ushort PresenceOf(byte[]?[] children)
    {
        ushort presence = 0;
        for (int index = 0; index < BranchRlp.ChildCount; index++)
        {
            if (children[index] is not null) presence |= (ushort)(1 << index);
        }

        return presence;
    }

    public static byte[] ExtensionRlp(ReadOnlySpan<byte> nibbles, byte[] childReference)
    {
        byte[] hexPrefix = HexPrefix.ToBytes(nibbles.ToArray(), isLeaf: false);
        int referenceLength = childReference.Length == Hash256.Size ? 1 + Hash256.Size : childReference.Length;
        int contentLength = Rlp.LengthOf(hexPrefix) + referenceLength;
        byte[] rlp = new byte[Rlp.LengthOfSequence(contentLength)];
        int position = Rlp.StartSequence(rlp, 0, contentLength);
        position = Rlp.Encode(rlp, position, hexPrefix);
        if (childReference.Length == Hash256.Size)
        {
            Rlp.Encode(rlp, position, childReference);
        }
        else
        {
            childReference.CopyTo(rlp.AsSpan(position));
        }

        return rlp;
    }

    public static byte[] LeafRlp(ReadOnlySpan<byte> nibbles, ReadOnlySpan<byte> value)
    {
        byte[] hexPrefix = HexPrefix.ToBytes(nibbles.ToArray(), isLeaf: true);
        int contentLength = Rlp.LengthOf(hexPrefix) + Rlp.LengthOf(value);
        byte[] rlp = new byte[Rlp.LengthOfSequence(contentLength)];
        int position = Rlp.StartSequence(rlp, 0, contentLength);
        position = Rlp.Encode(rlp, position, hexPrefix);
        Rlp.Encode(rlp, position, value);
        return rlp;
    }

    private static NodeView AsBranch(byte[] rlp)
    {
        byte[]?[] children = new byte[]?[BranchRlp.ChildCount];
        BranchRlp.ReadChildren(rlp, children);
        return NodeView.Branch(children);
    }

    private static (byte[] Nibbles, bool IsLeaf, byte[] Payload) DecodeShortNode(byte[] rlp)
    {
        RlpReader reader = new(rlp);
        reader.ReadSequenceLength();
        (byte[] nibbles, bool isLeaf) = HexPrefix.FromBytes(reader.DecodeByteArraySpan());
        if (isLeaf) return (nibbles, true, reader.DecodeByteArraySpan().ToArray());

        if (reader.IsSequenceNext())
        {
            (int prefixLength, int contentLength) = reader.PeekPrefixAndContentLength();
            return (nibbles, false, reader.Read(prefixLength + contentLength).ToArray());
        }

        return (nibbles, false, reader.DecodeByteArraySpan().ToArray());
    }
}
