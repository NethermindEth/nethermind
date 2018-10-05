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

using System.Numerics;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Test.Builders
{
    public class TransactionBuilder : BuilderBase<Transaction>
    {   
        public TransactionBuilder()
        {   
            TestObjectInternal = new Transaction();
            TestObjectInternal.GasPrice = 1;
            TestObjectInternal.GasLimit = 21000;
            TestObjectInternal.To = Build.An.Address.FromNumber(1).TestObject;
            TestObjectInternal.Nonce = 0;
            TestObjectInternal.Value = 1;
            TestObjectInternal.Data = new byte[0];
        }

        public TransactionBuilder WithNonce(UInt256 nonce)
        {
            TestObjectInternal.Nonce = nonce;
            return this;
        }
        
        public TransactionBuilder WithTo(Address address)
        {
            TestObjectInternal.To = address;
            return this;
        }
        
        public TransactionBuilder WithData(byte[] data)
        {
            TestObjectInternal.Data = data;
            return this;
        }
        
        public TransactionBuilder WithGasPrice(UInt256 gasPrice)
        {
            TestObjectInternal.GasPrice = gasPrice;
            return this;
        }
        
        public TransactionBuilder WithGasLimit(UInt256 gasLimit)
        {
            TestObjectInternal.GasLimit = gasLimit;
            return this;
        }
        
        public TransactionBuilder Signed(IEthereumSigner signer, PrivateKey privateKey, UInt256 blockNumber)
        {
            signer.Sign(privateKey, TestObjectInternal, blockNumber);
            return this;
        }

        // TODO: auto create signer here
        public TransactionBuilder SignedAndResolved(IEthereumSigner signer, PrivateKey privateKey, UInt256 blockNumber)
        {
            signer.Sign(privateKey, TestObjectInternal, blockNumber);
            TestObjectInternal.SenderAddress = privateKey.Address;
            return this;
        }

        protected override void BeforeReturn()
        {
            base.BeforeReturn();
            if (TestObjectInternal.IsSigned)
            {
                TestObjectInternal.Hash = Transaction.CalculateHash(TestObjectInternal);
            }
        }
    }
}