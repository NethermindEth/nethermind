// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Db;

/// <summary>Which flat-DB trie columns are written through the trie node log instead of directly into RocksDB.</summary>
public enum TrieNodeLogScope
{
    None,

    /// <summary>The <c>StateTopNodes</c> column (state trie paths of length 0-5).</summary>
    StateTop,

    /// <summary><c>StateTopNodes</c> and <c>StateNodes</c> (all state trie nodes with paths up to 15 nibbles).</summary>
    State,

    /// <summary>All four trie columns, including <c>StorageNodes</c> and <c>FallbackNodes</c>.</summary>
    All,
}
