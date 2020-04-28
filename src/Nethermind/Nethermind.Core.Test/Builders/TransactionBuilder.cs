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

using System;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Specs;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Logging;

namespace Nethermind.Core.Test.Builders
{
    public class TransactionBuilder<T> : BuilderBase<Transaction> where T : Transaction, new()
    {   
        public TransactionBuilder()
        {
            TestObjectInternal = new T
            {
                GasPrice = 1,
                GasLimit = Transaction.BaseTxGasCost,
                To = Address.Zero,
                Nonce = 0,
                Value = 1,
                Data = new byte[0],
                Timestamp = 0
            };
        }

        public TransactionBuilder<T> WithNonce(UInt256 nonce)
        {
            TestObjectInternal.Nonce = nonce;
            return this;
        }
        
        public TransactionBuilder<T> WithTo(Address address)
        {
            TestObjectInternal.To = address;
            return this;
        }
        
        public TransactionBuilder<T> To(Address address)
        {
            TestObjectInternal.To = address;
            return this;
        }
        
        public TransactionBuilder<T> WithData(byte[] data)
        {
            TestObjectInternal.Init = null;
            TestObjectInternal.Data = data;
            return this;
        }
        
        public TransactionBuilder<T> WithInit(byte[] initCode)
        {
            TestObjectInternal.Data = null;
            TestObjectInternal.Init = initCode;
            return this;
        }
        
        public TransactionBuilder<T> WithGasPrice(UInt256 gasPrice)
        {
            TestObjectInternal.GasPrice = gasPrice;
            return this;
        }
        
        public TransactionBuilder<T> WithGasLimit(long gasLimit)
        {
            TestObjectInternal.GasLimit = gasLimit;
            return this;
        }

        public TransactionBuilder<T> WithTimestamp(UInt256 timestamp)
        {
            TestObject.Timestamp = timestamp;
            return this;
        }
        
        public TransactionBuilder<T> WithValue(UInt256 value)
        {
            TestObjectInternal.Value = value;
            return this;
        }
        
        public TransactionBuilder<T> WithValue(int value)
        {
            TestObjectInternal.Value = (UInt256) value;
            return this;
        }
        
        public TransactionBuilder<T> WithSenderAddress(Address address)
        {
            TestObjectInternal.SenderAddress = address;
            return this;
        }
        
        public TransactionBuilder<T> Signed(IEthereumEcdsa ecdsa, PrivateKey privateKey, long blockNumber)
        {
            ecdsa.Sign(privateKey, TestObjectInternal, blockNumber);
            return this;
        }

        // TODO: auto create ecdsa here
        public TransactionBuilder<T> SignedAndResolved(IEthereumEcdsa ecdsa, PrivateKey privateKey, long blockNumber)
        {
            ecdsa.Sign(privateKey, TestObjectInternal, blockNumber);
            TestObjectInternal.SenderAddress = privateKey.Address;
            return this;
        }
        
        private EthereumEcdsa _ecdsa = new EthereumEcdsa(MainnetSpecProvider.Instance, LimboLogs.Instance);
        
        public TransactionBuilder<T> SignedAndResolved(PrivateKey privateKey)
        {
            _ecdsa.Sign(privateKey, TestObjectInternal, MainnetSpecProvider.MuirGlacierBlockNumber);
            TestObjectInternal.SenderAddress = privateKey.Address;
            return this;
        }
        
        public TransactionBuilder<T> SignedAndResolved()
        {
            EthereumEcdsa ecdsa = new EthereumEcdsa(MainnetSpecProvider.Instance, LimboLogs.Instance);
            ecdsa.Sign(TestItem.IgnoredPrivateKey, TestObjectInternal, 10000000);
            TestObjectInternal.SenderAddress = TestItem.IgnoredPrivateKey.Address;
            return this;
        }

        public TransactionBuilder<T> DeliveredBy(PublicKey publicKey)
        {
            TestObjectInternal.DeliveredBy = publicKey;
            return this;
        }

        protected override void BeforeReturn()
        {
            base.BeforeReturn();
            if (TestObjectInternal.IsSigned)
            {
                TestObjectInternal.Hash = TestObjectInternal.CalculateHash();
            }
        }
    }
}