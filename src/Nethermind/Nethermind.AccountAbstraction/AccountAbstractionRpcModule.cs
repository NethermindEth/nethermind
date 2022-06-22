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

using System.Linq;
using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.JsonRpc;
using Nethermind.Serialization.Rlp;

namespace Nethermind.AccountAbstraction
{
    public class AccountAbstractionRpcModule : IAccountAbstractionRpcModule
    {
        private readonly IDictionary<Address, IUserOperationPool> _userOperationPool;
        private readonly Address[] _supportedEntryPoints;

        static AccountAbstractionRpcModule()
        {
            Rlp.RegisterDecoders(typeof(UserOperationDecoder).Assembly);
        }
        
        public AccountAbstractionRpcModule(IDictionary<Address, IUserOperationPool> userOperationPool, Address[] supportedEntryPoints)
        {
            _userOperationPool = userOperationPool;
            _supportedEntryPoints = supportedEntryPoints;
        }

        public ResultWrapper<Keccak> eth_sendUserOperation(UserOperationRpc userOperationRpc, Address entryPointAddress)
        {
            if (!_supportedEntryPoints.Contains(entryPointAddress))
            {
                return ResultWrapper<Keccak>.Fail($"entryPoint {entryPointAddress} not supported, supported entryPoints: {string.Join(", ", _supportedEntryPoints.ToList())}");
            }
            
            // check if any entrypoint has both the sender and same nonce, if they do then the fee must increase 
            bool allow = _supportedEntryPoints
                .Select(ep => _userOperationPool[ep])
                .Where(pool => pool.IncludesUserOperationWithSenderAndNonce(userOperationRpc.Sender, userOperationRpc.Nonce))
                .All(pool => pool.CanInsert(new UserOperation(userOperationRpc)));

            if (!allow)
            {
                return ResultWrapper<Keccak>.Fail("op with same nonce and sender already present in a pool but op fee increase is not large enough to replace it");
            }

            return _userOperationPool[entryPointAddress].AddUserOperation(new UserOperation(userOperationRpc));
        }

        public ResultWrapper<Address[]> eth_supportedEntryPoints()
        {
            return ResultWrapper<Address[]>.Success(_supportedEntryPoints);
        }
    }
}
