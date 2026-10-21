// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie.Native;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Stores encoded trie-node records and indexes trie roots by hash.</summary>
/// <remarks>
/// Pointers are buffer-local byte addresses; zero means absent. Records are written and read by native bulk sets through
/// <see cref="Native"/>; a writer holds a <see cref="PinHead"/> no newer than what it writes.
/// Allocation pointers are ordered across writes, so writes following a pin's boundary have pointers at or above it.
/// </remarks>
public interface INodeRecordBuffer : IDisposable
{
    /// <summary>Keeps the current allocation frontier and every later write readable until the pin is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The buffer is disposed.</exception>
    INodeRecordBufferPin PinHead();

    /// <summary>Finds the newest available indexed pointer for a trie root, or returns zero.</summary>
    /// <param name="address">The storage trie address hash, or null for the state trie.</param>
    /// <param name="root">The root hash.</param>
    ulong FindRoot(Hash256? address, in ValueHash256 root);

    /// <summary>The native buffer the records are held in, which native bulk sets write to and read from.</summary>
    NativeNodeBuffer Native { get; }
}
