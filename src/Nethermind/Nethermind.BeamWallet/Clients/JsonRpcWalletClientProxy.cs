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
// 

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Facade.Proxy;

namespace Nethermind.BeamWallet.Clients
{
    public class JsonRpcWalletClientProxy : IJsonRpcWalletClientProxy
    {
        private readonly IJsonRpcClientProxy _proxy;

        public JsonRpcWalletClientProxy(IJsonRpcClientProxy proxy)
        {
            _proxy = proxy ?? throw new ArgumentNullException(nameof(proxy));
        }

        public Task<RpcResult<bool>> personal_unlockAccount(Address address, string passphrase)
            => _proxy.SendAsync<bool>(nameof(personal_unlockAccount), address, passphrase);

        public Task<RpcResult<bool>> personal_lockAccount(Address address)
            => _proxy.SendAsync<bool>(nameof(personal_lockAccount), address);

        public Task<RpcResult<Address>> personal_newAccount(string passphrase)
            => _proxy.SendAsync<Address>(nameof(personal_newAccount), passphrase);
    }
}
