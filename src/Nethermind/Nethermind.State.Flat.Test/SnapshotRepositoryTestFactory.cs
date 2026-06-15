// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Builds a <see cref="PersistedTierTestHarness"/> (a <see cref="SnapshotRepository"/> plus its
/// <see cref="PersistedSnapshotLoader"/>) over a fresh temp-dir-backed persisted tier (arena/blob
/// under a unique temp directory, an in-memory catalog). The repository starts with an empty persisted
/// tier, so it doubles as the in-memory-only repo for tests that don't persist. The returned harness
/// owns its arena/blob managers and must be disposed.
/// </summary>
internal static class SnapshotRepositoryTestFactory
{
    internal static PersistedTierTestHarness Create()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"nm-snaprepo-{Guid.NewGuid():N}");
        return new PersistedTierTestHarness(
            ArenaManagerTestFactory.Create(Path.Combine(dir, "arena"), 0),
            new BlobArenaManager(Path.Combine(dir, "blob"), 1024 * 1024),
            new MemDb(),
            new FlatDbConfig());
    }
}
