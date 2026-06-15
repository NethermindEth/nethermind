// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.State.Flat.PersistedSnapshots;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// Test convenience for the many fixtures that used to call the repository's removed
/// <c>ConvertSnapshotToPersistedSnapshot</c>: builds a <see cref="PersistedSnapshotConverter"/> over
/// the repository's own (shared) arena/blob managers and converts. A fresh default
/// <see cref="FlatDbConfig"/> is used — no convert-using test customizes bloom-bits or validation, so
/// it is behavior-equivalent.
/// </summary>
internal static class PersistedSnapshotConverterTestExtensions
{
    internal static PersistedSnapshot ConvertToPersistedBase(this SnapshotRepository repo, Snapshot snapshot)
        => new PersistedSnapshotConverter(repo.ArenaManager, repo.BlobArenaManager, new FlatDbConfig(), repo).Convert(snapshot);
}
