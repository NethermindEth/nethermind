// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt;

public enum PbtColumns
{
    Metadata,

    /// <summary>Code-chunk leaf values of the content-addressed code zone (0x1), keyed by their EIP-8297 tree key; all-zero chunks are absent, as in the tree.</summary>
    CodeLeaves,

    /// <summary>Whole accounts as their <see cref="PbtAccount"/> stem encoding, keyed by the PBT address hash.</summary>
    Accounts,

    /// <summary>Runs of sixteen consecutive storage words (see <see cref="Persistence.SlotRunCodec"/>) keyed by the address hash, zone and remaining bytes of their EIP-8297 storage key with its low four bits cleared.</summary>
    Storages,

    /// <summary>Whole bytecode keyed by its code hash.</summary>
    Codes,

    /// <summary>Account node groups below the top, keyed by boundary path.</summary>
    AccountNodeGroups,

    /// <summary>Code node groups below the top, keyed by boundary path.</summary>
    CodeNodeGroups,

    /// <summary>Storage node groups below the top, keyed by boundary path.</summary>
    StorageNodeGroups,

    /// <summary>The top of every partition: the shared depth-four groups and the groups above each partition's top depth, keyed by boundary path.</summary>
    TopNodeGroups,
}
