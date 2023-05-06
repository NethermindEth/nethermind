// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using DotNetty.Buffers;
using FluentAssertions;
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using Nethermind.Serialization.Rlp;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class ReceiptsMessageSerializerTests
    {
        private static void Test(TxReceipt[][] txReceipts)
        {
            ReceiptsMessage message = new(txReceipts);
            ReceiptsMessageSerializer serializer = new(RopstenSpecProvider.Instance);
            var serialized = serializer.Serialize(message);
            ReceiptsMessage deserialized = serializer.Deserialize(serialized);

            if (txReceipts is null)
            {
                Assert.That(deserialized.TxReceipts.Length, Is.EqualTo(0));
            }
            else
            {
                Assert.That(deserialized.TxReceipts.Length, Is.EqualTo(txReceipts.Length), "length");
                for (int i = 0; i < txReceipts.Length; i++)
                {
                    if (txReceipts[i] is null)
                    {
                        Assert.IsNull(deserialized.TxReceipts[i], $"receipts[{i}]");
                    }
                    else
                    {
                        for (int j = 0; j < txReceipts[i].Length; j++)
                        {
                            if (txReceipts[i][j] is null)
                            {
                                Assert.IsNull(deserialized.TxReceipts[i][j], $"receipts[{i}][{j}]");
                            }
                            else
                            {
                                Assert.That(deserialized.TxReceipts[i][j].TxType, Is.EqualTo(txReceipts[i][j].TxType), $"receipts[{i}][{j}].TxType");
                                Assert.That(deserialized.TxReceipts[i][j].Bloom, Is.EqualTo(txReceipts[i][j].Bloom), $"receipts[{i}][{j}].Bloom");
                                Assert.Null(deserialized.TxReceipts[i][j].Error, $"receipts[{i}][{j}].Error");
                                Assert.That(deserialized.TxReceipts[i][j].Index, Is.EqualTo(0), $"receipts[{i}][{j}].Index");
                                Assert.That(deserialized.TxReceipts[i][j].Logs.Length, Is.EqualTo(txReceipts[i][j].Logs.Length), $"receipts[{i}][{j}].Logs.Length");
                                Assert.Null(deserialized.TxReceipts[i][j].Recipient, $"receipts[{i}][{j}].Recipient");
                                Assert.Null(deserialized.TxReceipts[i][j].Sender, $"receipts[{i}][{j}].Sender");
                                Assert.Null(deserialized.TxReceipts[i][j].BlockHash, $"receipts[{i}][{j}].BlockHash");
                                Assert.That(deserialized.TxReceipts[i][j].BlockNumber, Is.EqualTo(0L), $"receipts[{i}][{j}].BlockNumber");
                                Assert.Null(deserialized.TxReceipts[i][j].ContractAddress, $"receipts[{i}][{j}].ContractAddress");
                                Assert.That(deserialized.TxReceipts[i][j].GasUsed, Is.EqualTo(0L), $"receipts[{i}][{j}].GasUsed");
                                Assert.That(deserialized.TxReceipts[i][j].GasUsedTotal, Is.EqualTo(txReceipts[i][j].GasUsedTotal), $"receipts[{i}][{j}].GasUsedTotal");
                                if (!txReceipts[i][j].SkipStateAndStatusInRlp)
                                {
                                    Assert.That(deserialized.TxReceipts[i][j].StatusCode, Is.EqualTo(txReceipts[i][j].BlockNumber < RopstenSpecProvider.ByzantiumBlockNumber ? 0 : txReceipts[i][j].StatusCode), $"receipts[{i}][{j}].StatusCode");
                                    Assert.That(deserialized.TxReceipts[i][j].PostTransactionState, Is.EqualTo(txReceipts[i][j].BlockNumber < RopstenSpecProvider.ByzantiumBlockNumber ? txReceipts[i][j].PostTransactionState : null), $"receipts[{i}][{j}].PostTransactionState");
                                }
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void Roundtrip()
        {
            TxReceipt[][] data = { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(0).TestObject }, new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject } };
            Test(data);
        }

        [Test]
        public void Roundtrip_with_IgnoreOutputs()
        {
            TxReceipt[][] data = { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(0).TestObject }, new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject } };
            foreach (TxReceipt[] receipts in data)
            {
                receipts.SetSkipStateAndStatusInRlp(true);
            }
            Test(data);
        }

        [Test]
        public void Roundtrip_with_eip658()
        {
            TxReceipt[][] data = { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject }, new[] { Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(RopstenSpecProvider.ConstantinopleBlockNumber).TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject } };
            Test(data);
        }

        [Test]
        public void Roundtrip_with_null_top_level()
        {
            Test(null);
        }

        [Test]
        public void Roundtrip_with_nulls()
        {
            TxReceipt[][] data = { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject }, null, new[] { null, Build.A.Receipt.WithAllFieldsFilled.TestObject } };
            Test(data);
        }

        [Test]
        public void Deserialize_empty()
        {
            ReceiptsMessageSerializer serializer = new(RopstenSpecProvider.Instance);
            serializer.Deserialize(new byte[0]).TxReceipts.Should().HaveCount(0);
        }

        [Test]
        public void Deserialize_non_empty_but_bytebuffer_starts_with_empty()
        {
            TxReceipt[][] data = { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(0).TestObject }, new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject } };
            ReceiptsMessage message = new(data);
            ReceiptsMessageSerializer serializer = new(RopstenSpecProvider.Instance);

            IByteBuffer buffer = Unpooled.Buffer(serializer.GetLength(message, out int _) + 1);
            buffer.WriteByte(Rlp.OfEmptySequence[0]);
            buffer.ReadByte();

            serializer.Serialize(buffer, message);
            ReceiptsMessage deserialized = serializer.Deserialize(buffer);

            deserialized.TxReceipts.Length.Should().Be(data.Length);
        }

        [Test]
        public void Roundtrip_mainnet_sample()
        {
            byte[] bytes = Bytes.FromHexString("f9012ef9012bf90128a08ccc6709a5df7acef07f97c5681356b6c37cfac15b554aff68e986f57116df2e825208b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0");
            ReceiptsMessageSerializer serializer = new(RopstenSpecProvider.Instance);
            ReceiptsMessage message = serializer.Deserialize(bytes);
            byte[] serialized = serializer.Serialize(message);
            Assert.That(serialized, Is.EqualTo(bytes));
        }

        [Test]
        public void Roundtrip_one_receipt_with_accessList()
        {
            TxReceipt[][] data = { new[] { Build.A.Receipt.WithAllFieldsFilled.WithTxType(TxType.AccessList).TestObject } };
            Test(data);
        }

        [Test]
        public void Roundtrip_with_both_txTypes_of_receipt()
        {
            TxReceipt[][] data = { new[] { Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(0).WithTxType(TxType.AccessList).TestObject }, new[] { Build.A.Receipt.WithAllFieldsFilled.WithTxType(TxType.AccessList).TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject } };
            Test(data);
        }
    }
}
