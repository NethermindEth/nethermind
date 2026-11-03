// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Test.IO;
using Nethermind.Db;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;
using Nethermind.State.Flat.Persistence.PaprikaFlat;
using Nethermind.State.Flat.Persistence.TrieNodeLog;
using Paprika.Store;
using Testably.Abstractions;

namespace Nethermind.State.Flat.Test;

/// <summary>
/// The persistence of one <see cref="FlatLayout"/> over a test's flat DB, with the stores the layout needs besides it:
/// for <see cref="FlatLayout.PaprikaFlat"/>, an in-memory Paprika store and the trie node log it requires.
/// </summary>
internal sealed class LayoutPersistence : IDisposable
{
    private const long PaprikaSizeBytes = 64 * 1024 * 1024;

    private readonly PaprikaFlatStore? _paprika;
    private readonly TempPath? _logDirectory;
    private readonly TrieNodeLog? _log;

    /// <param name="db">The flat DB; it must outlive this instance.</param>
    /// <param name="layout">The layout of the persistence.</param>
    /// <param name="trieNodeLog">Whether a RocksDB layout logs its trie nodes; <see cref="FlatLayout.PaprikaFlat"/> always does.</param>
    public LayoutPersistence(IColumnsDb<FlatDbColumns> db, FlatLayout layout, bool trieNodeLog)
    {
        FlatDbConfig config = new() { Layout = layout, TrieNodeLogEnabled = trieNodeLog || layout == FlatLayout.PaprikaFlat };
        _paprika = layout == FlatLayout.PaprikaFlat
            ? new PaprikaFlatStore(db, null, static () => PagedDb.NativeMemoryDb(PaprikaSizeBytes, PaprikaFlatStore.HistoryDepth), TimeSpan.FromSeconds(1), LimboLogs.Instance)
            : null;
        try
        {
            Metadata = _paprika is null ? new ColumnsFlatMetadataStore(db, config) : _paprika;

            ITrieNodeLog log = NullTrieNodeLog.Instance;
            if (config.TrieNodeLogEnabled)
            {
                _logDirectory = TempPath.GetTempDirectory();
                log = _log = new TrieNodeLog(_logDirectory.Path, new RealFileSystem(), db, Metadata, config, LimboLogs.Instance);
            }

            Persistence = layout switch
            {
                FlatLayout.Flat => new RocksDbPersistence(db, LimboLogs.Instance, log),
                FlatLayout.FlatInTrie => new FlatInTriePersistence(db, LimboLogs.Instance, log),
                FlatLayout.PreimageFlatV1 or FlatLayout.PreimageFlat => new PreimageRocksdbPersistence(db, LimboLogs.Instance, layout, log),
                FlatLayout.PaprikaFlat => new PaprikaFlatPersistence(db, _paprika!, log, config),
                _ => throw new ArgumentOutOfRangeException(nameof(layout)),
            };
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public IPersistence Persistence { get; }

    /// <summary>The store the layout keeps its markers in.</summary>
    public IFlatMetadataStore Metadata { get; }

    public void Dispose()
    {
        _log?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _paprika?.Dispose();
        _logDirectory?.Dispose();
    }
}
