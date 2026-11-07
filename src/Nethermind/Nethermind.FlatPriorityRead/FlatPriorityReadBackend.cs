// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.FlatPriorityRead;

/// <summary>The store that holds the priority contracts' slots.</summary>
public enum FlatPriorityReadBackend
{
    /// <summary>In-process hash tables plus one immutable delta per commit; see <see cref="MemoryPrioritySlotStore"/>.</summary>
    Memory,

    /// <summary>LMDB through LightningDB; see <see cref="LmdbPrioritySlotStore"/>.</summary>
    Lmdb,

    /// <summary>RocksDB with PlainTable over mmap; see <see cref="RocksDbPrioritySlotStore"/>.</summary>
    RocksDb,
}
