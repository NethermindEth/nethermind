// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;

namespace Nethermind.Consensus.Stateless;

internal sealed partial class HashKeyedNodeStorage
{
    // The host updates storage roots on several threads, so the overlay stays safe for readers on any of them.
    private readonly ConcurrentDictionary<NodeKey, byte[]?> _nodes = new();

    private bool TryGetOverlay(NodeKey key, out byte[]? value) => _nodes.TryGetValue(key, out value);

    private void SetOverlay(NodeKey key, byte[]? value) => _nodes[key] = value;
}
