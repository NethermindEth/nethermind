// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.Consensus.Qbft.Bft;
using Nethermind.Consensus.Qbft.Validators;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.Consensus.Qbft.P2P;

/// <summary>A connected peer that negotiated <c>istanbul/100</c>.</summary>
public interface IQbftPeer
{
    /// <summary>Address derived from the peer's node key; validators are identified by it.</summary>
    Address NodeAddress { get; }

    void Send(int code, byte[] data);
}

/// <summary>Sends consensus messages to the current validators.</summary>
public interface IValidatorMulticaster
{
    void Send(int code, byte[] data);

    void Send(int code, byte[] data, IReadOnlyCollection<Address> denylist);
}

/// <summary>
/// Registry of <c>istanbul/100</c> peers keyed by node address; multicasts to whichever of the current
/// validators are connected.
/// </summary>
/// <remarks>Multiple connections to the same address may briefly coexist, so each address maps to a set.</remarks>
public sealed class ValidatorPeers(IValidatorProvider validatorProvider, ILogManager logManager) : IValidatorMulticaster
{
    private readonly ConcurrentDictionary<Address, ConcurrentDictionary<IQbftPeer, byte>> _peersByAddress = new();
    private readonly ILogger _logger = logManager.GetClassLogger<ValidatorPeers>();

    /// <summary>Addresses with at least one connected peer; kept small by pruning in <see cref="Remove"/>.</summary>
    internal int TrackedAddressCount => _peersByAddress.Count;

    public void Add(IQbftPeer peer)
    {
        while (true)
        {
            ConcurrentDictionary<IQbftPeer, byte> peers = _peersByAddress.GetOrAdd(peer.NodeAddress, static _ => new ConcurrentDictionary<IQbftPeer, byte>());
            peers[peer] = 0;
            // A concurrent Remove may have dropped this set between the GetOrAdd and the write; retry on the next one.
            if (_peersByAddress.TryGetValue(peer.NodeAddress, out ConcurrentDictionary<IQbftPeer, byte>? current) && ReferenceEquals(current, peers))
            {
                return;
            }
        }
    }

    public void Remove(IQbftPeer peer)
    {
        if (_peersByAddress.TryGetValue(peer.NodeAddress, out ConcurrentDictionary<IQbftPeer, byte>? peers))
        {
            peers.TryRemove(peer, out _);
            if (peers.IsEmpty)
            {
                // Any peer that ever spoke the sub-protocol would otherwise keep its address here for the life of the node.
                ((ICollection<KeyValuePair<Address, ConcurrentDictionary<IQbftPeer, byte>>>)_peersByAddress)
                    .Remove(new KeyValuePair<Address, ConcurrentDictionary<IQbftPeer, byte>>(peer.NodeAddress, peers));
            }
        }
    }

    public int ConnectedValidatorCount
    {
        get
        {
            int count = 0;
            foreach (Address validator in validatorProvider.GetValidatorsAtHead())
            {
                if (_peersByAddress.TryGetValue(validator, out ConcurrentDictionary<IQbftPeer, byte>? peers) && !peers.IsEmpty)
                {
                    count++;
                }
            }

            return count;
        }
    }

    public void Send(int code, byte[] data) => Send(code, data, []);

    public void Send(int code, byte[] data, IReadOnlyCollection<Address> denylist)
    {
        foreach (Address validator in validatorProvider.GetValidatorsAtHead())
        {
            if (denylist.ContainsAddress(validator))
            {
                continue;
            }

            if (!_peersByAddress.TryGetValue(validator, out ConcurrentDictionary<IQbftPeer, byte>? peers))
            {
                continue;
            }

            foreach (KeyValuePair<IQbftPeer, byte> entry in peers)
            {
                try
                {
                    entry.Key.Send(code, data);
                }
                catch (Exception e)
                {
                    if (_logger.IsTrace) _logger.Trace($"Lost connection to validator {validator}: {e.Message}");
                }
            }
        }
    }
}
