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
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Network;
using Nethermind.Synchronization;

namespace Nethermind.JsonRpc.Modules.Net
{
    public class NetBridge : INetBridge
    {
        private readonly IEnode _localNode;
        private readonly ISyncServer _syncServer;

        public NetBridge(IEnode localNode, ISyncServer syncServer)
        {
            _localNode = localNode ?? throw new ArgumentNullException(nameof(localNode));
            _syncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));
        }

        public Address LocalAddress => _localNode.Address;
        public string LocalEnode => _localNode.Info;
        public ulong NetworkId => _syncServer.ChainId;
        public int PeerCount => _syncServer.GetPeerCount();
    }
}
