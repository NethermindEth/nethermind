//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.Net.Http;
using Nethermind.Core;
using Nethermind.Facade.Proxy;
using Nethermind.Logging;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthModuleProxyFactory : ModuleFactoryBase<IEthModule>
    {
        private readonly string[] _urlProxies;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogManager _logManager;

        public EthModuleProxyFactory(string[] urlProxies, IJsonSerializer jsonSerializer, ILogManager logManager)
        {
            _urlProxies = urlProxies;
            _jsonSerializer = jsonSerializer;
            _logManager = logManager;
        }

        public override IEthModule Create()
            => new EthModuleProxy(new EthJsonRpcClientProxy(new JsonRpcClientProxy(new DefaultHttpClient(
                new HttpClient(), _jsonSerializer, _logManager), _urlProxies)));
    }
}