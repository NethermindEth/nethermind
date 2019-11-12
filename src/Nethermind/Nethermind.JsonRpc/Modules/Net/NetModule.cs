﻿/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Numerics;
using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Net
{
    public class NetModule : INetModule
    {
        private readonly INetBridge _netBridge;
        private string _netVersionString;

        public NetModule(ILogManager logManager, INetBridge netBridge)
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

        [Todo(Improve.MissingFunctionality, "Implement net_listening")]
        public ResultWrapper<bool> net_listening()
        {
            return ResultWrapper<bool>.Success(false);
        }

        public ResultWrapper<int> net_peerCount()
        {
            return ResultWrapper<int>.Success(_netBridge.PeerCount);
        }
        
        public ResultWrapper<bool> net_dumpPeerConnectionDetails()
        {
            var result = _netBridge.LogPeerConnectionDetails();
            return ResultWrapper<bool>.Success(result);
        }
    }
}