// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie.Sparse;

/// <summary>
/// Generates multiproofs by walking the persistent trie for multiple target keys simultaneously.
/// A single walk produces all intermediate hashes needed for the sparse trie to compute the root.
/// </summary>
public static class MultiProofReader
{
    public static DecodedMultiProof ReadAccountProofs(
        ITrieNodeReader reader, Hash256 stateRoot, Hash256[] hashedAddresses)
    {
        DecodedMultiProof proof = new();
        if (stateRoot == Keccak.EmptyTreeHash || hashedAddresses.Length == 0)
            return proof;

        byte[][] targets = SortTargets(hashedAddresses);
        LoadRlpFunc loadRlp = new StateLoadRlp(reader);
        WalkTrie(loadRlp, stateRoot, targets, proof.AccountNodes);
        return proof;
    }

    public static DecodedMultiProof ReadStorageProofs(
        ITrieNodeReader reader, Hash256 accountPathHash, Hash256 storageRoot, Hash256[] hashedSlots)
    {
        DecodedMultiProof proof = new();
        if (storageRoot == Keccak.EmptyTreeHash || hashedSlots.Length == 0)
            return proof;

        List<ProofNode> storageNodes = [];
        byte[][] targets = SortTargets(hashedSlots);
        LoadRlpFunc loadRlp = new StorageLoadRlp(reader, accountPathHash);
        WalkTrie(loadRlp, storageRoot, targets, storageNodes);

        if (storageNodes.Count > 0)
            proof.StorageNodes[accountPathHash] = storageNodes;
        return proof;
    }

    private interface LoadRlpFunc
    {
        byte[] Load(in TreePath path, Hash256 hash, ReadFlags flags);
    }

    private readonly struct StateLoadRlp(ITrieNodeReader reader) : LoadRlpFunc
    {
        public byte[] Load(in TreePath path, Hash256 hash, ReadFlags flags) =>
            reader.LoadStateRlp(path, hash, flags);
    }

    private readonly struct StorageLoadRlp(ITrieNodeReader reader, Hash256 accountPathHash) : LoadRlpFunc
    {
        public byte[] Load(in TreePath path, Hash256 hash, ReadFlags flags) =>
            reader.LoadStorageRlp(accountPathHash, path, hash, flags);
    }

    private static void WalkTrie<T>(
        T loadRlp,
        Hash256 rootHash,
        byte[][] sortedTargetNibbles,
        List<ProofNode> output) where T : LoadRlpFunc
    {
        byte[] rootRlp = loadRlp.Load(TreePath.Empty, rootHash, ReadFlags.None);
        ProofNode rootProof = DecodeProofNode(rootRlp, TreePath.Empty);
        output.Add(rootProof);

        WalkNode(loadRlp, rootProof, TreePath.Empty, sortedTargetNibbles, 0, sortedTargetNibbles.Length, 0, output);
    }

    private static void WalkNode<T>(
        T loadRlp,
        ProofNode node,
        TreePath currentPath,
        byte[][] targets,
        int targetStart,
        int targetEnd,
        int nibbleDepth,
        List<ProofNode> output) where T : LoadRlpFunc
    {
        if (targetStart >= targetEnd) return;

        switch (node.Kind)
        {
            case ProofNodeKind.Leaf:
                return;

            case ProofNodeKind.Extension:
                {
                    byte[] extKey = node.Key ?? [];
                    int newDepth = nibbleDepth + extKey.Length;

                    // Find targets that match the extension key
                    int matchStart = targetEnd; // no match by default
                    int matchEnd = targetStart;
                    for (int i = targetStart; i < targetEnd; i++)
                    {
                        if (MatchesPrefix(targets[i], nibbleDepth, extKey))
                        {
                            if (i < matchStart) matchStart = i;
                            matchEnd = i + 1;
                        }
                    }
                    if (matchStart >= matchEnd) return;

                    if (node.ChildRlps is { Length: > 0 } && !node.ChildRlps[0].IsNull)
                    {
                        RlpNode childRef = node.ChildRlps[0];
                        TreePath childPath = currentPath.Append(extKey);

                        if (childRef.IsHash())
                        {
                            Hash256 childHash = childRef.AsHash();
                            byte[] childRlp = loadRlp.Load(childPath, childHash, ReadFlags.None);
                            ProofNode childProof = DecodeProofNode(childRlp, childPath);
                            output.Add(childProof);
                            WalkNode(loadRlp, childProof, childPath, targets, matchStart, matchEnd, newDepth, output);
                        }
                        else
                        {
                            ProofNode childProof = DecodeProofNode(childRef.AsSpan().ToArray(), childPath);
                            output.Add(childProof);
                            WalkNode(loadRlp, childProof, childPath, targets, matchStart, matchEnd, newDepth, output);
                        }
                    }
                    break;
                }

            case ProofNodeKind.Branch:
                {
                    for (int nibble = 0; nibble < 16; nibble++)
                    {
                        int subStart = -1;
                        int subEnd = -1;
                        for (int i = targetStart; i < targetEnd; i++)
                        {
                            if (nibbleDepth < targets[i].Length && targets[i][nibbleDepth] == nibble)
                            {
                                if (subStart == -1) subStart = i;
                                subEnd = i + 1;
                            }
                        }
                        if (subStart == -1) continue;

                        if (!node.ChildMask.IsBitSet(nibble))
                            continue; // empty slot — absence proof

                        RlpNode childRef = node.ChildRlps is not null && nibble < node.ChildRlps.Length
                            ? node.ChildRlps[nibble]
                            : default;

                        if (childRef.IsNull) continue;

                        TreePath childPath = currentPath.Append(nibble);

                        if (childRef.IsHash())
                        {
                            Hash256 childHash = childRef.AsHash();
                            byte[] childRlp = loadRlp.Load(childPath, childHash, ReadFlags.None);
                            ProofNode childProof = DecodeProofNode(childRlp, childPath);
                            output.Add(childProof);
                            WalkNode(loadRlp, childProof, childPath, targets, subStart, subEnd, nibbleDepth + 1, output);
                        }
                        else
                        {
                            ProofNode childProof = DecodeProofNode(childRef.AsSpan().ToArray(), childPath);
                            output.Add(childProof);
                            WalkNode(loadRlp, childProof, childPath, targets, subStart, subEnd, nibbleDepth + 1, output);
                        }
                    }
                    break;
                }
        }
    }

