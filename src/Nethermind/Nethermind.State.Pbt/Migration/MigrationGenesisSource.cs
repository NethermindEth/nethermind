// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Pbt.Migration;

/// <summary>Owns the isolated preimage-flat copy of the normally executed genesis allocation snapshot.</summary>
/// <remarks>Registered only for explicit genesis bootstrap. The stable path preserves the source across restart;
/// its state identity is verified against the independently loaded target genesis before conversion.</remarks>
internal sealed class MigrationGenesisSource : IDisposable
{
    public IColumnsDb<FlatDbColumns> Database { get; }
    public IPersistence Persistence { get; }

    public MigrationGenesisSource(IDbFactory dbFactory, ILogManager logManager)
    {
        Database = dbFactory.CreateColumnsDb<FlatDbColumns>(new DbSettings("MigrationGenesisSource", "migration-work/genesis-source")
        {
            DeleteOnStart = false,
            CanDeleteFolder = false
        });
        try { Persistence = new PreimageRocksdbPersistence(Database, logManager, FlatLayout.PreimageFlat); }
        catch
        {
            Database.Dispose();
            throw;
        }
    }

    public void Dispose() => Database.Dispose();
}
