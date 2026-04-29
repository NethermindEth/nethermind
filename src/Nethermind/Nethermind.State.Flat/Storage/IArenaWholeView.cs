// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

/// <summary>
/// A scoped read-only view over an <see cref="ArenaReservation"/>'s bytes. For mmap-backed
/// arenas this is a fresh per-reservation accessor with normal-access madvise hints, distinct
/// from the global random-access view used by point queries. Disposing applies MADV_DONTNEED
/// to the range so the kernel can drop pages we don't need to keep resident.
/// </summary>
public interface IArenaWholeView : IDisposable
{
    ReadOnlySpan<byte> GetSpan();
}
