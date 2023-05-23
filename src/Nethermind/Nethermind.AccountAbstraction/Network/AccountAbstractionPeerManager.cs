// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
        private readonly IUserOperationBroadcaster _broadcaster;
        private readonly ILogger _logger;

        public AccountAbstractionPeerManager(IDictionary<Address, IUserOperationPool> userOperationPools,
            IUserOperationBroadcaster broadcaster,
            ILogger logger)
            : this(userOperationPools, broadcaster, 0, logger)
        {
        }

        public AccountAbstractionPeerManager(IDictionary<Address, IUserOperationPool> userOperationPools,
            IUserOperationBroadcaster broadcaster,
            int numberOfPriorityAaPeers,
            ILogger logger)
        {
            _userOperationPools = userOperationPools;
            _broadcaster = broadcaster;
            _logger = logger;

            NumberOfPriorityAaPeers = numberOfPriorityAaPeers;
        }

        public int NumberOfPriorityAaPeers { get; set; }

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
                foreach (KeyValuePair<Address, IUserOperationPool> kv in _userOperationPools)
                {
                    entryPoints[counter] = kv.Key;
                    userOperations[counter] = kv.Value.GetUserOperations().ToArray();
                    totalLength += userOperations[counter].Length;
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
