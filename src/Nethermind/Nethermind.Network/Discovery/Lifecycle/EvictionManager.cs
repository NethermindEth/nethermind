//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Logging;
using Nethermind.Network.Discovery.RoutingTable;

namespace Nethermind.Network.Discovery.Lifecycle
{
    public class EvictionManager : IEvictionManager
    {
        private readonly ConcurrentDictionary<Keccak, EvictionPair> _evictionPairs = new ConcurrentDictionary<Keccak, EvictionPair>();
        private readonly INodeTable _nodeTable;
        private readonly ILogger _logger;

        public EvictionManager(INodeTable nodeTable, ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
            _nodeTable = nodeTable;
        }

        public void StartEvictionProcess(INodeLifecycleManager evictionCandidate, INodeLifecycleManager replacementCandidate)
        {
            if(_logger.IsTrace) _logger.Trace($"Starting eviction process, evictionCandidate: {evictionCandidate.ManagedNode}, replacementCandidate: {replacementCandidate.ManagedNode}");

            EvictionPair newPair = new EvictionPair
            {
                EvictionCandidate = evictionCandidate,
                ReplacementCandidate = replacementCandidate,
            };

            EvictionPair pair = _evictionPairs.GetOrAdd(evictionCandidate.ManagedNode.IdHash, newPair);
            if (pair != newPair)
            {
                //existing eviction in process
                //TODO add queue for further evictions
                if(_logger.IsTrace) _logger.Trace($"Existing eviction in process, evictionCandidate: {evictionCandidate.ManagedNode}, replacementCandidate: {replacementCandidate.ManagedNode}");
                return;
            }
            
            evictionCandidate.StartEvictionProcess();
            evictionCandidate.OnStateChanged += OnStateChange;
        }

        private void OnStateChange(object sender, NodeLifecycleState state)
        {
            if (!(sender is INodeLifecycleManager evictionCandidate))
            {
                return;
            }

            if (!_evictionPairs.TryGetValue(evictionCandidate.ManagedNode.IdHash, out EvictionPair evictionPair))
            {
                return;
            }

            if (state == NodeLifecycleState.Active)
            {
                //survived eviction
                if(_logger.IsTrace) _logger.Trace($"Survived eviction process, evictionCandidate: {evictionCandidate.ManagedNode}, replacementCandidate: {evictionPair.ReplacementCandidate.ManagedNode}");
                evictionPair.ReplacementCandidate.LostEvictionProcess();
                CloseEvictionProcess(evictionCandidate);
            }
            else if (state == NodeLifecycleState.Unreachable)
            {
                //lost eviction, being replaced in nodeTable
                _nodeTable.ReplaceNode(evictionCandidate.ManagedNode, evictionPair.ReplacementCandidate.ManagedNode);
                if(_logger.IsTrace) _logger.Trace($"Lost eviction process, evictionCandidate: {evictionCandidate.ManagedNode}, replacementCandidate: {evictionPair.ReplacementCandidate.ManagedNode}");
                CloseEvictionProcess(evictionCandidate);
            }
        }

        private void CloseEvictionProcess(INodeLifecycleManager evictionCandidate)
        {
            evictionCandidate.OnStateChanged -= OnStateChange;
            _evictionPairs.TryRemove(evictionCandidate.ManagedNode.IdHash, out EvictionPair _);
        }
    }
}
