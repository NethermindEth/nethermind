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
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class TransactionReceiptDecoderTests
    {
        [Test]
        public void Can_do_roundtrip_storage()
        {
            TransactionReceipt receipt = Build.A.Receipt.TestObject;
            receipt.BlockNumber = 1;
            receipt.BlockHash = TestObject.KeccakA;
            receipt.Bloom = new Bloom();
            receipt.Bloom.Set(Keccak.EmptyTreeHash.Bytes);
            receipt.ContractAddress = TestObject.AddressA;
            receipt.Sender = TestObject.AddressB;
            receipt.Recipient = TestObject.AddressC;
            receipt.GasUsed = 100;
            receipt.GasUsedTotal = 1000;
            receipt.Index = 2;
            receipt.PostTransactionState = TestObject.KeccakH;
            
            TransactionReceiptDecoder decoder = new TransactionReceiptDecoder();
            Rlp rlp = decoder.Encode(receipt, RlpBehaviors.Storage);
            TransactionReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpContext(), RlpBehaviors.Storage);

            Assert.AreEqual(receipt.BlockHash, deserialized.BlockHash, "block hash");
            Assert.AreEqual(receipt.BlockNumber, deserialized.BlockNumber, "block number");
            Assert.AreEqual(receipt.Index, deserialized.Index, "index");
            Assert.AreEqual(receipt.ContractAddress, deserialized.ContractAddress, "contract");
            Assert.AreEqual(receipt.Sender, deserialized.Sender, "sender");
            Assert.AreEqual(receipt.GasUsed, deserialized.GasUsed, "gas used");
            Assert.AreEqual(receipt.GasUsedTotal, deserialized.GasUsedTotal, "gas used total");
            Assert.AreEqual(receipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(receipt.PostTransactionState, deserialized.PostTransactionState, "post transaction state");
            Assert.AreEqual(receipt.Recipient, deserialized.Recipient, "recipient");
            Assert.AreEqual(receipt.StatusCode, deserialized.StatusCode, "status");
        }
        
        [Test]
        public void Can_do_roundtrip_root()
        {
            TransactionReceipt receipt = Build.A.Receipt.TestObject;
            receipt.BlockNumber = 1;
            receipt.BlockHash = TestObject.KeccakA;
            receipt.Bloom = new Bloom();
            receipt.Bloom.Set(Keccak.EmptyTreeHash.Bytes);
            receipt.ContractAddress = TestObject.AddressA;
            receipt.Sender = TestObject.AddressB;
            receipt.Recipient = TestObject.AddressC;
            receipt.GasUsed = 100;
            receipt.GasUsedTotal = 1000;
            receipt.Index = 2;
            receipt.PostTransactionState = TestObject.KeccakH;
            
            TransactionReceiptDecoder decoder = new TransactionReceiptDecoder();
            Rlp rlp = decoder.Encode(receipt);
            TransactionReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpContext());

            Assert.AreEqual(null, deserialized.BlockHash, "block hash");
            Assert.AreEqual(UInt256.Zero, deserialized.BlockNumber, "block number");
            Assert.AreEqual(0, deserialized.Index, "index");
            Assert.AreEqual(null, deserialized.ContractAddress, "contract");
            Assert.AreEqual(null, deserialized.Sender, "sender");
            Assert.AreEqual(0L, deserialized.GasUsed, "gas used");
            Assert.AreEqual(1000L, deserialized.GasUsedTotal, "gas used total");
            Assert.AreEqual(receipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(receipt.PostTransactionState, deserialized.PostTransactionState, "post transaction state");
            Assert.AreEqual(null, deserialized.Recipient, "recipient");
            Assert.AreEqual(receipt.StatusCode, deserialized.StatusCode, "status");
        }
    }
}