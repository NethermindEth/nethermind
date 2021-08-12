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
using Nethermind.Facade.Proxy;
using Nethermind.Wallet;

namespace Nethermind.JsonRpc.Modules.Eth
{
    public class EthModuleProxyFactory : ModuleFactoryBase<IEthRpcModule>
    {
        private readonly IEthJsonRpcClientProxy _ethJsonRpcClientProxy;
        private readonly IWallet _wallet;

        public EthModuleProxyFactory(IEthJsonRpcClientProxy? ethJsonRpcClientProxy, IWallet? wallet)
        {
            _ethJsonRpcClientProxy = ethJsonRpcClientProxy ?? throw new ArgumentNullException(nameof(ethJsonRpcClientProxy));
            _wallet = wallet ?? throw new ArgumentNullException(nameof(wallet));
        }

        public override IEthRpcModule Create() => new EthRpcModuleProxy(_ethJsonRpcClientProxy, _wallet);
    }
}
