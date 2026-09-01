// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Trie.Pruning;

/// <summary>
/// Provides an internal batch path for resolving persisted trie nodes.
/// </summary>
internal interface ITrieNodeBatchResolver
{
    void TryLoadRlpBatch(ReadOnlySpan<TreePath> paths, Span<byte[]?> values, ReadFlags flags = ReadFlags.None);
}
