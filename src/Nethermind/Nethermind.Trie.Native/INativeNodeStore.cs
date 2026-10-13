// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

/// <summary>
/// Path-keyed node storage used by <see cref="NativePatriciaBulkSet"/>.
/// Each node is stored as its RLP.
/// </summary>
/// <remarks>Called concurrently from native worker threads.</remarks>
public interface INativeNodeStore
{
    /// <summary>Copies the RLP of the node at <paramref name="path"/> whose hash is <paramref name="hash"/> into <paramref name="buffer"/>.</summary>
    /// <returns>
    /// The length of the node, or 0 when there is none.
    /// A length greater than <paramref name="buffer"/> means nothing was copied and the call is retried with a larger buffer.
    /// </returns>
    int Fetch(in TreePath path, in ValueHash256 hash, Span<byte> buffer);

    /// <summary>Stores <paramref name="rlp"/>, the node whose hash is <paramref name="hash"/>, at <paramref name="path"/>.</summary>
    void Write(in TreePath path, in ValueHash256 hash, ReadOnlySpan<byte> rlp);
}
