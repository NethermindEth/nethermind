// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Crypto;

namespace Nethermind.Trie.Pruning;

public interface IBatchTrieNodeResolver
{
    /// <summary>Loads owned RLP values that match each requested hash. Missing or stale nodes produce null.</summary>
    void LoadRlps(ReadOnlySpan<(TreePath Path, Hash256 Hash)> requests, Span<byte[]?> values);
}
