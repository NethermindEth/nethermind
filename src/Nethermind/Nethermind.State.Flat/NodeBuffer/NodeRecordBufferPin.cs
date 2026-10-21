// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Trie.Native;

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Keeps a generation of a <see cref="NodeRecordBuffer"/>, and every later one, readable in place until disposed.</summary>
/// <remarks>Obtained from <see cref="NodeRecordBuffer.PinHead"/> or <see cref="Copy"/>, which have already pinned the generation.</remarks>
public sealed class NodeRecordBufferPin(INodeRecordBuffer buffer, NativeNodeBufferPin native) : INodeRecordBufferPin
{
    /// <summary>The inclusive allocation frontier captured when this pin was taken.</summary>
    public ulong WriteBoundary => native.WriteBoundary;

    /// <summary>The buffer whose generation is pinned.</summary>
    public INodeRecordBuffer Buffer => buffer;

    /// <summary>A further, independently disposed pin of the same generation.</summary>
    public INodeRecordBufferPin Copy() => new NodeRecordBufferPin(buffer, native.Copy());

    /// <summary>Releases the generation; later calls do nothing.</summary>
    public void Dispose() => native.Dispose();
}
