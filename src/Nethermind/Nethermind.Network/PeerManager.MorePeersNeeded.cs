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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using Nethermind.Core.Crypto;
using Nethermind.Stats.Model;
// ReSharper disable InconsistentNaming

namespace Nethermind.Network;

public partial class PeerManager
{
    private class MorePeersNeeded : PeeringEvent
    {
        public MorePeersNeeded(PeerManager peerManager) : base(peerManager)
        {
        }
        
        private enum ActivePeerSelectionCounter
        {
            AllNonActiveCandidates,
            FilteredByZeroPort,
            FilteredByDisconnect,
            FilteredByFailedConnection
        }
        
        private static readonly ActivePeerSelectionCounter[] _enumValues = InitEnumValues();

        private static ActivePeerSelectionCounter[] InitEnumValues()
        {
            Array values = Enum.GetValues(typeof(ActivePeerSelectionCounter));
            ActivePeerSelectionCounter[] result = new ActivePeerSelectionCounter[values.Length];

            int index = 0;
            foreach (ActivePeerSelectionCounter value in values)
            {
                result[index++] = value;
            }

            return result;
        }

        public override void Execute()
        {
            RunPeerUpdateLoop().Wait();
        }

        private void CleanupCandidatePeers()
        {
            if (_peerPool.PeerCount <= _peerManager._networkConfig.CandidatePeerCountCleanupThreshold)
            {
                return;
            }

            List<Peer> candidates = _peerPool.NonStaticPeers;
            int countToRemove = candidates.Count - _peerManager._networkConfig.MaxCandidatePeerCount;
            Peer[] failedValidationCandidates = candidates.Where(x => _stats.HasFailedValidation(x.Node))
                .OrderBy(x => _stats.GetCurrentReputation(x.Node)).ToArray();
            Peer[] otherCandidates = candidates.Except(failedValidationCandidates)
                .Except(_peerPool.ActivePeers.Values)
                .OrderBy(x => _stats.GetCurrentReputation(x.Node)).ToArray();
            Peer[] nodesToRemove = failedValidationCandidates.Length <= countToRemove
                ? failedValidationCandidates
                : failedValidationCandidates.Take(countToRemove).ToArray();
            int failedValidationRemovedCount = nodesToRemove.Length;
            int remainingCount = countToRemove - failedValidationRemovedCount;
            if (remainingCount > 0)
            {
                Peer[] otherToRemove = otherCandidates.Take(remainingCount).ToArray();
                nodesToRemove = nodesToRemove.Length == 0
                    ? otherToRemove
                    : nodesToRemove.Concat(otherToRemove).ToArray();
            }

            if (nodesToRemove.Length > 0)
            {
                _logger.Info(
                    $"Removing {nodesToRemove.Length} out of {candidates.Count} peer candidates (candidates cleanup).");
                foreach (Peer peer in nodesToRemove)
                {
                    _peerPool.TryRemove(peer.Node.Id, out _);
                }

                if (_logger.IsDebug)
                    _logger.Debug(
                        $"Removing candidate peers: {nodesToRemove.Length}, failedValidationRemovedCount: {failedValidationRemovedCount}, otherRemovedCount: {remainingCount}, prevCount: {candidates.Count}, newCount: {_peerPool.PeerCount}, CandidatePeerCountCleanupThreshold: {_networkConfig.CandidatePeerCountCleanupThreshold}, MaxCandidatePeerCount: {_networkConfig.MaxCandidatePeerCount}");
            }
        }
        
        private class CandidateSelection
        {
            public List<Peer> PreCandidates { get; } = new();
            public List<Peer> Candidates { get; } = new();
            public List<Peer> Incompatible { get; } = new();
            public Dictionary<ActivePeerSelectionCounter, int> Counters { get; } = new();
        }

        private readonly CandidateSelection _currentSelection = new();

        private void SelectAndRankCandidates()
        {
            if (AvailableActivePeersCount <= 0)
            {
                return;
            }

            _currentSelection.PreCandidates.Clear();
            _currentSelection.Candidates.Clear();
            _currentSelection.Incompatible.Clear();

            for (int i = 0; i < _enumValues.Length; i++)
            {
                _currentSelection.Counters[_enumValues[i]] = 0;
            }

            foreach ((_, Peer peer) in _peerPool.Peers)
            {
                // node can be connected but a candidate (for some short times)
                // [describe when]

                // node can be active but not connected (for some short times between sending connection request and
                // establishing a session)
                if (peer.IsAwaitingConnection || peer.IsConnected ||
                    _peerPool.ActivePeers.TryGetValue(peer.Node.Id, out _))
                {
                    continue;
                }

                if (peer.Node.Port > 65535)
                {
                    continue;
                }

                _currentSelection.PreCandidates.Add(peer);
            }

            bool hasOnlyStaticNodes = false;
            List<Peer> staticPeers = _peerPool.StaticPeers;
            if (!_currentSelection.PreCandidates.Any() && staticPeers.Any())
            {
                _currentSelection.Candidates.AddRange(staticPeers.Where(sn =>
                    !_peerPool.ActivePeers.ContainsKey(sn.Node.Id)));
                hasOnlyStaticNodes = true;
            }

            if (!_currentSelection.PreCandidates.Any() && !hasOnlyStaticNodes)
            {
                return;
            }

            _currentSelection.Counters[ActivePeerSelectionCounter.AllNonActiveCandidates] =
                _currentSelection.PreCandidates.Count;

            foreach (Peer preCandidate in _currentSelection.PreCandidates)
            {
                if (preCandidate.Node.Port == 0)
                {
                    _currentSelection.Counters[ActivePeerSelectionCounter.FilteredByZeroPort]++;
                    continue;
                }

                (bool Result, NodeStatsEventType? DelayReason) delayResult =
                    _stats.IsConnectionDelayed(preCandidate.Node);
                if (delayResult.Result)
                {
                    if (delayResult.DelayReason == NodeStatsEventType.Disconnect)
                    {
                        _currentSelection.Counters[ActivePeerSelectionCounter.FilteredByDisconnect]++;
                    }
                    else if (delayResult.DelayReason == NodeStatsEventType.ConnectionFailed)
                    {
                        _currentSelection.Counters[ActivePeerSelectionCounter.FilteredByFailedConnection]++;
                    }

                    continue;
                }

                if (_stats.FindCompatibilityValidationResult(preCandidate.Node).HasValue)
                {
                    _currentSelection.Incompatible.Add(preCandidate);
                    continue;
                }

                if (preCandidate.IsConnected)
                {
                    // in transition
                    continue;
                }

                _currentSelection.Candidates.Add(preCandidate);
            }

            if (!hasOnlyStaticNodes)
            {
                _currentSelection.Candidates.AddRange(staticPeers.Where(sn =>
                    !_peerPool.ActivePeers.ContainsKey(sn.Node.Id)));
            }

            _stats.UpdateCurrentReputation(_currentSelection.Candidates);
            _currentSelection.Candidates.Sort(_peerComparer);
        }

