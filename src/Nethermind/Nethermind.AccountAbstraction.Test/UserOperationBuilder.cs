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

using System.Collections.Generic;
using Nethermind.AccountAbstraction.Data;
using Nethermind.Core;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.AccountAbstraction.Test
{
    public class UserOperationBuilder : BuilderBase<UserOperation>
    {
        public UserOperationBuilder()
        {
            UserOperationRpc rpcOp = new UserOperationRpc()
            {
                Sender = Address.Zero,
                Nonce = 0,
                Paymaster = Address.Zero,
                CallData = Bytes.Empty,
                InitCode = Bytes.Empty,
                MaxFeePerGas = 1,
                MaxPriorityFeePerGas = 1,
                CallGas = 1_000_000,
                VerificationGas = 1_000_000,
                PreVerificationGas = 210000,
                PaymasterData = Bytes.Empty,
            };
            TestObjectInternal = new UserOperation(rpcOp);
        }

        public UserOperationBuilder WithTarget(Address target)
        {
            TestObjectInternal.Sender = target;
            return this;
        }
        
        public UserOperationBuilder WithNonce(UInt256 nonce)
        {
            TestObjectInternal.Nonce = nonce;
            return this;
        }
        
        public UserOperationBuilder WithPaymaster(Address paymaster)
        {
            TestObjectInternal.Paymaster = paymaster;
            return this;
        }
        
        public UserOperationBuilder WithCallData(byte[] bytes)
        {
            TestObjectInternal.CallData = bytes;
            return this;
        }
        
        public UserOperationBuilder WithInitCode(byte[] bytes)
        {
            TestObjectInternal.InitCode = bytes;
            return this;
        }
        
        public UserOperationBuilder WithPaymasterData(byte[] bytes)
        {
            TestObjectInternal.PaymasterData = bytes;
            return this;
        }
        
        public UserOperationBuilder WithMaxFeePerGas(ulong maxFeePerGas)
        {
            TestObjectInternal.MaxFeePerGas = maxFeePerGas;
            return this;
        }
        
        public UserOperationBuilder WithMaxPriorityFeePerGas(ulong maxPriorityFeePerGas)
        {
            TestObjectInternal.MaxPriorityFeePerGas = maxPriorityFeePerGas;
            return this;
        }
        
        public UserOperationBuilder WithCallGas(ulong callGas)
        {
            TestObjectInternal.CallGas = callGas;
            return this;
        }
        
        public UserOperationBuilder WithVerificationGas(UInt256 verificationGas)
        {
            TestObjectInternal.VerificationGas = verificationGas;
            return this;
        }
        
        public UserOperationBuilder SignedAndResolved(PrivateKey? privateKey = null)
        {
            privateKey ??= TestItem.IgnoredPrivateKey;
            AccountAbstractionRpcModuleTests.SignUserOperation(TestObjectInternal, privateKey);
            return this;
        }

        protected override void BeforeReturn()
        {
            base.BeforeReturn();
            if (TestObjectInternal.Hash == null)
            {
                TestObjectInternal.Hash = UserOperation.CalculateHash(TestObjectInternal);
            }
        }
    }
}
