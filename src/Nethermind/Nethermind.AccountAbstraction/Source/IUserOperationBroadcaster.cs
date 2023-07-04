// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.AccountAbstraction.Broadcaster;
using Nethermind.AccountAbstraction.Network;
using Nethermind.Core.Crypto;

namespace Nethermind.AccountAbstraction.Source
{
    public interface IUserOperationBroadcaster
    {
        void BroadcastOnce(UserOperationWithEntryPoint op);
        void BroadcastOnce(IUserOperationPoolPeer peer, UserOperationWithEntryPoint[] ops);
        bool AddPeer(IUserOperationPoolPeer peer);
        bool RemovePeer(PublicKey nodeId);
    }
}
