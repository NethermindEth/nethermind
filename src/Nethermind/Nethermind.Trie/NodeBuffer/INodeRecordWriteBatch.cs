// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie.NodeBuffer;

/// <summary>Contiguous, zero-filled bytes reserved in a node-record buffer, each record locatable by its pointer once written.</summary>
/// <remarks>
/// The writer fills <see cref="Bytes"/> with complete, record-aligned node records from its start. Disposal only ends the reservation;
/// a batch must not be used after it is disposed.
/// </remarks>
public interface INodeRecordWriteBatch : IDisposable
{
    /// <summary>The pointer of the first reserved byte; adding a byte offset in <see cref="Bytes"/> addresses the same byte.</summary>
    ulong BasePointer { get; }

    /// <summary>The reserved bytes.</summary>
    Span<byte> Bytes { get; }
}
