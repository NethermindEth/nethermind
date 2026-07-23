// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt;

public enum PbtColumns
{
    Metadata,

    /// <summary>Stem leaf blobs of the account header zone (0x0), keyed by stem.</summary>
    AccountLeaves,

    /// <summary>Stem leaf blobs of the content-addressed code zone (0x1), keyed by stem.</summary>
    CodeLeaves,

    /// <summary>Stem leaf blobs of the storage zones (0x8-0xF), keyed by stem.</summary>
    StorageLeaves,

    /// <summary>
    /// Stem trie nodes of the account header zone (0x0), keyed by (path bits, depth), plus the
    /// depth-0 root group, whose path has no zone nibble yet.
    /// </summary>
    AccountTrieNodes,

    /// <summary>Stem trie nodes of the content-addressed code zone (0x1), keyed by (path bits, depth).</summary>
    CodeTrieNodes,

    /// <summary>Stem trie nodes of the storage zones (0x8-0xF), keyed by (path bits, depth).</summary>
    StorageTrieNodes,
}
