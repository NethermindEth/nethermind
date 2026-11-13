// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie;

/// <summary>A trie node resolver told of each node resolved from the RLP it holds, inlined in its parent, rather than loaded.</summary>
internal interface IEmbeddedNodeObserver
{
    /// <summary>Called before the node at <paramref name="path"/> is decoded from <paramref name="rlp"/>.</summary>
    void OnEmbeddedNodeResolving(in TreePath path, ReadOnlySpan<byte> rlp);
}
