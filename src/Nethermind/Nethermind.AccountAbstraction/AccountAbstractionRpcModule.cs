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
using Microsoft.VisualBasic;
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
        private readonly IDictionary<Address, UserOperationPool> _userOperationPool;
        private readonly Address[] _supportedEntryPoints;

        static AccountAbstractionRpcModule()
        {
            Rlp.RegisterDecoders(typeof(UserOperationDecoder).Assembly);
        }
        
        public AccountAbstractionRpcModule(IDictionary<Address, UserOperationPool> userOperationPool, Address[] supportedEntryPoints)
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
            return _userOperationPool[entryPointAddress].AddUserOperation(new UserOperation(userOperationRpc));
        }

        public ResultWrapper<Address[]> eth_supportedEntryPoints()
        {
            return ResultWrapper<Address[]>.Success(_supportedEntryPoints);
        }
    }
}
