// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.NodeBuffer;

/// <summary>A node-record buffer whose allocation ranges can outlive individual operations.</summary>
/// <remarks>
/// Allocation pointers are ordered across reservations. Writes following a captured boundary have pointers at or above it;
/// retained records are immutable, and their pointers cannot be reset or reused while a retain is outstanding.
/// </remarks>
public interface IRetainableNodeRecordBuffer : INodeRecordBuffer
{
    /// <summary>Keeps the current allocation frontier and all subsequent writes readable until the retain is disposed.</summary>
    INodeRecordBufferRetain RetainActive();
}
