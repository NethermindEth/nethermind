// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Protects an allocation frontier and subsequent writes in a node-record buffer.</summary>
public interface INodeRecordBufferRetain : IDisposable
{
    /// <summary>The buffer whose records are protected.</summary>
    INodeRecordBuffer Buffer { get; }

    /// <summary>The inclusive allocation frontier captured by this retain.</summary>
    /// <remarks>Boundaries are ordered byte addresses within the same buffer; copies preserve the exact boundary.</remarks>
    ulong WriteBoundary { get; }

    /// <summary>Creates an independently disposed retain protecting the same range.</summary>
    INodeRecordBufferRetain Copy();
}
