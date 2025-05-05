// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Multiformats.Address;
using Nethermind.Logging;

namespace Nethermind.Optimism.CL.P2P;

public class PeerManager(ILogger logger) : IPeerManager
{
    private readonly SortedSet<(int, Multiaddress)> _peers = new();
    private readonly Dictionary<Multiaddress, int> _ratings = new();

    public IEnumerable<Multiaddress> GetPeers()
    {
        foreach (var peer in _peers)
        {
            logger.Error($"Rating {peer.Item1}, Peer {peer.Item2}");
        }
        return _peers.ToList().Select(p => p.Item2);
    }

    public void AddActivePeer(Multiaddress peer)
    {
        const int defaultActivePeerRating = 5;
        _ratings.Add(peer, defaultActivePeerRating);
        _peers.Add((defaultActivePeerRating, peer));
    }

    public void AddInactivePeer(Multiaddress peer)
    {
        _ratings.Add(peer, 0);
        _peers.Add((0, peer));
    }

    public void IncreaseRating(Multiaddress peer)
    {
        if (logger.IsWarn) logger.Warn($"Increasing rating from {_ratings[peer]} for peer {peer}");
        _peers.Remove((_ratings[peer], peer));
        _ratings[peer]++;
        _peers.Add((_ratings[peer], peer));
    }

    public void DecreaseRating(Multiaddress peer)
    {
        if (logger.IsWarn) logger.Warn($"Decreasing rating from {_ratings[peer]} for peer {peer}");
        _peers.Remove((_ratings[peer], peer));
        _ratings[peer]--;
        _peers.Add((_ratings[peer], peer));
    }
}
