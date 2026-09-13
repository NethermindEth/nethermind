// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Nethermind.Consensus.Stateless;

internal sealed partial class HashKeyedNodeStorage
{
    private Dictionary<NodeKey, byte[]?>? _nodes;
    private ConcurrentDictionary<NodeKey, byte[]?>? _concurrentNodes;

    private partial void InitializeOverlay(bool threadSafe)
    {
        if (threadSafe)
        {
            _concurrentNodes = [];
        }
        else
        {
            _nodes = [];
        }
    }

    private partial bool TryGetOverlay(NodeKey key, out byte[]? value)
    {
        if (_concurrentNodes is not null)
        {
            if (_concurrentNodes.TryGetValue(key, out value))
            {
                return true;
            }
        }
        else if (_nodes!.TryGetValue(key, out value))
        {
            return true;
        }

        value = null;
        return false;
    }

    private partial void SetOverlay(NodeKey key, byte[]? value)
    {
        if (_concurrentNodes is not null)
        {
            _concurrentNodes[key] = value;
        }
        else
        {
            _nodes![key] = value;
        }
    }
}
