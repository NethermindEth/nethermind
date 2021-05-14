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

using System.Collections.Generic;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
#pragma warning disable 618

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class ReceiptDecoderTests
    {
        [Test]
        public void Can_do_roundtrip_storage([Values(true, false)] bool encodeWithTxHash, [Values(RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts, RlpBehaviors.Storage)] RlpBehaviors encodeBehaviors, [Values(true, false)] bool withError, [Values(true, false)] bool valueDecoder)
        {
            TxReceipt GetExpected()
            {
                ReceiptBuilder receiptBuilder = Build.A.Receipt.WithAllFieldsFilled;
                
                if ((encodeBehaviors & RlpBehaviors.Eip658Receipts) != 0)
                {
                    receiptBuilder.WithState(null);
                }
                else
                {
                    receiptBuilder.WithStatusCode(0);
                }

                if (!encodeWithTxHash)
                {
                    receiptBuilder.WithTransactionHash(null);
                }
                
                if (!withError)
                {
                    receiptBuilder.WithError(string.Empty);
                }

                return receiptBuilder.TestObject;
            }

            TxReceipt BuildReceipt()
            {
                ReceiptBuilder receiptBuilder = Build.A.Receipt.WithAllFieldsFilled;
                if (!withError)
                {
                    receiptBuilder.WithError(string.Empty);
                }

                return receiptBuilder.TestObject;
            }

            TxReceipt txReceipt = BuildReceipt();

            ReceiptStorageDecoder encoder = new ReceiptStorageDecoder(encodeWithTxHash);
            Rlp rlp = encoder.Encode(txReceipt, encodeBehaviors);
            
            ReceiptStorageDecoder decoder = new ReceiptStorageDecoder();
            TxReceipt deserialized;
            if (valueDecoder)
            {
                Rlp.ValueDecoderContext valueContext = rlp.Bytes.AsRlpValueContext();
                deserialized = decoder.Decode(ref valueContext, RlpBehaviors.Storage);
            }
            else
            {
                deserialized = decoder.Decode(rlp.Bytes.AsRlpStream(), RlpBehaviors.Storage);
            }

            deserialized.Should().BeEquivalentTo(GetExpected());
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

            ReceiptStorageDecoder decoder = new ReceiptStorageDecoder();
            Rlp rlp = decoder.Encode(txReceipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);
            TxReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpStream(), RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);

            AssertStorageReceipt(txReceipt, deserialized);
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

            ReceiptStorageDecoder decoder = new ReceiptStorageDecoder();
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

            ReceiptStorageDecoder decoder = new ReceiptStorageDecoder();

            byte[] rlpStreamResult = decoder.Encode(txReceipt, RlpBehaviors.Storage).Bytes;
            TxReceipt deserialized = decoder.Decode(new RlpStream(rlpStreamResult), RlpBehaviors.Storage);

            AssertStorageReceipt(txReceipt, deserialized);
        }

        [Test]
        public void Can_do_roundtrip_none_rlp_stream()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            txReceipt.Bloom = new Bloom();
            txReceipt.Bloom.Set(Keccak.EmptyTreeHash.Bytes);
            txReceipt.GasUsedTotal = 1000;
            txReceipt.PostTransactionState = TestItem.KeccakH;

            ReceiptMessageDecoder decoder = new ReceiptMessageDecoder();

            byte[] rlpStreamResult = decoder.EncodeNew(txReceipt, RlpBehaviors.None);
            TxReceipt deserialized = Rlp.Decode<TxReceipt>(rlpStreamResult, RlpBehaviors.None);

            AssertMessageReceipt(txReceipt, deserialized);
        }
        
        [Test]
        public void Can_do_roundtrip_with_receipt_message_and_tx_type_access_list()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            txReceipt.Bloom = new Bloom();
            txReceipt.Bloom.Set(Keccak.EmptyTreeHash.Bytes);
            txReceipt.GasUsedTotal = 1000;
            txReceipt.PostTransactionState = TestItem.KeccakH;
            txReceipt.TxType = TxType.AccessList;

            ReceiptMessageDecoder decoder = new ReceiptMessageDecoder();

            byte[] rlpStreamResult = decoder.Encode(txReceipt).Bytes;
            TxReceipt deserialized = decoder.Decode(new RlpStream(rlpStreamResult));
            
            AssertMessageReceipt(txReceipt, deserialized);
        }

        [Test]
        public void Can_do_roundtrip_with_storage_receipt_and_tx_type_access_list()
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
            txReceipt.TxType = TxType.AccessList;

            ReceiptStorageDecoder decoder = new ReceiptStorageDecoder();
            Rlp rlp = decoder.Encode(txReceipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);
            TxReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpStream(), RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);

            AssertStorageReceipt(txReceipt, deserialized);
        }
        
        public static IEnumerable<(TxReceipt, string)> TestCaseSource()
        {
            Bloom bloom = new Bloom();
            bloom.Set(Keccak.EmptyTreeHash.Bytes);
            yield return (Build.A.Receipt.TestObject, "basic with defaults");
            yield return (Build.A.Receipt.WithBloom(bloom).WithGasUsedTotal(1000).WithState(TestItem.KeccakH).TestObject, "basic");
            yield return (Build.A.Receipt.WithBloom(bloom).WithGasUsedTotal(500).WithState(TestItem.KeccakA).WithTxType(TxType.AccessList).TestObject, "access list");
            yield return (Build.A.Receipt.WithBloom(bloom).WithGasUsedTotal(100).WithState(TestItem.KeccakH).WithTxType(TxType.EIP1559).TestObject, "eip 1559");
        }
        
        [TestCaseSource(nameof(TestCaseSource))]
        public void Can_do_roundtrip_with_storage_receipt((TxReceipt TxReceipt, string Description) testCase)
        {
            TxReceipt txReceipt = testCase.TxReceipt;

            ReceiptStorageDecoder decoder = new ReceiptStorageDecoder();
            Rlp rlp = decoder.Encode(txReceipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);
            TxReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpStream(), RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);

            AssertStorageReceipt(txReceipt, deserialized);
        }
        
        [TestCaseSource(nameof(TestCaseSource))]
        public void Can_do_roundtrip_with_receipt_message((TxReceipt TxReceipt, string Description) testCase)
        {
            TxReceipt txReceipt = testCase.TxReceipt;

            ReceiptMessageDecoder decoder = new ReceiptMessageDecoder();

            byte[] rlpStreamResult = decoder.Encode(txReceipt).Bytes;
            TxReceipt deserialized = decoder.Decode(new RlpStream(rlpStreamResult));

            AssertMessageReceipt(txReceipt, deserialized);
        }
        
        private void AssertMessageReceipt(TxReceipt txReceipt, TxReceipt deserialized)
        {
            Assert.AreEqual(txReceipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(txReceipt.GasUsedTotal, deserialized.GasUsedTotal, "gas used total");
            Assert.AreEqual(txReceipt.PostTransactionState, deserialized.PostTransactionState, "post transaction state");
            Assert.AreEqual(txReceipt.StatusCode, deserialized.StatusCode, "status");
            Assert.AreEqual(txReceipt.TxType, deserialized.TxType, "type");
        }

        private void AssertStorageReceipt(TxReceipt txReceipt, TxReceipt deserialized)
        {
            Assert.AreEqual(txReceipt.TxType, deserialized.TxType, "tx type");
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

    }
}
