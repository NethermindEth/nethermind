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

using System;
using System.Threading.Tasks;
using Nethermind.AccountAbstraction.Data;
using Nethermind.AccountAbstraction.Source;
using Nethermind.Core.Crypto;

namespace Nethermind.AccountAbstraction.Broadcaster
{
    public class UserOperationPoolSender : IUserOperationSender
    {
        private readonly IUserOperationPool _userOperationPool;
        private readonly IUserOperationSealer[] _sealers;

        public UserOperationPoolSender(IUserOperationPool userOperationPool, params IUserOperationSealer[] sealers)
        {
            _userOperationPool = userOperationPool ?? throw new ArgumentNullException(nameof(userOperationPool));
            _sealers = sealers ?? throw new ArgumentNullException(nameof(sealers));
            if (sealers.Length == 0) throw new ArgumentException("Sealers can not be empty.", nameof(sealers));
        }
        
        public ValueTask<(Keccak? Hash, AddUserOperationResult? addUserOperationResult)> SendUserOperation(UserOperation uop, UserOperationHandlingOptions userOperationHandlingOptions)
        {
            AddUserOperationResult? result = null;
            
            foreach (IUserOperationSealer sealer in _sealers)
            {
                sealer.Seal(uop, userOperationHandlingOptions);
                
                result = _userOperationPool.SubmitUserOperation(uop, userOperationHandlingOptions);

                if (result != AddUserOperationResult.OwnNonceAlreadyUsed && result != AddUserOperationResult.AlreadyKnown
                    || (userOperationHandlingOptions & UserOperationHandlingOptions.ManagedNonce) != UserOperationHandlingOptions.ManagedNonce)
                {
                    break;
                }
            }

            return new ValueTask<(Keccak, AddUserOperationResult?)>((uop.Hash, result));
        }
    }
}
