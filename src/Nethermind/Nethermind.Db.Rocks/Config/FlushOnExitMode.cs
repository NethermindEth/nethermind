// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db.Rocks.Config;

/// <summary>Controls how a RocksDB instance is flushed when it is disposed on shutdown.</summary>
public enum FlushOnExitMode
{
    /// <summary>Do not flush on exit.</summary>
    None,

    /// <summary>
    /// Flush only the write-ahead log on exit. Fast shutdown; WAL-backed writes are recovered by
    /// WAL replay on the next start.
    /// </summary>
    WalOnly,

    /// <summary>Flush the write-ahead log and materialize all memtables into SST files on exit.</summary>
    Full,
}
