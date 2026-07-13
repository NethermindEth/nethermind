// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie;

internal readonly record struct WarmedTrieNode(TreePath Path, byte[] Rlp);
