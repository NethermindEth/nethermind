// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt;

public enum PbtColumns
{
    Metadata,

    /// <summary>Legacy EIP-8297 split leaves, retained for schema detection.</summary>
    FullLeaves,

    /// <summary>Stem leaf blobs of the account header zone (0x0), keyed by stem.</summary>
    AccountLeaves,

    /// <summary>Stem leaf blobs of the content-addressed code zone (0x1), keyed by stem.</summary>
    CodeLeaves,

    /// <summary>Stem leaf blobs of the storage zones (0x8-0xF), keyed by stem.</summary>
    StorageLeaves,

    /// <summary>Stem trie nodes of the account header zone (0x0), keyed by (path bits, depth).</summary>
    AccountTrieNodes,

    /// <summary>Stem trie nodes of the content-addressed code zone (0x1), keyed by (path bits, depth).</summary>
    CodeTrieNodes,

    /// <summary>Stem trie nodes of the storage zones (0x8-0xF), keyed by (path bits, depth).</summary>
    StorageTrieNodes,
    /// <summary>Whole accounts keyed by the PBT address hash.</summary>
    Accounts,

    /// <summary>Runs of sixteen consecutive storage words (see <see cref="Persistence.SlotRunCodec"/>) keyed by the address hash, zone and remaining bytes of their EIP-8297 storage key with its low four bits cleared.</summary>
    Storages,

    /// <summary>Whole bytecode keyed by its code hash.</summary>
    Codes,

    /// <summary>Account and shared account/code node groups keyed by boundary path.</summary>
    AccountNodeGroups,

    /// <summary>Code node groups keyed by boundary path.</summary>
    CodeNodeGroups,

    /// <summary>Storage node groups keyed by boundary path.</summary>
    StorageNodeGroups,
}
