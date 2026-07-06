// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;

namespace Nethermind.State.Flat;

/// <summary>
/// Fsyncs the block tree databases before the persisted flat state pointer advances
/// (per checkpoint, not per block).
/// </summary>
public sealed class BlockTreeDurabilityBarrier(IDbProvider dbProvider) : IPersistenceBarrier
{
    /// <inheritdoc/>
    public void BeforePersistedStateAdvance()
    {
        dbProvider.HeadersDb.Flush(onlyWal: true);
        dbProvider.BlockInfosDb.Flush(onlyWal: true);
    }
}