        private async Task RunPeerUpdateLoop()
        {
            try
            {
                try
                {
                    CleanupCandidatePeers();
                }
                catch (Exception e)
                {
                    if (_logger.IsDebug) _logger.Error("Candidate peers cleanup failed", e);
                }

                if (AvailableActivePeersCount == 0)
                {
                    return;
                }

                Interlocked.Exchange(ref _peerManager._tryCount, 0);
                Interlocked.Exchange(ref _peerManager._newActiveNodes, 0);
                Interlocked.Exchange(ref _peerManager._failedInitialConnect, 0);

                SelectAndRankCandidates();
                List<Peer> remainingCandidates = _currentSelection.Candidates;
                if (!remainingCandidates.Any())
                {
                    return;
                }

                if (_cancellationTokenSource.IsCancellationRequested)
                {
                    return;
                }

                int currentPosition = 0;
                while (true)
                {
                    if (_cancellationTokenSource.IsCancellationRequested)
                    {
                        break;
                    }

                    int nodesToTry = Math.Min(remainingCandidates.Count - currentPosition,
                        AvailableActivePeersCount);
                    if (nodesToTry <= 0)
                    {
                        break;
                    }

                    ActionBlock<Peer> workerBlock = new(
                        _peerManager.SetupPeerConnection,
                        new ExecutionDataflowBlockOptions
                        {
                            MaxDegreeOfParallelism = _parallelism,
                            CancellationToken = _cancellationTokenSource.Token
                        });

                    for (int i = 0; i < nodesToTry; i++)
                    {
                        await workerBlock.SendAsync(remainingCandidates[currentPosition + i]);
                    }

                    currentPosition += nodesToTry;

                    workerBlock.Complete();

                    // Wait for all messages to propagate through the network.
                    workerBlock.Completion.Wait();
                }

                if (_logger.IsTrace)
                {
                    List<KeyValuePair<PublicKey, Peer>>? activePeers = _peerPool.ActivePeers.ToList();
                    string countersLog = string.Join(", ",
                        _currentSelection.Counters.Select(x => $"{x.Key.ToString()}: {x.Value}"));
                    _logger.Trace(
                        $"RunPeerUpdate | {countersLog}, Incompatible: {GetIncompatibleDesc(_currentSelection.Incompatible)}, EligibleCandidates: {_currentSelection.Candidates.Count()}, " +
                        $"Tried: {_peerManager._tryCount}, Failed initial connect: {_peerManager._failedInitialConnect}, Established initial connect: {_peerManager._newActiveNodes}, " +
                        $"Current candidate peers: {_peerPool.PeerCount}, Current active peers: {activePeers.Count} " +
                        $"[InOut: {activePeers.Count(x => x.Value.OutSession != null && x.Value.InSession != null)} | " +
                        $"[Out: {activePeers.Count(x => x.Value.OutSession != null)} | " +
                        $"In: {activePeers.Count(x => x.Value.InSession != null)}]");
                }

                if (_logger.IsTrace)
                {
                    string nl = Environment.NewLine;
                    _logger.Trace(
                        $"{nl}{nl}All active peers: {nl} {string.Join(nl, _peerPool.ActivePeers.Values.Select(x => $"{x.Node:s} | P2P: {_stats.GetOrAdd(x.Node).DidEventHappen(NodeStatsEventType.P2PInitialized)} | Eth62: {_stats.GetOrAdd(x.Node).DidEventHappen(NodeStatsEventType.Eth62Initialized)} | {_stats.GetOrAdd(x.Node).P2PNodeDetails?.ClientId} | {_stats.GetOrAdd(x.Node).ToString()}"))} {nl}{nl}");
                }
            }
            catch (Exception e)
            {
                _logger.Error($"Error when updating peers {e}");
            }
        }

        private string GetIncompatibleDesc(IReadOnlyCollection<Peer> incompatibleNodes)
        {
            if (!incompatibleNodes.Any())
            {
                return "0";
            }

            IGrouping<CompatibilityValidationType?, Peer>[] validationGroups =
                incompatibleNodes.GroupBy(x => _stats.FindCompatibilityValidationResult(x.Node)).ToArray();
            return $"[{string.Join(", ", validationGroups.Select(x => $"{x.Key.ToString()}:{x.Count()}"))}]";
        }
    }
}
