// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Synchronization;

namespace Nethermind.JsonRpc.Modules.Net
{
    public class NetBridge(IEnode localNode, ISyncServer syncServer) : INetBridge
    {
        private readonly IEnode _localNode = localNode ?? throw new ArgumentNullException(nameof(localNode));
        private readonly ISyncServer _syncServer = syncServer ?? throw new ArgumentNullException(nameof(syncServer));

        public Address LocalAddress => _localNode.Address;
        public string LocalEnode => _localNode.Info;
        public ulong NetworkId => _syncServer.NetworkId;
        public int PeerCount => _syncServer.GetPeerCount();
    }
}
