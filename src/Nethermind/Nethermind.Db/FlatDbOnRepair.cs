// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

/// <summary>What to do after RocksDB auto-repairs the flat DB.</summary>
public enum FlatDbOnRepair
{
    /// <summary>Wipe flat columns (headers/bodies/receipts kept) and re-enter state sync.</summary>
    Resync = 0,

    /// <summary>Keep the repaired DB. Escape hatch; the node may diverge.</summary>
    Ignore = 1,
}
