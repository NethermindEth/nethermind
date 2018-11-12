/*
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

using Nethermind.Config;
using Nethermind.Core;
using Nethermind.Core.Logging;
using Nethermind.JsonRpc.DataModel;

namespace Nethermind.JsonRpc.Module
{
    public class NetModule : ModuleBase, INetModule
    {
        private readonly INetBridge _netBridge;

        public NetModule(IConfigProvider configurationProvider, ILogManager logManager, IJsonSerializer jsonSerializer, INetBridge netBridge) : base(configurationProvider, logManager, jsonSerializer)
        {
            _netBridge = netBridge;
        }

        public ResultWrapper<string> net_version()
        {
            return ResultWrapper<string>.Success(_netBridge.NetworkId.ToString());
        }

        [Todo(Improve.MissingFunctionality, "Implement net_listening")]
        public ResultWrapper<bool> net_listening()
        {
            return ResultWrapper<bool>.Success(false);
        }

        public ResultWrapper<Quantity> net_peerCount()
        {
            return ResultWrapper<Quantity>.Success(new Quantity(_netBridge.PeerCount));
        }
    }
}