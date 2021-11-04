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
// 

using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Rlp;

namespace Nethermind.AccountAbstraction
{
    public class AccountAbstractionRpcModule : IAccountAbstractionRpcModule
    {
        private readonly IUserOperationPool _userOperationPool;

        static AccountAbstractionRpcModule()
        {
            Rlp.RegisterDecoders(typeof(UserOperationDecoder).Assembly);
        }
        
        public AccountAbstractionRpcModule(IUserOperationPool userOperationPool)
        {
            _userOperationPool = userOperationPool;
        }

        public ResultWrapper<Keccak> eth_sendUserOperation(UserOperationRpc userOperationRpc)
        {
            return _userOperationPool.AddUserOperation(new UserOperation(userOperationRpc));
        }
    }
}
