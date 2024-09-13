// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.Core.Crypto;

namespace Nethermind.AccountAbstraction.Network
{
    public interface IAccountAbstractionPeerManager
    {
        int NumberOfPriorityAaPeers { get; set; }
        void AddPeer(IUserOperationPoolPeer peer);
        void RemovePeer(PublicKey nodeId);
    }
}
