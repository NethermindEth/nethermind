// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>Keeps an allocation frontier and every later write of a group record buffer readable until disposed.</summary>
public interface IPbtGroupRecordBufferPin : IDisposable
{
    /// <summary>The buffer whose records are pinned.</summary>
    IPbtGroupRecordBuffer Buffer { get; }

    /// <summary>The inclusive allocation frontier captured by this pin.</summary>
    /// <remarks>Boundaries are ordered byte addresses within the same buffer; copies preserve the exact boundary.</remarks>
    ulong WriteBoundary { get; }

    /// <summary>Creates an independently disposed pin of the same range.</summary>
    IPbtGroupRecordBufferPin Copy();
}
