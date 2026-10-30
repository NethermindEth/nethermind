// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt.NodeBuffer;

namespace Nethermind.State.Pbt.NodeBuffer;

/// <summary>Stores PBT group records and indexes tree roots by hash.</summary>
/// <remarks>
/// Pointers are buffer-local byte addresses; zero means absent. Implementations must not reuse a pointer for a different record
/// while a caller can still hold it. Records and reserved spans are borrowed: a writer holds a <see cref="PinHead"/> no newer than
/// what it writes, and a reader holds a <see cref="PinAll"/> taken before it looks up the records it reads.
/// Allocation pointers are ordered across reservations, so writes following a pin's boundary have pointers at or above it.
/// </remarks>
public interface IPbtGroupRecordBuffer : IDisposable
{
    /// <summary>Keeps the current allocation frontier and every later write readable until the pin is disposed.</summary>
    /// <exception cref="ObjectDisposedException">The buffer is disposed.</exception>
    IPbtGroupRecordBufferPin PinHead();

    /// <summary>Keeps every record borrowed while the returned pin lives valid until it is disposed.</summary>
    /// <remarks>
    /// A record may stop resolving while the pin lives, so readers fall back on a miss; records already borrowed stay valid.
    /// This lets overlapping readers not hold old records forever.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">The buffer is disposed.</exception>
    IDisposable PinAll();

    /// <summary>Finds a borrowed record, or misses when its pointer is absent or no longer available.</summary>
    bool TryLocate(ulong pointer, out PbtGroupRecord record);

    /// <summary>Reserves contiguous, zero-filled bytes for group records, registered with the buffer when the batch is disposed.</summary>
    /// <remarks>The writer holds a pin no newer than the batch until all written records and their roots are published.</remarks>
    /// <param name="alignedTotalLength">The total length of the records, a multiple of <see cref="PbtGroupRecordCodec.RecordAlignment"/>.</param>
    IPbtGroupRecordWriteBatch StartWriteBatch(int alignedTotalLength);

    /// <summary>Finds the newest available indexed pointer for a tree root, or returns zero.</summary>
    /// <param name="root">The root hash.</param>
    ulong FindRoot(in ValueHash256 root);

    /// <summary>Indexes a completed group as the root of a tree.</summary>
    /// <param name="root">The root hash.</param>
    /// <param name="rootPointer">The pointer of the completed root record.</param>
    void SetRoot(in ValueHash256 root, ulong rootPointer);
}
