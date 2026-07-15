// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt;

public enum PbtColumns
{
    Metadata,

    /// <summary>Flat account entries keyed by blake3(address32).</summary>
    Account,

    /// <summary>Flat storage entries keyed by blake3(address32) || slot32BE.</summary>
    Storage,

    /// <summary>Stem leaf blobs of the account header zone (0x0), keyed by stem.</summary>
    AccountLeaves,

    /// <summary>Stem leaf blobs of the content-addressed code zone (0x1), keyed by stem.</summary>
    CodeLeaves,

    /// <summary>Stem leaf blobs of the storage zones (0x8-0xF), keyed by stem.</summary>
    StorageLeaves,

    /// <summary>Stem trie nodes keyed by (depth, path bits).</summary>
    TrieNodes,
}
