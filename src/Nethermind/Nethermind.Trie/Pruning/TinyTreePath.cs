// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Runtime.CompilerServices;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie;

/// <summary>
/// Like TreePath, but tiny. Fit in 8 byte, like a long. Can only represent 14 nibble.
/// </summary>
[InlineArray(8)]
public struct TinyTreePath
{
    public const int MaxNibbleLength = 14;

    byte _byte;

    public TinyTreePath(in TreePath path)
    {
        if (path.Length > MaxNibbleLength) throw new InvalidOperationException("Unable to represent more than 14 nibble");
        Span<byte> pathSpan = path.Path.BytesAsSpan;
        for (int i = 0; i < 7; i++)
        {
            this[i] = pathSpan[i];
        }

        this[7] = (byte)path.Length;
    }

    public readonly int Length => this[7];

    public readonly TreePath ToTreePath()
    {
        ValueHash256 rawPath = Keccak.Zero;
        Span<byte> pathSpan = rawPath.BytesAsSpan;
        for (int i = 0; i < 7; i++)
        {
            pathSpan[i] = this[i];
        }

        return new TreePath(rawPath, Length);
    }
}
