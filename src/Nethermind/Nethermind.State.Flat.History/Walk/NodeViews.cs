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
    private const int MaxNibbles = 2 * Hash256.Size;

    public static NodeView FromRoot(TrieNode? root, int depth, ITrieNodeResolver resolver)
    {
        if (root is null) return NodeView.Empty;

        ReadOnlySpan<byte> rootRlp = root.FullRlp.AsSpan();
        if (depth == 0) return root.IsBranch ? AsBranch(rootRlp, root.Keccak) : NodeView.Whole(rootRlp, root.Keccak);
        if (root.IsBranch) throw new InvalidOperationException("A partial trie holding one prefix cannot have a branch at its root.");

        byte[] key = root.Key!;
        if (key.Length < depth) throw new InvalidOperationException("A partial trie's root does not cover the prefix it was built for.");

        ReadOnlySpan<byte> rest = key.AsSpan(depth);
        if (root.IsLeaf) return NodeView.Leaf(rest, root.Value.AsSpan());

        TreePath path = TreePath.Empty;
        TrieNode child = root.GetChild(resolver, ref path, 0) ?? throw new InvalidOperationException("An extension node lost its child between commit and view.");
        ReadOnlySpan<byte> childRlp = child.FullRlp.AsSpan();
        if (rest.IsEmpty) return child.IsBranch ? AsBranch(childRlp, child.Keccak) : NodeView.Whole(childRlp, child.Keccak);

        Span<byte> reference = stackalloc byte[Hash256.Size];
        int referenceLength = BranchRlp.ReferenceOf(childRlp, reference);
        return NodeView.Extension(rest, reference[..referenceLength]);
    }

    public static NodeView FromRlp(ReadOnlySpan<byte> rlp)
    {
        ChildVector children = ChildVector.Rent();
        if (BranchRlp.TryReadChildren(rlp, children)) return NodeView.Branch(children, rlp, knownHash: null);

        ChildVector.Return(children);
        return NodeView.Whole(rlp);
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
            ChildVector references = ChildVector.Rent();
            for (int index = 0; index < BranchRlp.ChildCount; index++) children[index].WriteReference(references, index);
            return NodeView.Branch(references);
        }

        NodeView child = children[only];
        if (child.Kind == NodeViewKind.Branch)
        {
            Span<byte> reference = stackalloc byte[Hash256.Size];
            child.CopyReferenceTo(reference);
            return NodeView.Extension([(byte)only], reference[..child.ReferenceLength]);
        }

        Span<byte> merged = stackalloc byte[MaxNibbles + 1];
        merged[0] = (byte)only;
        bool isLeaf = DecodeShortNode(child.Rlp, merged[1..], out int nibbleCount, out ReadOnlySpan<byte> payload);
        ReadOnlySpan<byte> nibbles = merged[..(nibbleCount + 1)];
        return isLeaf ? NodeView.Leaf(nibbles, payload) : NodeView.Extension(nibbles, payload);
    }

    private static NodeView AsBranch(ReadOnlySpan<byte> rlp, Hash256? knownHash)
    {
        ChildVector children = ChildVector.Rent();
        BranchRlp.ReadChildren(rlp, children);
        return NodeView.Branch(children, rlp, knownHash);
    }

    private static bool DecodeShortNode(ReadOnlySpan<byte> rlp, Span<byte> nibbles, out int nibbleCount, out ReadOnlySpan<byte> payload)
    {
        RlpReader reader = new(rlp);
        reader.ReadSequenceLength();
        ReadOnlySpan<byte> hexPrefix = reader.DecodeByteArraySpan();
        bool isLeaf = (hexPrefix[0] & 0x20) != 0;
        nibbleCount = 0;
        if ((hexPrefix[0] & 0x10) != 0) nibbles[nibbleCount++] = (byte)(hexPrefix[0] & 0x0F);
        for (int index = 1; index < hexPrefix.Length; index++)
        {
            nibbles[nibbleCount++] = (byte)(hexPrefix[index] >> 4);
            nibbles[nibbleCount++] = (byte)(hexPrefix[index] & 0x0F);
        }

        if (isLeaf)
        {
            payload = reader.DecodeByteArraySpan();
            return true;
        }

        if (reader.IsSequenceNext())
        {
            (int prefixLength, int contentLength) = reader.PeekPrefixAndContentLength();
            payload = reader.Read(prefixLength + contentLength);
            return false;
        }

        payload = reader.DecodeByteArraySpan();
        return false;
    }
}
