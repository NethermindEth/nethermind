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
    /// Purpose-built, append-only, block-major feed for replication/import (39-2 devp2p serving, 39-3 backfill
    /// import) — deliberately separate from the key-major AccountHistory/StorageHistory read-path store, which v2
    /// dropped this shape from on purpose (see <c>HistoryAvailability.FormatVersion</c>'s doc comment). Its
    /// retention and lifecycle are independent of the read-path window's floor.
    /// </summary>
    ChangesetSidecar,
}
