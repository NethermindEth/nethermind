// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Flat;

public enum FlatDbColumns
{
    Metadata,
    Account,
    Storage,
    StateNodes,
    StateTopNodes,
    StorageNodes,
    FallbackNodes,
}

public enum FlatHistoryColumns
{
    AccountHistory,
    StorageHistory,
    AvailableBlocks,
    StorageClears,

    /// <summary>
    /// Retired block-major changeset feed. The enum entry stays so RocksDB keeps opening databases that already
    /// created its column family; nothing reads or writes it anymore.
    /// </summary>
    ChangesetSidecar,
}
