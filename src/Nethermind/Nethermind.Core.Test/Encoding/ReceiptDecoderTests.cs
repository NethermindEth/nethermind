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

using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
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
                var receiptBuilder = Build.A.Receipt.WithAllFieldsFilled;
                
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
                var receiptBuilder = Build.A.Receipt.WithAllFieldsFilled;
                if (!withError)
                {
                    receiptBuilder.WithError(string.Empty);
                }

                return receiptBuilder.TestObject;
            }

            var txReceipt = BuildReceipt();

            ReceiptStorageDecoder encoder = new ReceiptStorageDecoder(encodeWithTxHash);
            Rlp rlp = encoder.Encode(txReceipt, encodeBehaviors);
            
            ReceiptStorageDecoder decoder = new ReceiptStorageDecoder();
            TxReceipt deserialized;
            if (valueDecoder)
            {
                var valueContext = rlp.Bytes.AsRlpValueContext();
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

            ReceiptMessageDecoder decoder = new ReceiptMessageDecoder();

            byte[] rlpStreamResult = decoder.EncodeNew(txReceipt, RlpBehaviors.None);
            TxReceipt deserialized = Rlp.Decode<TxReceipt>(rlpStreamResult, RlpBehaviors.None);

            Assert.AreEqual(txReceipt.GasUsedTotal, deserialized.GasUsedTotal, "gas used total");
            Assert.AreEqual(txReceipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(txReceipt.PostTransactionState, deserialized.PostTransactionState, "post transaction state");
        }
        
        [Test]
        public void Can_do_yolo_roundtrip_none_rlp_stream()
        {
            ReceiptsMessageSerializer receiptMessageSerializer
                = new ReceiptsMessageSerializer(MainnetSpecProvider.Instance);
            var hash =
                "fa1a4c42f9309ef9039f0183031de2b9010000000000000000000000000000040000002800400400000000000000000000000000200000000000000000000000000000001000000000000000000000000000000000000080000000000008004000000000001010000000000000000040000000100000020000000000000000000800000000000000000000000010000000000000000000000000000000000000000000000000000000000000000000000000000000020000000000000080440000000000000000000000000000000000000000000002000000000000000000000000000200000000000000800000010020000000000000000000000000000000000040100002000000000200000000000000f90294f89b9462bc478ffc429161115a6e4090f819ce5c50a5d9f863a0ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3efa00000000000000000000000000000000000000000000000000000000000000000a000000000000000000000000093b97eac6bbc15ed765d9a8e2d8a4afa0403050ba000000000000000000000000000000000000000000000000000536e6bd3a0eeadf8bb9462bc478ffc429161115a6e4090f819ce5c50a5d9f842a06155cfd0fd028b0ca77e8495a60cbe563e8bce8611f0aad6fedbdaafc05d44a2a000000000000000000000000093b97eac6bbc15ed765d9a8e2d8a4afa0403050bb86000000000000000000000000000000000000000000000000000536e6bd3a0eead000000000000000000000000000000000000000000000000006a94d74f4300000000000000000000000000000000000000000000000000000000000060533c29f89b944f6f4a40abf28e3425afeaff24658da2685f78adf842a07aa1a8eb998c779420645fc14513bf058edb347d95c2fc2e6845bdc22f888631a000000000000000000000000093b97eac6bbc15ed765d9a8e2d8a4afa0403050bb840000000000000000000000000000000000000000000000000006a94d74f4300000000000000000000000000000000000000000000000000000000000060533c29f89b94dea4da771cc1fccf83e24fbf96d122cc91d5d07ef842a02c7d80ba9bc6395644b4ff4a878353ac20adeed6e23cead48c8cec7a58b6e719a0d76aaac3ecd5ced13bbab3b240a426352f76a6fffd583c3b15f4ddae2b754e4eb840000000000000000000000000000000000000000000000000006a94d74f4300000000000000000000000000000000000000000000000000000000000060533c29f903db018307b117b9010000000000000001000220000000000000000000000000000001800000000000000000000000000000000000000000000000004000000000000000000100000000000000000001000001000028000200000005000000000000000000000000000000000000020000000080000000000800000000400000084000000010000000400000000000000000000800100000000000000000000800000000000000000000000000000000005000000000000000000000000000000000000400000000000000000002028000240000000000000000400008800000000000000000000020000000000000000004000000000000000000000000000000000000000000002000f902d0f89b94f74a5ca65e4552cff0f13b116113ccb493c580c5f863a0ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3efa0000000000000000000000000ba174c90d500fe913322e61d7b5cb5d36f36b242a000000000000000000000000015b1281f4e58215b2c3243d864bdf8b9dddc0da2a00000000000000000000000000000000000000000000000001c80d209ff91da25f87b94ba174c90d500fe913322e61d7b5cb5d36f36b242f863a08be0079c531659141344cd1fd0a4f28419497f9722a3daafe3b4186f6b6457e0a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000ba74beb81ae6b18a2cfc8c13a221a76290ecfaab80f89b94f74a5ca65e4552cff0f13b116113ccb493c580c5f863a0ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3efa000000000000000000000000015b1281f4e58215b2c3243d864bdf8b9dddc0da2a000000000000000000000000045b224f0cd64ed5179502da42ed4e32228485b3ba00000000000000000000000000000000000000000000000001c80d209ff91da25f89b9415b1281f4e58215b2c3243d864bdf8b9dddc0da2f863a02ed7bcf2ff03098102c7003d7ce2a633e4b49b8198b07de5383cdf4c0ab9228ba0000000000000000000000000ba74beb81ae6b18a2cfc8c13a221a76290ecfaaba0000000000000000000000000d5d2f5729d4581dfacebedf46c7014defda43585a0000000000000000000000000ba174c90d500fe913322e61d7b5cb5d36f36b242f87a9415b1281f4e58215b2c3243d864bdf8b9dddc0da2f842a0efaf768237c22e140a862d5d375ad5c153479fac3f8bcf8b580a1651fd62c3efa0000000000000000000000000ba74beb81ae6b18a2cfc8c13a221a76290ecfaaba0000000000000000000000000ba174c90d500fe913322e61d7b5cb5d36f36b242f901a70183087aa0b901000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000004000000000000000000000000000000000000000000000100000000000800000000000000000000000000000000000000000000000000000000000000000000";
            RlpStream incomingTxRlp = Bytes.FromHexString(hash).AsRlpStream();
            ReceiptMessageDecoder decoder = new ReceiptMessageDecoder();
            var decoded = receiptMessageSerializer.Deserialize(incomingTxRlp);
        }

    }
}
