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

using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Specs.Forks;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Core.Test.Builders
{
    public class TransactionBuilder : BuilderBase<Transaction>
    {   
        public TransactionBuilder(bool isSystem = false)
        {   
            TestObjectInternal = new Transaction(isSystem);
            TestObjectInternal.GasPrice = 1;
            TestObjectInternal.GasLimit = 21000;
            TestObjectInternal.To = Address.Zero;
            TestObjectInternal.Nonce = 0;
            TestObjectInternal.Value = 1;
            TestObjectInternal.Data = new byte[0];
            TestObjectInternal.Timestamp = 0;
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
        
        public TransactionBuilder To(Address address)
        {
            TestObjectInternal.To = address;
            return this;
        }
        
        public TransactionBuilder WithData(byte[] data)
        {
            TestObjectInternal.Data = data;
            return this;
        }
        
        public TransactionBuilder WithInit(byte[] initCode)
        {
            TestObjectInternal.Init = initCode;
            return this;
        }
        
        public TransactionBuilder WithGasPrice(UInt256 gasPrice)
        {
            TestObjectInternal.GasPrice = gasPrice;
            return this;
        }
        
        public TransactionBuilder WithGasLimit(long gasLimit)
        {
            TestObjectInternal.GasLimit = gasLimit;
            return this;
        }

        public TransactionBuilder WithTimestamp(UInt256 timestamp)
        {
            TestObject.Timestamp = timestamp;
            return this;
        }
        
        public TransactionBuilder WithValue(UInt256 value)
        {
            TestObjectInternal.Value = value;
            return this;
        }
        
        public TransactionBuilder WithSenderAddress(Address address)
        {
            TestObjectInternal.SenderAddress = address;
            return this;
        }
        
        public TransactionBuilder Signed(IEthereumEcdsa ecdsa, PrivateKey privateKey, long blockNumber)
        {
            ecdsa.Sign(privateKey, TestObjectInternal, blockNumber);
            return this;
        }

        // TODO: auto create ecdsa here
        public TransactionBuilder SignedAndResolved(IEthereumEcdsa ecdsa, PrivateKey privateKey, long blockNumber)
        {
            ecdsa.Sign(privateKey, TestObjectInternal, blockNumber);
            TestObjectInternal.SenderAddress = privateKey.Address;
            return this;
        }
        
        public TransactionBuilder SignedAndResolved()
        {
            EthereumEcdsa ecdsa = new EthereumEcdsa(MainNetSpecProvider.Instance, LimboLogs.Instance);
            ecdsa.Sign(TestItem.IgnoredPrivateKey, TestObjectInternal, 10000000);
            TestObjectInternal.SenderAddress = TestItem.IgnoredPrivateKey.Address;
            return this;
        }

        public TransactionBuilder DeliveredBy(PublicKey publicKey)
        {
            TestObject.DeliveredBy = publicKey;
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