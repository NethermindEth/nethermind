// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie.Native;

/// <summary>
/// Path-keyed node storage used by <see cref="NativePatriciaBulkSet"/>.
/// Each node is stored as <c>&lt;tag&gt;[&lt;bitset&gt;]&lt;rlp&gt;</c>.
/// </summary>
/// <remarks>Called concurrently from native worker threads.</remarks>
public interface INativeNodeStore
{
    /// <returns>
    /// The length of the node at <paramref name="path"/>, or 0 when there is none.
    /// A length greater than <paramref name="buffer"/> means nothing was copied and the call is retried with a larger buffer.
    /// </returns>
    int Fetch(in TreePath path, Span<byte> buffer);

    /// <summary>Stores <paramref name="data"/> at <paramref name="path"/>; empty <paramref name="data"/> removes the node.</summary>
    void Write(in TreePath path, ReadOnlySpan<byte> data);
}
