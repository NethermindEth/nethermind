// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Buffers;

namespace Nethermind.Trie;

public static class TrieNodeFactory
{
    public static TrieNode CreateBranch()
    {
        return new(new BranchData());
    }

    public static TrieNode CreateLeaf(ReadOnlySpan<byte> path, SpanSource value)
    {
        byte[] pathArray = HexPrefix.GetPathArray(path);
        return new(new LeafData(pathArray, value));
    }

    public static TrieNode CreateExtension(ReadOnlySpan<byte> path)
    {
        byte[] pathArray = HexPrefix.GetPathArray(path);
        return new(new ExtensionData(pathArray));
    }

    public static TrieNode CreateExtension(ReadOnlySpan<byte> path, TrieNode child)
    {
        byte[] pathArray = HexPrefix.GetPathArray(path);
        return new(new ExtensionData(pathArray, child));
    }
}
