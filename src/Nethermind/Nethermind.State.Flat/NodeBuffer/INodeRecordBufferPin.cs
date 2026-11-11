// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.NodeBuffer;

/// <summary>Keeps an allocation frontier and every later write of a node-record buffer readable until disposed.</summary>
public interface INodeRecordBufferPin : IDisposable
{
    /// <summary>The buffer whose records are pinned.</summary>
    INodeRecordBuffer Buffer { get; }

    /// <summary>The inclusive allocation frontier captured by this pin.</summary>
    /// <remarks>Boundaries are ordered byte addresses within the same buffer; copies preserve the exact boundary.</remarks>
    ulong WriteBoundary { get; }

    /// <summary>Creates an independently disposed pin of the same range.</summary>
    INodeRecordBufferPin Copy();
}
