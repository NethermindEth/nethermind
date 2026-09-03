// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State.Pbt;

public enum PbtColumns
{
    Metadata,

    /// <summary>Current EIP-8297 complete keys mapped directly to 32-byte values.</summary>
    FullLeaves,

    /// <summary>Canonical compressed nodes keyed by node locator.</summary>
    CompressedNodes,

    /// <summary>Content-addressed overflow-code reference records.</summary>
    CodeReferences,

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
}
