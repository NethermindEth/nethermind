// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Native;

/// <summary>
/// Path-keyed node storage <see cref="NativePatriciaBulkSet"/> reads from.
/// Each node is stored as its RLP.
/// </summary>
/// <remarks>Called concurrently from native worker threads.</remarks>
public interface INativeNodeStore
{
    /// <summary>Passes the RLP of the node at <paramref name="path"/> whose hash is <paramref name="hash"/> to <paramref name="receiver"/>.</summary>
    /// <returns>Whether the node was found and passed to <paramref name="receiver"/>.</returns>
    bool Fetch(in TreePath path, in ValueHash256 hash, NativeNodeReceiver receiver);
}

/// <summary>Hands the RLP of a fetched node to the native bulk set, which decodes it before <see cref="Receive"/> returns.</summary>
public readonly unsafe ref struct NativeNodeReceiver(delegate* unmanaged<void*, byte*, int, void> receive, void* context)
{
    /// <remarks>Call at most once. <paramref name="rlp"/> only needs to stay valid for the duration of the call.</remarks>
    public void Receive(ReadOnlySpan<byte> rlp)
    {
        fixed (byte* rlpPointer = rlp) receive(context, rlpPointer, rlp.Length);
    }
}
