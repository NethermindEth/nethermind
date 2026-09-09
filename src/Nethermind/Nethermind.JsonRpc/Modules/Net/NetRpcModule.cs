// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Net
{
    public class NetRpcModule : INetRpcModule
    {
        private readonly INetBridge _netBridge;
        private readonly string _netVersionString;

        public NetRpcModule(ILogManager logManager, INetBridge netBridge)
        {
            _netBridge = netBridge ?? throw new ArgumentNullException(nameof(netBridge));
            _netVersionString = _netBridge.NetworkId.ToString();
        }

        public ResultWrapper<Address> net_localAddress() => ResultWrapper<Address>.Success(_netBridge.LocalAddress);

        public ResultWrapper<string> net_localEnode() => ResultWrapper<string>.Success(_netBridge.LocalEnode);

        public ResultWrapper<string> net_version() => ResultWrapper<string>.Success(_netVersionString);

        public ResultWrapper<bool> net_listening() => ResultWrapper<bool>.Success(true);

        public ResultWrapper<long> net_peerCount() => ResultWrapper<long>.Success(_netBridge.PeerCount);
    }
}
