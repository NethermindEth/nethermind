// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Trie;

public partial class PatriciaTree
{
    /// <summary>Whether a walk to a value keeps the path of the node it has reached.</summary>
    /// <remarks>Path-keyed node storage finds a node by its path, so the walk has to track it.</remarks>
    private static bool TracksPath => true;
}
