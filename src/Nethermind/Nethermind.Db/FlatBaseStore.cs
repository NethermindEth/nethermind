// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

/// <summary>
/// Backend for the base tier of the <see cref="FlatLayout.Flat"/> layout's Account/Storage data.
/// </summary>
public enum FlatBaseStore
{
    /// <summary>Base rows live in the RocksDB Account/Storage columns (the default).</summary>
    Rocks,

    /// <summary>
    /// Base rows live in prefix-sharded immutable sorted tables backed by mmap arena files; the RocksDB
    /// Account/Storage columns hold only a small recent-delta overlay that is periodically folded into
    /// the shard tables. Experimental.
    /// </summary>
    Arena,
}
