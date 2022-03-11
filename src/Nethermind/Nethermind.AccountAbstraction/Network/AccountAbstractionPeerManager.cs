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

using System.Collections.Generic;
using System.Linq;
using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.AccountAbstraction.Network
{
    public class AccountAbstractionPeerManager : IAccountAbstractionPeerManager
    {
        private IDictionary<Address, IUserOperationPool> _userOperationPools;
        private readonly UserOperationBroadcaster _broadcaster;
        private readonly ILogger _logger;

        public AccountAbstractionPeerManager(IDictionary<Address, IUserOperationPool> userOperationPools, UserOperationBroadcaster broadcaster, ILogger logger)
        {
            _userOperationPools = userOperationPools;
            _broadcaster = broadcaster;
            _logger = logger;
        }

        public void AddPeer(IUserOperationPoolPeer peer)
        {
            PeerInfo peerInfo = new(peer);
            if (_broadcaster.AddPeer(peerInfo))
            {
                // TODO: Gather all ops for all pools and submit at the same time
                Address[] entryPoints = new Address[_userOperationPools.Count];
                UserOperation[][] userOperations = new UserOperation[_userOperationPools.Count][];
                int counter = 0;
                int totalLength = 0;
                foreach (KeyValuePair<Address, IUserOperationPool> kv in _userOperationPools) {
                    entryPoints[counter] = kv.Key;
                    userOperations[counter] = kv.Value.GetUserOperations().ToArray();
                    totalLength = totalLength + userOperations[counter].Length;
                    counter++;
                }
                UserOperationWithEntryPoint[] userOperationsWithEntryPoints = new UserOperationWithEntryPoint[totalLength];
                counter = 0;
                for (int i = 0; i < _userOperationPools.Count; i++)
                {
                    // TODO: Try not to loop here, also maybe this doesn't need to be an array
                    // UserOperation[] userOperations = kv.Value.GetUserOperations().ToArray();
                    // UserOperationWithEntryPoint[] userOperationsWithEntryPoints = new UserOperationWithEntryPoint[userOperations.Length];
                    for (int j = 0; j < userOperations[i].Length; j++)
                    {
                        userOperationsWithEntryPoints[counter] = new UserOperationWithEntryPoint(userOperations[i][j], entryPoints[i]);
                        counter++;
                    }
                }
                _broadcaster.BroadcastOnce(peerInfo, userOperationsWithEntryPoints);
                
                if (_logger.IsTrace) _logger.Trace($"Added a peer to User Operation pool: {peer.Id}");
            }
        }

        public void RemovePeer(PublicKey nodeId)
        {
            if (_broadcaster.RemovePeer(nodeId))
            {
                if (_logger.IsTrace) _logger.Trace($"Removed a peer from User Operation pool: {nodeId}");
            }
        }
    }
}
