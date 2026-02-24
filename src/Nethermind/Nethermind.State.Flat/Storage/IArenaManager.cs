// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.Storage;

public interface IArenaManager : IDisposable
{
    void Initialize(IReadOnlyList<SnapshotCatalog.CatalogEntry> entries);
    ArenaReservation ReserveForWrite(int maximumSize);
    ArenaReservation Open(in SnapshotLocation location);
    Span<byte> GetSpan(ArenaReservation reservation);
    SnapshotLocation FinalizedWrite(ArenaReservation reservation, int actualSize);
    void Return(ArenaReservation reservation);
    void MarkDead(in SnapshotLocation location);
}
