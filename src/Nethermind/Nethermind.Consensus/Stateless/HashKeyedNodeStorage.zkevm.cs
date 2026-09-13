// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Consensus.Stateless;

internal sealed partial class HashKeyedNodeStorage
{
    private Dictionary<NodeKey, byte[]?> _nodes = null!;

    private partial void InitializeOverlay(bool threadSafe) => _nodes = [];

    private partial bool TryGetOverlay(NodeKey key, out byte[]? value)
    {
        if (_nodes.Count != 0 && _nodes.TryGetValue(key, out value)) return true;

        value = null;
        return false;
    }

    private partial void SetOverlay(NodeKey key, byte[]? value) => _nodes[key] = value;
}
