// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Pruning;

public record TrieStoreState(
    long PersistedCacheMemory,
    long DirtyCacheMemory,
    long LatestCommittedBlock,
    long LastPersistedBlock);
