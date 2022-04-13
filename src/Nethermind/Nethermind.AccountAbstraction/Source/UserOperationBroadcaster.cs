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
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.AccountAbstraction.Network;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Source
{
    /// <summary>
    /// This class responsibility is to notify other peers about interesting user operations.
    /// </summary>
    public class UserOperationBroadcaster : IUserOperationBroadcaster
    {
        /// <summary>
        /// Connected peers that can be notified about user operations.
        /// </summary>
        private readonly ConcurrentDictionary<PublicKey, IUserOperationPoolPeer> _peers = new();

        private readonly ILogger _logger;

        public UserOperationBroadcaster(ILogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public void BroadcastOnce(UserOperationWithEntryPoint op)
        {
            NotifyAllPeers(op);
        }
        
        public void BroadcastOnce(IUserOperationPoolPeer peer, UserOperationWithEntryPoint[] ops)
        {
            NotifyPeer(peer, ops);
        }
        
        private void NotifyAllPeers(UserOperationWithEntryPoint op)
        {
            if (_logger.IsDebug) _logger.Debug($"Broadcasting new user operation {op.UserOperation.RequestId!} to entryPoint {op.EntryPoint} to all peers");

            foreach ((_, IUserOperationPoolPeer peer) in _peers)
            {
                try
                {
                    peer.SendNewUserOperation(op);
                    if (_logger.IsTrace) _logger.Trace($"Notified {peer.Id} about user operation {op.UserOperation.RequestId!} to entryPoint {op.EntryPoint}.");
                }
                catch (Exception e)
                {
                    if (_logger.IsError) _logger.Error($"Failed to notify {peer.Id} about user operation {op.UserOperation.RequestId!} to entryPoint {op.EntryPoint}.", e);
                }
            }
        }
        
        private void NotifyPeer(IUserOperationPoolPeer peer, IEnumerable<UserOperationWithEntryPoint> ops)
        {
            try
            {
                peer.SendNewUserOperations(ops);
                if (_logger.IsTrace) _logger.Trace($"Notified {peer.Id} about user operations.");
            }
            catch (Exception e)
            {
                if (_logger.IsError) _logger.Error($"Failed to notify {peer.Id} about user operations.", e);
            }
        }

        public bool AddPeer(IUserOperationPoolPeer peer)
        {
            return _peers.TryAdd(peer.Id, peer);
        }

        public bool RemovePeer(PublicKey nodeId)
        {
            return _peers.TryRemove(nodeId, out _);
        }
    }
}
