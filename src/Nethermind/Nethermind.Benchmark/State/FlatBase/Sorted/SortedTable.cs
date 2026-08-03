// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// Verbatim copy of Nethermind.State.Flat/PersistedSnapshots/Sorted/SortedTable.cs (internal in
// Nethermind.State.Flat, which has no InternalsVisibleTo for benchmarks). Benchmark-scoped
// prototype code — keep in sync with the original; do not diverge.

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Io;

namespace Nethermind.Benchmarks.State.FlatBase.Sorted;

/// <summary>
/// Shared wire-format constants and footer helper for the two-level sorted table that backs a
/// persisted snapshot's metadata blob. It is an ascending byte-sorted map of fully-materialized keys
/// to small inline values, laid out as a run of 4 KiB-aligned <see cref="Block">data blocks</see>
/// located by table-relative byte offset, followed by a single index block (separator → byte offset)
/// and a footer.
/// </summary>
internal static class SortedTable
{
    /// <summary>Data-block size and alignment — every data block but the last is zero-padded to this 4 KiB
    /// page, so block <c>i</c> sits at <c>i · BlockSize</c> and the index records its byte offset directly.</summary>
    internal const int BlockSize = PageLayout.PageSize;

    /// <summary>Default front-coding restart interval (records per restart run).</summary>
    internal const int DefaultRestartInterval = 8;

    /// <summary>Fixed footer: index-block byte offset (i64), version (u8).</summary>
    internal const int FooterSize = sizeof(long) + 1;

    /// <summary>On-disk footer layout: index-block byte offset then version. Read by reinterpreting the
    /// trailing bytes (the <c>i64</c> field is little-endian on disk, matching the host on supported
    /// targets). <see cref="FooterSize"/> equals <c>Unsafe.SizeOf&lt;FooterBytes&gt;()</c>.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct FooterBytes
    {
        internal readonly long IndexOffset;
        internal readonly byte Version;
    }

    internal const byte FormatVersion = 1;

    /// <summary>Footer-resolved table geometry: the table-relative byte offset of the (unaligned) index
    /// block. The index values need no stored restart interval — a restart is any <c>cp == 0</c> record
    /// (see <see cref="Block"/>).</summary>
    internal readonly record struct Footer(long IndexOffset);

    /// <summary>Reader-absolute start of the index block.</summary>
    internal static long IndexBlockStart(Bound table, in Footer footer) => table.Offset + footer.IndexOffset;

    /// <summary>Reader-absolute start of the data block at table-relative <paramref name="byteOffset"/>.</summary>
    internal static long DataBlockStart(Bound table, long byteOffset) => table.Offset + byteOffset;

    /// <summary>Read the footer of the table occupying <paramref name="table"/> and resolve the
    /// index-block offset.</summary>
    /// <returns><c>false</c> when the bound is too small, unreadable, or carries an unknown version.</returns>
    internal static bool TryReadFooter<TReader, TPin>(scoped in TReader reader, Bound table, out Footer footer)
        where TPin : struct, IBufferPin, allows ref struct
        where TReader : IByteReader<TPin>, allows ref struct
    {
        footer = default;
        if (table.Length < FooterSize) return false;

        FooterBytes bytes = default;
        if (!reader.TryRead(table.Offset + table.Length - FooterSize, MemoryMarshal.AsBytes(new Span<FooterBytes>(ref bytes)))) return false;
        if (bytes.Version != FormatVersion) return false;

        long indexOffset = bytes.IndexOffset;
        // Bound the offset by the actual table size so a corrupt footer cannot address outside the
        // bound: data blocks live in [0, indexOffset) and the index block + footer fill the tail.
        if (indexOffset < 0) return false;
        if (indexOffset > table.Length - FooterSize) return false;

        footer = new Footer(indexOffset);
        return true;
    }

    /// <summary>Signals unrecoverable corruption in an on-disk table — an impossible length or offset read
    /// off a torn/malformed record. Loud by design: a persisted-snapshot read fails fast here rather than
    /// silently returning a miss, which would surface downstream as wrong state.</summary>
    [DoesNotReturn]
    internal static void ThrowCorrupt(string detail) =>
        throw new InvalidOperationException(
            $"Corrupt persisted-snapshot SortedTable: {detail}. The persistedSnapshot/ directory is corrupted — wipe and resync.");
}
