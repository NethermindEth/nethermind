// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;
using Nethermind.State.Flat.PersistedSnapshots.Storage;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Bundles a <see cref="SnapshotRepository"/> with a <see cref="PersistedSnapshotLoader"/> over the
/// same arena/blob/catalog, mirroring the production wiring where the loader (not the repository's
/// constructor) drives load and teardown. Constructing the harness loads the persisted tier from the
/// catalog; disposing it runs the loader's teardown (flush buckets, dispose arena/blobs).
/// </summary>
/// <remarks>
/// Replaces the old "<c>using SnapshotRepository repo = new(...)</c>" idiom in tests: reopen/restart
/// tests build a second harness over the same on-disk arena/blob/catalog to verify data survives.
/// </remarks>
internal sealed class PersistedTierTestHarness : IDisposable
{
    public SnapshotRepository Repository { get; }

    private readonly IPersistedSnapshotLoader _loader;

    public PersistedTierTestHarness(IArenaManager arena, BlobArenaManager blobs, IDb catalogDb, IFlatDbConfig config)
    {
        Repository = new SnapshotRepository(arena, blobs, catalogDb, config, LimboLogs.Instance);
        _loader = new PersistedSnapshotLoader(Repository, arena, blobs, catalogDb, config, LimboLogs.Instance);
        _loader.Load();
    }

    public void Dispose() => _loader.Dispose();
}
