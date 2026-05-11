// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;

namespace Nethermind.Trie;

public static class TrieNodeFactory
{
    public static TrieNode CreateBranch() => TrieNode.CreateBranchTyped();

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
