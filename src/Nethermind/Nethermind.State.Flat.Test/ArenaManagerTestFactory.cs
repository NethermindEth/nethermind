// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Builds an <see cref="ArenaManager"/> for tests from primitive knobs, mirroring the production
/// <see cref="IFlatDbConfig"/>-driven ctor so test call sites stay terse. The parameter list
/// matches the knobs the manager reads from config; defaults track the production defaults.
/// </summary>
internal static class ArenaManagerTestFactory
{
    internal static ArenaManager Create(
        string basePath,
        long pageCacheBytes,
        long maxArenaSize = 1L * 1024 * 1024 * 1024,
        bool fadviseOnEviction = false,
        long dedicatedArenaThreshold = 1L * 1024 * 1024 * 1024,
        bool punchHoleOnReclaim = true)
        => new(basePath, new FlatDbConfig
        {
            PersistedSnapshotArenaPageCacheBytes = pageCacheBytes,
            ArenaFileSizeBytes = maxArenaSize,
            PersistedSnapshotFadviseOnPageEviction = fadviseOnEviction,
            PersistedSnapshotDedicatedArenaThresholdBytes = dedicatedArenaThreshold,
            PersistedSnapshotPunchHoleOnReclaim = punchHoleOnReclaim,
        }, LimboLogs.Instance);
}
