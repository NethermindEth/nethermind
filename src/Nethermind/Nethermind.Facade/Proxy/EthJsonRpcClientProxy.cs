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

using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Facade.Proxy
{
    public class EthJsonRpcClientProxy : IEthJsonRpcClientProxy
    {
        private readonly IJsonRpcClientProxy _proxy;

        public EthJsonRpcClientProxy(IJsonRpcClientProxy proxy)
        {
            _proxy = proxy;
        }

        public Task<RpcResult<UInt256?>> eth_blockNumber()
            => _proxy.SendAsync<UInt256?>(nameof(eth_blockNumber));

        public Task<RpcResult<UInt256?>> eth_getBalance(Address address, string blockParameter = null,
            long? blockNumber = null)
            => _proxy.SendAsync<UInt256?>(nameof(eth_getBalance), address, blockParameter);
    }
}