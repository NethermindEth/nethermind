// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A scoped read-only view over an <see cref="ArenaReservation"/>'s bytes. For mmap-backed
/// arenas this is a fresh per-reservation accessor with normal-access madvise hints, distinct
/// from the global random-access view used by point queries. Disposing applies MADV_DONTNEED
/// to the range so the kernel can drop pages we don't need to keep resident.
/// </summary>
public unsafe interface IArenaWholeView : IDisposable
{
    /// <summary>
    /// Single-Span view over the reservation's bytes. Throws on materialisation if
    /// the reservation exceeds <see cref="int.MaxValue"/>; use <see cref="DataPtr"/>
    /// + <see cref="Size"/> for chunk-aware access of larger views.
    /// </summary>
    ReadOnlySpan<byte> GetSpan();

    /// <summary>
    /// Raw pointer to the first byte of the view. Long-offset arithmetic on this
    /// pointer is valid for the entire <see cref="Size"/> range; the view's
    /// underlying memory (mmap pages or pinned byte[]) is kept alive until
    /// <see cref="IDisposable.Dispose"/>.
    /// </summary>
    byte* DataPtr { get; }

    /// <summary>Total view length in bytes (long-typed).</summary>
    long Size { get; }
}
