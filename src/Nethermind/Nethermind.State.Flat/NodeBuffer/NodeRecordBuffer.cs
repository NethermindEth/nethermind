// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Trie.Native;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>A trie node buffer held natively, see <see cref="NativeNodeBuffer"/>.</summary>
public sealed class NodeRecordBuffer(NativeNodeBuffer native) : INodeRecordBuffer
{
    /// <summary>The live generations of the buffers that do not take them from configuration.</summary>
    public const int DefaultLiveGenerations = NativeNodeBuffer.DefaultLiveGenerations;

    // Pointers are the generation shifted left by this, or-ed with the byte offset in it.
    internal const int OffsetBits = 32;

    /// <inheritdoc/>
    public NativeNodeBuffer Native => native;

    /// <summary>Keeps the active generation, and every later one, readable in place until the returned pin is disposed.</summary>
    public INodeRecordBufferPin PinHead() => new NodeRecordBufferPin(this, native.PinActiveGeneration());

    /// <inheritdoc/>
    public ulong FindRoot(Hash256? address, in ValueHash256 root) => native.ResolveRoot(address, root);

    public void Dispose() => native.Dispose();
}
