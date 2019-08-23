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

using System.IO;
using Nethermind.Core.Crypto;
using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class ReceiptDecoderTests
    {
        [Test]
        public void Can_do_roundtrip_storage()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            txReceipt.BlockNumber = 1;
            txReceipt.BlockHash = TestItem.KeccakA;
            txReceipt.Bloom = new Bloom();
            txReceipt.Bloom.Set(Keccak.EmptyTreeHash.Bytes);
            txReceipt.ContractAddress = TestItem.AddressA;
            txReceipt.Sender = TestItem.AddressB;
            txReceipt.Recipient = TestItem.AddressC;
            txReceipt.GasUsed = 100;
            txReceipt.GasUsedTotal = 1000;
            txReceipt.Index = 2;
            txReceipt.PostTransactionState = TestItem.KeccakH;

            ReceiptDecoder decoder = new ReceiptDecoder();
            Rlp rlp = decoder.Encode(txReceipt, RlpBehaviors.Storage);
            TxReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpStream(), RlpBehaviors.Storage);

            Assert.AreEqual(txReceipt.BlockHash, deserialized.BlockHash, "block hash");
            Assert.AreEqual(txReceipt.BlockNumber, deserialized.BlockNumber, "block number");
            Assert.AreEqual(txReceipt.Index, deserialized.Index, "index");
            Assert.AreEqual(txReceipt.ContractAddress, deserialized.ContractAddress, "contract");
            Assert.AreEqual(txReceipt.Sender, deserialized.Sender, "sender");
            Assert.AreEqual(txReceipt.GasUsed, deserialized.GasUsed, "gas used");
            Assert.AreEqual(txReceipt.GasUsedTotal, deserialized.GasUsedTotal, "gas used total");
            Assert.AreEqual(txReceipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(txReceipt.PostTransactionState, deserialized.PostTransactionState, "post transaction state");
            Assert.AreEqual(txReceipt.Recipient, deserialized.Recipient, "recipient");
            Assert.AreEqual(txReceipt.StatusCode, deserialized.StatusCode, "status");
        }

        [Test]
        public void Can_do_roundtrip_storage_eip()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            txReceipt.BlockNumber = 1;
            txReceipt.BlockHash = TestItem.KeccakA;
            txReceipt.Bloom = new Bloom();
            txReceipt.Bloom.Set(Keccak.EmptyTreeHash.Bytes);
            txReceipt.ContractAddress = TestItem.AddressA;
            txReceipt.Sender = TestItem.AddressB;
            txReceipt.Recipient = TestItem.AddressC;
            txReceipt.GasUsed = 100;
            txReceipt.GasUsedTotal = 1000;
            txReceipt.Index = 2;
            txReceipt.PostTransactionState = TestItem.KeccakH;
            txReceipt.StatusCode = 1;

            ReceiptDecoder decoder = new ReceiptDecoder();
            Rlp rlp = decoder.Encode(txReceipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);
            TxReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpStream(), RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);

            Assert.AreEqual(txReceipt.BlockHash, deserialized.BlockHash, "block hash");
            Assert.AreEqual(txReceipt.BlockNumber, deserialized.BlockNumber, "block number");
            Assert.AreEqual(txReceipt.Index, deserialized.Index, "index");
            Assert.AreEqual(txReceipt.ContractAddress, deserialized.ContractAddress, "contract");
            Assert.AreEqual(txReceipt.Sender, deserialized.Sender, "sender");
            Assert.AreEqual(txReceipt.GasUsed, deserialized.GasUsed, "gas used");
            Assert.AreEqual(txReceipt.GasUsedTotal, deserialized.GasUsedTotal, "gas used total");
            Assert.AreEqual(txReceipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(txReceipt.Recipient, deserialized.Recipient, "recipient");
            Assert.AreEqual(txReceipt.StatusCode, deserialized.StatusCode, "status");
        }

        [Test]
        public void Can_do_roundtrip_root()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            txReceipt.BlockNumber = 1;
            txReceipt.BlockHash = TestItem.KeccakA;
            txReceipt.Bloom = new Bloom();
            txReceipt.Bloom.Set(Keccak.EmptyTreeHash.Bytes);
            txReceipt.ContractAddress = TestItem.AddressA;
            txReceipt.Sender = TestItem.AddressB;
            txReceipt.Recipient = TestItem.AddressC;
            txReceipt.GasUsed = 100;
            txReceipt.GasUsedTotal = 1000;
            txReceipt.Index = 2;
            txReceipt.PostTransactionState = TestItem.KeccakH;

            ReceiptDecoder decoder = new ReceiptDecoder();
            Rlp rlp = decoder.Encode(txReceipt);
            TxReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpStream());

            Assert.AreEqual(null, deserialized.BlockHash, "block hash");
            Assert.AreEqual(0L, deserialized.BlockNumber, "block number");
            Assert.AreEqual(0, deserialized.Index, "index");
            Assert.AreEqual(null, deserialized.ContractAddress, "contract");
            Assert.AreEqual(null, deserialized.Sender, "sender");
            Assert.AreEqual(0L, deserialized.GasUsed, "gas used");
            Assert.AreEqual(1000L, deserialized.GasUsedTotal, "gas used total");
            Assert.AreEqual(txReceipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(txReceipt.PostTransactionState, deserialized.PostTransactionState, "post transaction state");
            Assert.AreEqual(null, deserialized.Recipient, "recipient");
            Assert.AreEqual(txReceipt.StatusCode, deserialized.StatusCode, "status");
        }

        [Test]
        public void Can_do_roundtrip_storage_rlp_stream()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            txReceipt.BlockNumber = 1;
            txReceipt.BlockHash = TestItem.KeccakA;
            txReceipt.Bloom = new Bloom();
            txReceipt.Bloom.Set(Keccak.EmptyTreeHash.Bytes);
            txReceipt.ContractAddress = TestItem.AddressA;
            txReceipt.Sender = TestItem.AddressB;
            txReceipt.Recipient = TestItem.AddressC;
            txReceipt.GasUsed = 100;
            txReceipt.GasUsedTotal = 1000;
            txReceipt.Index = 2;
            txReceipt.PostTransactionState = TestItem.KeccakH;

            ReceiptDecoder decoder = new ReceiptDecoder();

            byte[] rlpStreamResult = decoder.EncodeNew(txReceipt, RlpBehaviors.Storage);
            TxReceipt deserialized = Rlp.Decode<TxReceipt>(rlpStreamResult, RlpBehaviors.Storage);

            Assert.AreEqual(txReceipt.BlockHash, deserialized.BlockHash, "block hash");
            Assert.AreEqual(txReceipt.BlockNumber, deserialized.BlockNumber, "block number");
            Assert.AreEqual(txReceipt.Index, deserialized.Index, "index");
            Assert.AreEqual(txReceipt.ContractAddress, deserialized.ContractAddress, "contract");
            Assert.AreEqual(txReceipt.Sender, deserialized.Sender, "sender");
            Assert.AreEqual(txReceipt.GasUsed, deserialized.GasUsed, "gas used");
            Assert.AreEqual(txReceipt.GasUsedTotal, deserialized.GasUsedTotal, "gas used total");
            Assert.AreEqual(txReceipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(txReceipt.PostTransactionState, deserialized.PostTransactionState, "post transaction state");
            Assert.AreEqual(txReceipt.Recipient, deserialized.Recipient, "recipient");
            Assert.AreEqual(txReceipt.StatusCode, deserialized.StatusCode, "status");
        }

        [Test]
        public void Can_do_roundtrip_none_rlp_stream()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            txReceipt.Bloom = new Bloom();
            txReceipt.Bloom.Set(Keccak.EmptyTreeHash.Bytes);
            txReceipt.GasUsedTotal = 1000;
            txReceipt.PostTransactionState = TestItem.KeccakH;

            ReceiptDecoder decoder = new ReceiptDecoder();

            byte[] rlpStreamResult = decoder.EncodeNew(txReceipt, RlpBehaviors.None);
            TxReceipt deserialized = Rlp.Decode<TxReceipt>(rlpStreamResult, RlpBehaviors.None);

            Assert.AreEqual(txReceipt.GasUsedTotal, deserialized.GasUsedTotal, "gas used total");
            Assert.AreEqual(txReceipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(txReceipt.PostTransactionState, deserialized.PostTransactionState, "post transaction state");
        }
    }
}