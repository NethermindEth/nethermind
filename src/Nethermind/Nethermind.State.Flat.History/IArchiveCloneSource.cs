// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat.History;

public readonly record struct ArchiveCloneRowPage(IReadOnlyList<HistoryRowEntry> Entries, byte[]? NextCursor, bool Refused);

public interface IArchiveCloneSource
{
    bool SupportsFullClone { get; }

    byte RowFormatVersion { get; }

    ulong Watermark { get; }

    Task<ArchiveCloneRowPage> GetHistoryRowsAsync(HistoryRowColumn column, byte[] startKey, byte[] endKey, byte[]? cursor, CancellationToken cancellationToken);
}
