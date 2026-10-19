// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Protects borrowed node records for a stack-scoped operation.</summary>
public readonly ref struct NodeRecordBufferReadLease
{
    private readonly INodeRecordBuffer _buffer;

    internal NodeRecordBufferReadLease(INodeRecordBuffer buffer) => _buffer = buffer;

    /// <summary>Releases the read operation; each lease must be disposed exactly once.</summary>
    public void Dispose() => _buffer.ExitRead();
}
