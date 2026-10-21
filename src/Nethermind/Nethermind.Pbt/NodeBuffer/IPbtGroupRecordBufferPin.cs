// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt.NodeBuffer;

/// <summary>Keeps a buffer's records from an allocation frontier on readable until disposed.</summary>
public interface IPbtGroupRecordBufferPin : IDisposable
{
    /// <summary>The buffer this pin belongs to.</summary>
    IPbtGroupRecordBuffer Buffer { get; }

    /// <summary>The inclusive allocation frontier captured when the pin was first acquired.</summary>
    /// <remarks>Every record reserved after the pin has a pointer greater than or equal to it.</remarks>
    ulong WriteBoundary { get; }

    /// <summary>Keeps the same records readable independently of this pin.</summary>
    IPbtGroupRecordBufferPin Copy();
}
