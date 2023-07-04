// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.RoutingTable;

namespace Nethermind.Network.Discovery.Lifecycle;

public class EvictionManager : IEvictionManager
{
    private readonly ConcurrentDictionary<Keccak, EvictionPair?> _evictionPairs = new();
    private readonly INodeTable _nodeTable;
    private readonly ILogger _logger;

    public EvictionManager(INodeTable nodeTable, ILogManager logManager)
    {
        _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        _nodeTable = nodeTable;
    }

    public void StartEvictionProcess(INodeLifecycleManager evictionCandidate, INodeLifecycleManager replacementCandidate)
    {
        if (_logger.IsTrace) _logger.Trace($"Starting eviction process, evictionCandidate: {evictionCandidate.ManagedNode}, replacementCandidate: {replacementCandidate.ManagedNode}");

        EvictionPair newPair = new(evictionCandidate, replacementCandidate);
        EvictionPair? pair = _evictionPairs.GetOrAdd(evictionCandidate.ManagedNode.IdHash, newPair);
        if (pair != newPair)
        {
            //existing eviction in process
            //TODO add queue for further evictions
            if (_logger.IsTrace) _logger.Trace($"Existing eviction in process, evictionCandidate: {evictionCandidate.ManagedNode}, replacementCandidate: {replacementCandidate.ManagedNode}");
            return;
        }

        evictionCandidate.StartEvictionProcess();
        evictionCandidate.OnStateChanged += OnStateChange;
    }

    private void OnStateChange(object? sender, NodeLifecycleState state)
    {
        if (sender is not INodeLifecycleManager evictionCandidate)
        {
            return;
        }

        if (!_evictionPairs.TryGetValue(evictionCandidate.ManagedNode.IdHash, out EvictionPair? evictionPair))
        {
            return;
        }

        if (evictionPair is null)
        {
            return;
        }

        if (state == NodeLifecycleState.Active)
        {
            //survived eviction
            if (_logger.IsTrace) _logger.Trace($"Survived eviction process, evictionCandidate: {evictionCandidate.ManagedNode}, replacementCandidate: {evictionPair.ReplacementCandidate.ManagedNode}");
            evictionPair.ReplacementCandidate.LostEvictionProcess();
            CloseEvictionProcess(evictionCandidate);
        }
        else if (state == NodeLifecycleState.Unreachable)
        {
            //lost eviction, being replaced in nodeTable
            _nodeTable.ReplaceNode(evictionCandidate.ManagedNode, evictionPair.ReplacementCandidate.ManagedNode);
            if (_logger.IsTrace) _logger.Trace($"Lost eviction process, evictionCandidate: {evictionCandidate.ManagedNode}, replacementCandidate: {evictionPair.ReplacementCandidate.ManagedNode}");
            CloseEvictionProcess(evictionCandidate);
        }
    }

    private void CloseEvictionProcess(INodeLifecycleManager evictionCandidate)
    {
        evictionCandidate.OnStateChanged -= OnStateChange;
        _evictionPairs.TryRemove(evictionCandidate.ManagedNode.IdHash, out EvictionPair? _);
    }
}
