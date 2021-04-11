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
using Nethermind.Core;
using Nethermind.Core.Attributes;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Net
{
    public class NetRpcModule : INetRpcModule
    {
        private readonly INetBridge _netBridge;
        private string _netVersionString;

        public NetRpcModule(ILogManager logManager, INetBridge netBridge)
        {
            _netBridge = netBridge ?? throw new ArgumentNullException(nameof(netBridge));
            _netVersionString = _netBridge.NetworkId.ToString();
        }

        public ResultWrapper<Address> net_localAddress()
        {
            return ResultWrapper<Address>.Success(_netBridge.LocalAddress);
        }

        public ResultWrapper<string> net_localEnode()
        {
            return ResultWrapper<string>.Success(_netBridge.LocalEnode);
        }

        public ResultWrapper<string> net_version()
        {
            return ResultWrapper<string>.Success(_netVersionString);
        }

        public ResultWrapper<bool> net_listening()
        {
            return ResultWrapper<bool>.Success(true);
        }

        public ResultWrapper<long> net_peerCount()
        {
            return ResultWrapper<long>.Success(_netBridge.PeerCount);
        }
    }
}
