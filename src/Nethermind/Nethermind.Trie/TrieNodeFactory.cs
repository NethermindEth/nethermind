// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Trie;

public static class TrieNodeFactory
{
    public static TrieNode CreateBranch() => TrieNode.CreateBranchTyped();

    internal static TrieNode CreateBranchWithChildHash(int childIndex, in ValueHash256 childHash)
    {
        const int contentLength = TrieNode.BranchesCount + Rlp.LengthOfKeccakRlp;
        byte[] rlp = new byte[Rlp.LengthOfSequence(contentLength)];
        Span<byte> destination = rlp;
        int position = Rlp.StartSequence(destination, 0, contentLength);
        for (int i = 0; i < TrieNode.BranchesCount; i++)
        {
            if (i == childIndex)
            {
                position = Rlp.Encode(destination, position, in childHash);
            }
            else
            {
                destination[position++] = 128;
            }
        }

        destination[position] = 128;

        return TrieNode.CreateBranchTyped(rlp, isDirty: true);
    }

    internal static TrieNode CreateExtensionWithChildHash(byte[] hexPrefixedPath, in ValueHash256 childHash)
    {
        int hexLength = HexPrefix.ByteLength(hexPrefixedPath);
        byte[] keyBytes = new byte[hexLength];
        HexPrefix.CopyToSpan(hexPrefixedPath, isLeaf: false, keyBytes);
        int contentLength = Rlp.LengthOf(keyBytes) + Rlp.LengthOfKeccakRlp;
        byte[] rlp = new byte[Rlp.LengthOfSequence(contentLength)];
        Span<byte> destination = rlp;
        int position = Rlp.StartSequence(destination, 0, contentLength);
        position = Rlp.Encode(destination, position, keyBytes);
        Rlp.Encode(destination, position, in childHash);

        TrieNode extension = TrieNode.CreateExtensionTyped(hexPrefixedPath);
        extension.WriteRlp(rlp);
        return extension;
    }

    public static TrieNode CreateLeaf(ReadOnlySpan<byte> path, CappedArray<byte> value)
    {
        byte[] pathArray = HexPrefix.GetArray(path);
        return TrieNode.CreateLeafTyped(pathArray, value);
    }

    public static TrieNode CreateExtension(ReadOnlySpan<byte> path, TrieNode child)
    {
        byte[] pathArray = HexPrefix.GetArray(path);
        return TrieNode.CreateExtensionTyped(pathArray, child);
    }

    public static TrieNode CreateExtension(byte[] pathArray, TrieNode child) =>
        TrieNode.CreateExtensionTyped(pathArray, child);
}
