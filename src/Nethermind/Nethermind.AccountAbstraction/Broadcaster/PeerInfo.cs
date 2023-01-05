// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Network;
using Nethermind.Core.Caching;
using Nethermind.Core.Crypto;

namespace Nethermind.AccountAbstraction.Broadcaster
{
    public class PeerInfo : IUserOperationPoolPeer
    {
        private IUserOperationPoolPeer Peer { get; }

        private LruKeyCache<Keccak> NotifiedUserOperations { get; } = new(MemoryAllowance.MemPoolSize, "notifiedUserOperations");

        public PeerInfo(IUserOperationPoolPeer peer)
        {
            Peer = peer;
        }

        public PublicKey Id => Peer.Id;

        public void SendNewUserOperation(UserOperationWithEntryPoint uop)
        {
            if (NotifiedUserOperations.Set(uop.UserOperation.RequestId!))
            {
                Peer.SendNewUserOperation(uop);
            }
        }

        public void SendNewUserOperations(IEnumerable<UserOperationWithEntryPoint> uops)
        {
            Peer.SendNewUserOperations(GetUOpsToSendAndMarkAsNotified(uops));
        }

        //TODO: check whether NotifiedUserOperations will support this form
        private IEnumerable<UserOperationWithEntryPoint> GetUOpsToSendAndMarkAsNotified(IEnumerable<UserOperationWithEntryPoint> uops)
        {
            foreach (UserOperationWithEntryPoint uop in uops)
            {
                if (NotifiedUserOperations.Set(uop.UserOperation.RequestId!))
                {
                    yield return uop;
                }
            }
        }

        public override string ToString() => Peer.Enode;
    }
}
