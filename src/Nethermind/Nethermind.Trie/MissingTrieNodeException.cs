// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie;

public class MissingTrieNodeException : TrieException
{
    public MissingTrieNodeException(string message, Exception inner, byte[] updatePath, int currentIndex) : base(message, inner)
    {
        UpdatePath = updatePath;
        CurrentIndex = currentIndex;
    }

    public byte[] UpdatePath { get; }
    public int CurrentIndex { get; }
    public ReadOnlySpan<byte> GetPathPart() => UpdatePath.AsSpan(0, CurrentIndex + 1);
}
