// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Builds a <see cref="PersistedSnapshotCompactor"/> for tests over the given
/// <see cref="SnapshotRepository"/> (which owns the compaction-assembly walk) so call sites
/// stay terse.
/// </summary>
internal static class CompactorTestFactory
{
    internal static PersistedSnapshotCompactor Create(
        SnapshotRepository repo, IArenaManager arena, IFlatDbConfig config, int scheduleOffset = 0)
        => new(
            repo,
            arena,
            config,
            ScheduleHelper.CreateWithOffset(config, scheduleOffset),
            LimboLogs.Instance);
}
