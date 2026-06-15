// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.PersistedSnapshots;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Test convenience for the many fixtures that used to call the repository's removed
/// <c>ConvertSnapshotToPersistedSnapshot</c>: builds a <see cref="PersistedSnapshotLoader"/> over the
/// repository's own (shared) arena/blob managers and converts. A fresh default <see cref="FlatDbConfig"/>
/// is used — no convert-using test customizes bloom-bits or validation, so it is behavior-equivalent.
/// </summary>
/// <remarks>
/// The loader is convert-only here: it is not <see cref="System.IDisposable.Dispose"/>d (that would tear
/// down the repository's shared arena/blobs). It is built over the repository's own catalog db so the
/// catalog entry <see cref="PersistedSnapshotLoader.Convert"/> writes is the same one a reload reads back.
/// </remarks>
internal static class PersistedSnapshotConverterTestExtensions
{
    internal static PersistedSnapshot ConvertToPersistedBase(this SnapshotRepository repo, Snapshot snapshot)
        => new PersistedSnapshotLoader(repo, repo.ArenaManager, repo.BlobArenaManager, repo.CatalogDb, new FlatDbConfig(), LimboLogs.Instance)
            .Convert(snapshot);
}