    internal static ProofNode DecodeProofNode(byte[] rlp, TreePath path)
    {
        Rlp.ValueDecoderContext ctx = new(rlp);
        ctx.ReadSequenceLength();

        int startPos = ctx.Position;
        int itemCount = ctx.PeekNumberOfItemsRemaining(null, 18);
        ctx.Position = startPos;

        if (itemCount >= 17)
            return DecodeBranchProof(rlp, ref ctx, path);
        if (itemCount == 2)
            return DecodeTwoItemProof(rlp, ref ctx, path);

        return new ProofNode { Path = path, Kind = ProofNodeKind.Empty, RawRlp = rlp };
    }

    private static ProofNode DecodeBranchProof(byte[] rlp, ref Rlp.ValueDecoderContext ctx, TreePath path)
    {
        TrieMask childMask = TrieMask.Empty;
        RlpNode[] childRlps = new RlpNode[16];

        for (int i = 0; i < 16; i++)
        {
            int itemStart = ctx.Position;
            int itemLen = ctx.PeekNextRlpLength();

            if (itemLen == 1 && rlp[itemStart] == 0x80)
            {
                ctx.SkipItem();
                continue;
            }

            childMask = childMask.SetBit(i);

            if (itemLen == 33 && rlp[itemStart] == 0xa0)
            {
                childRlps[i] = RlpNode.FromHash(new Hash256(rlp.AsSpan(itemStart + 1, 32)));
            }
            else
            {
                byte[] childBytes = new byte[itemLen];
                rlp.AsSpan(itemStart, itemLen).CopyTo(childBytes);
                childRlps[i] = RlpNode.FromRlp(childBytes);
            }
            ctx.Position = itemStart + itemLen;
        }
        ctx.SkipItem(); // 17th element (branch value, always 0x80)

        return new ProofNode
        {
            Path = path,
            Kind = ProofNodeKind.Branch,
            ChildMask = childMask,
            ChildRlps = childRlps,
            RawRlp = rlp,
        };
    }

    private static ProofNode DecodeTwoItemProof(byte[] rlp, ref Rlp.ValueDecoderContext ctx, TreePath path)
    {
        ReadOnlySpan<byte> encodedKeySpan = ctx.DecodeByteArraySpan();
        byte[] encodedKey = encodedKeySpan.ToArray();
        (byte[] nibbleKey, bool isLeaf) = HexPrefix.FromBytes(encodedKey);

        if (isLeaf)
        {
            ReadOnlySpan<byte> valueSpan = ctx.DecodeByteArraySpan();
            byte[] value = valueSpan.ToArray();
            return new ProofNode
            {
                Path = path,
                Kind = ProofNodeKind.Leaf,
                Key = nibbleKey,
                Value = value,
                RawRlp = rlp,
            };
        }

        // Extension
        int childStart = ctx.Position;
        int childLen = ctx.PeekNextRlpLength();

        RlpNode childRlp;
        if (childLen == 33 && rlp[childStart] == 0xa0)
        {
            childRlp = RlpNode.FromHash(new Hash256(rlp.AsSpan(childStart + 1, 32)));
        }
        else
        {
            byte[] childBytes = new byte[childLen];
            rlp.AsSpan(childStart, childLen).CopyTo(childBytes);
            childRlp = RlpNode.FromRlp(childBytes);
        }

        return new ProofNode
        {
            Path = path,
            Kind = ProofNodeKind.Extension,
            Key = nibbleKey,
            ChildRlps = [childRlp],
            ChildNibble = -1,
            RawRlp = rlp,
        };
    }

    private static byte[][] SortTargets(Hash256[] keys)
    {
        byte[][] nibbles = new byte[keys.Length][];
        for (int i = 0; i < keys.Length; i++)
            nibbles[i] = Nibbles.BytesToNibbleBytes(keys[i].Bytes);
        Array.Sort(nibbles, CompareNibbleArrays);
        return nibbles;
    }

    private static int CompareNibbleArrays(byte[] a, byte[] b)
    {
        int minLen = Math.Min(a.Length, b.Length);
        for (int i = 0; i < minLen; i++)
        {
            int cmp = a[i].CompareTo(b[i]);
            if (cmp != 0) return cmp;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static bool MatchesPrefix(byte[] target, int offset, byte[] prefix)
    {
        if (offset + prefix.Length > target.Length) return false;
        for (int i = 0; i < prefix.Length; i++)
        {
            if (target[offset + i] != prefix[i]) return false;
        }
        return true;
    }
}
