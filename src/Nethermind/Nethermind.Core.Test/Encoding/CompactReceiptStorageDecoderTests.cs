// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using DotNetty.Common.Utilities;
using FluentAssertions;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;
#pragma warning disable 618

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class CompactReceiptDecoderTests
    {
        [Test]
        public void Can_do_roundtrip_storage(
            [Values(RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts, RlpBehaviors.Storage)] RlpBehaviors encodeBehaviors,
            [Values(true, false)] bool withNonEmptyTopic,
            [Values(true, false)] bool valueDecoder)
        {
            TxReceipt GetExpected()
            {
                ReceiptBuilder receiptBuilder = Build.A.Receipt.WithAllFieldsFilled;

                if ((encodeBehaviors & RlpBehaviors.Eip658Receipts) != 0)
                {
                    receiptBuilder.WithState(null!);
                }
                else
                {
                    receiptBuilder.WithStatusCode(0);
                }

                if ((encodeBehaviors & RlpBehaviors.Storage) != 0)
                {
                    receiptBuilder.WithBlockNumber(0);
                    receiptBuilder.WithBlockHash(null!);
                    receiptBuilder.WithTransactionHash(null!);
                    receiptBuilder.WithIndex(0);
                    receiptBuilder.WithGasUsed(0);
                    receiptBuilder.WithContractAddress(null!);
                    receiptBuilder.WithRecipient(null!);
                }

                if (withNonEmptyTopic)
                {
                    receiptBuilder.WithLogs(Build.A.LogEntry
                        .WithTopics(new Keccak("0x00000000000000000000000000000000000000000000000000000000000000ff"))
                        .WithData(Bytes.FromHexString("0x0000000000ff0000000000"))
                        .TestObject);
                }

                receiptBuilder.WithError(null);

                return receiptBuilder.WithCalculatedBloom().TestObject;
            }

            TxReceipt BuildReceipt()
            {
                ReceiptBuilder receiptBuilder = Build.A.Receipt.WithAllFieldsFilled;

                if (withNonEmptyTopic)
                {
                    receiptBuilder.WithLogs(Build.A.LogEntry
                        .WithTopics(new Keccak("0x00000000000000000000000000000000000000000000000000000000000000ff"))
                        .WithData(Bytes.FromHexString("0x0000000000ff0000000000"))
                        .TestObject);
                }

                return receiptBuilder.WithCalculatedBloom().TestObject;
            }

            TxReceipt txReceipt = BuildReceipt();

            CompactReceiptStorageDecoder encoder = new();
            Rlp rlp = encoder.Encode(txReceipt, encodeBehaviors);

            CompactReceiptStorageDecoder decoder = new();
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
            txReceipt.Bloom = new Bloom(txReceipt.Logs);
            txReceipt.ContractAddress = TestItem.AddressA;
            txReceipt.Sender = TestItem.AddressB;
            txReceipt.Recipient = TestItem.AddressC;
            txReceipt.GasUsed = 100;
            txReceipt.GasUsedTotal = 1000;
            txReceipt.Index = 2;
            txReceipt.PostTransactionState = TestItem.KeccakH;
            txReceipt.StatusCode = 1;

            CompactReceiptStorageDecoder decoder = new();
            Rlp rlp = decoder.Encode(txReceipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);
            TxReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpStream(), RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);

            AssertStorageReceipt(txReceipt, deserialized);
        }

        [Test]
        public void Can_do_roundtrip_storage_ref_struct()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            txReceipt.BlockNumber = 1;
            txReceipt.BlockHash = TestItem.KeccakA;
            txReceipt.Bloom = new Bloom(txReceipt.Logs);
            txReceipt.ContractAddress = TestItem.AddressA;
            txReceipt.Sender = TestItem.AddressB;
            txReceipt.Recipient = TestItem.AddressC;
            txReceipt.GasUsed = 100;
            txReceipt.GasUsedTotal = 1000;
            txReceipt.Index = 2;
            txReceipt.PostTransactionState = TestItem.KeccakH;

            CompactReceiptStorageDecoder decoder = new();

            byte[] rlpStreamResult = decoder.Encode(txReceipt, RlpBehaviors.Storage).Bytes;
            Rlp.ValueDecoderContext ctx = new(rlpStreamResult);
            decoder.DecodeStructRef(ref ctx, RlpBehaviors.Storage, out var deserialized);

            Assert.AreEqual(txReceipt.TxType, deserialized.TxType, "tx type");
            deserialized.BlockHash.Bytes.Length.Should().Be(0);
            Assert.AreEqual(0, deserialized.BlockNumber, "block number");
            Assert.AreEqual(0, deserialized.Index, "index");
            deserialized.ContractAddress.Bytes.Length.Should().Be(0);
            Assert.AreEqual(txReceipt.Sender.ToString(), deserialized.Sender.ToString(), "sender");
            Assert.AreEqual(0, deserialized.GasUsed, "gas used");
            Assert.AreEqual(txReceipt.GasUsedTotal, deserialized.GasUsedTotal, "gas used total");
            deserialized.Bloom.Bytes.Length.Should().Be(0);
            deserialized.Recipient.Bytes.Length.Should().Be(0);
            Assert.AreEqual(txReceipt.StatusCode, deserialized.StatusCode, "status");
        }

        [Test]
        public void Can_do_roundtrip_storage_rlp_stream()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            txReceipt.BlockNumber = 1;
            txReceipt.BlockHash = TestItem.KeccakA;
            txReceipt.Bloom = new Bloom(txReceipt.Logs);
            txReceipt.ContractAddress = TestItem.AddressA;
            txReceipt.Sender = TestItem.AddressB;
            txReceipt.Recipient = TestItem.AddressC;
            txReceipt.GasUsed = 100;
            txReceipt.GasUsedTotal = 1000;
            txReceipt.Index = 2;
            txReceipt.PostTransactionState = TestItem.KeccakH;

            CompactReceiptStorageDecoder decoder = new();

            byte[] rlpStreamResult = decoder.Encode(txReceipt, RlpBehaviors.Storage).Bytes;
            TxReceipt deserialized = decoder.Decode(new RlpStream(rlpStreamResult), RlpBehaviors.Storage);

            AssertStorageReceipt(txReceipt, deserialized);
        }

        [Test]
        public void Can_do_roundtrip_with_storage_receipt_and_tx_type_access_list()
        {
            TxReceipt txReceipt = Build.A.Receipt.TestObject;
            txReceipt.BlockNumber = 1;
            txReceipt.BlockHash = TestItem.KeccakA;
            txReceipt.Bloom = new Bloom(txReceipt.Logs);
            txReceipt.ContractAddress = TestItem.AddressA;
            txReceipt.Sender = TestItem.AddressB;
            txReceipt.Recipient = TestItem.AddressC;
            txReceipt.GasUsed = 100;
            txReceipt.GasUsedTotal = 1000;
            txReceipt.Index = 2;
            txReceipt.PostTransactionState = TestItem.KeccakH;
            txReceipt.StatusCode = 1;
            txReceipt.TxType = TxType.AccessList;

            CompactReceiptStorageDecoder decoder = new();
            Rlp rlp = decoder.Encode(txReceipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);
            TxReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpStream(), RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);

            txReceipt.TxType = TxType.Legacy; // Compact decoder does not store tx type

            AssertStorageReceipt(txReceipt, deserialized);
        }

        [Test]
        public void Netty_and_rlp_array_encoding_should_be_the_same()
        {
            TxReceipt[] receipts = new[]
            {
                Build.A.Receipt.WithAllFieldsFilled.TestObject,
                Build.A.Receipt.WithAllFieldsFilled.TestObject
            };

            CompactReceiptStorageDecoder decoder = new();
            Rlp rlp = decoder.Encode(receipts);
            using (NettyRlpStream nettyRlpStream = decoder.EncodeToNewNettyStream(receipts))
            {
                byte[] nettyBytes = nettyRlpStream.AsSpan().ToArray();
                nettyBytes.Should().BeEquivalentTo(rlp.Bytes);
            }
        }

        public static IEnumerable<(TxReceipt, string)> TestCaseSource()
        {
            yield return (Build.A.Receipt.WithCalculatedBloom().TestObject, "basic with defaults");
            yield return (Build.A.Receipt.WithCalculatedBloom().WithGasUsedTotal(1000).WithState(TestItem.KeccakH).TestObject, "basic");
            yield return (Build.A.Receipt.WithCalculatedBloom().WithGasUsedTotal(500).WithState(TestItem.KeccakA).WithTxType(TxType.AccessList).TestObject, "access list");
            yield return (Build.A.Receipt.WithCalculatedBloom().WithGasUsedTotal(100).WithState(TestItem.KeccakH).WithTxType(TxType.EIP1559).TestObject, "eip 1559");
        }

        [TestCaseSource(nameof(TestCaseSource))]
        public void Can_do_roundtrip_with_storage_receipt((TxReceipt TxReceipt, string Description) testCase)
        {
            TxReceipt txReceipt = testCase.TxReceipt;

            CompactReceiptStorageDecoder decoder = new();
            Rlp rlp = decoder.Encode(txReceipt, RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);
            TxReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpStream(), RlpBehaviors.Storage | RlpBehaviors.Eip658Receipts);

            txReceipt.TxType = TxType.Legacy; // It does not store tx type

            AssertStorageReceipt(txReceipt, deserialized);
        }

        private void AssertStorageReceipt(TxReceipt txReceipt, TxReceipt deserialized)
        {
            Assert.AreEqual(txReceipt.TxType, deserialized.TxType, "tx type");
            Assert.AreEqual(null, deserialized.BlockHash, "block hash");
            Assert.AreEqual(0, deserialized.BlockNumber, "block number");
            Assert.AreEqual(0, deserialized.Index, "index");
            Assert.AreEqual(null, deserialized.ContractAddress, "contract");
            Assert.AreEqual(txReceipt.Sender, deserialized.Sender, "sender");
            Assert.AreEqual(0, deserialized.GasUsed, "gas used");
            Assert.AreEqual(txReceipt.GasUsedTotal, deserialized.GasUsedTotal, "gas used total");
            Assert.AreEqual(txReceipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(null, deserialized.Recipient, "recipient");
            Assert.AreEqual(txReceipt.StatusCode, deserialized.StatusCode, "status");
        }

        private void AssertStorageLegaxyReceipt(TxReceipt txReceipt, TxReceipt deserialized)
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
