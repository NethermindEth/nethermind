// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie.Sparse;

public enum SparseNodeKind : byte
{
    Empty = 0,
    Branch = 1,
    Leaf = 2,
    Blinded = 3,
}

public enum SparseNodeState : byte
{
    Revealed = 0,
    Cached = 1,
    Dirty = 2,
}
