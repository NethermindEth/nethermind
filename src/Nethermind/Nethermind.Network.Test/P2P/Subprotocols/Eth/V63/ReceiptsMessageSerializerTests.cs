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

using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [TestFixture]
    public class ReceiptsMessageSerializerTests
    {
        private static void Test(TransactionReceipt[][] transactionReceipts)
        {
            ReceiptsMessage message = new ReceiptsMessage(transactionReceipts);
            ReceiptsMessageSerializer serializer = new ReceiptsMessageSerializer();
            var serialized = serializer.Serialize(message);
            ReceiptsMessage deserialized = serializer.Deserialize(serialized);

            if (transactionReceipts == null)
            {
                Assert.AreEqual(0, deserialized.TransactionReceipts.Length);
            }
            else
            {
                Assert.AreEqual(transactionReceipts.Length, deserialized.TransactionReceipts.Length, "length");
                for (int i = 0; i < transactionReceipts.Length; i++)
                {
                    if (transactionReceipts[i] == null)
                    {
                        Assert.IsNull(deserialized.TransactionReceipts[i], $"receipts[{i}]");
                    }
                    else
                    {
                        for (int j = 0; j < transactionReceipts[i].Length; j++)
                        {
                            if (transactionReceipts[i][j] == null)
                            {
                                Assert.IsNull(deserialized.TransactionReceipts[i][j], $"receipts[{i}][{j}]");
                            }
                            else
                            {
                                Assert.AreEqual(transactionReceipts[i][j].Bloom, deserialized.TransactionReceipts[i][j].Bloom, $"receipts[{i}][{j}].Bloom");
                                Assert.Null(deserialized.TransactionReceipts[i][j].Error, $"receipts[{i}][{j}].Error");
                                Assert.AreEqual(0, deserialized.TransactionReceipts[i][j].Index, $"receipts[{i}][{j}].Index");
                                Assert.AreEqual(transactionReceipts[i][j].Logs.Length, deserialized.TransactionReceipts[i][j].Logs.Length, $"receipts[{i}][{j}].Logs.Length");
                                Assert.Null(deserialized.TransactionReceipts[i][j].Recipient, $"receipts[{i}][{j}].Recipient");
                                Assert.Null(deserialized.TransactionReceipts[i][j].Sender, $"receipts[{i}][{j}].Sender");
                                Assert.Null(deserialized.TransactionReceipts[i][j].BlockHash, $"receipts[{i}][{j}].BlockHash");
                                Assert.AreEqual(UInt256.Zero, deserialized.TransactionReceipts[i][j].BlockNumber, $"receipts[{i}][{j}].BlockNumber");
                                Assert.Null(deserialized.TransactionReceipts[i][j].ContractAddress, $"receipts[{i}][{j}].ContractAddress");
                                Assert.AreEqual(0L, deserialized.TransactionReceipts[i][j].GasUsed, $"receipts[{i}][{j}].GasUsed");
                                Assert.AreEqual(transactionReceipts[i][j].StatusCode, deserialized.TransactionReceipts[i][j].StatusCode, $"receipts[{i}][{j}].StatusCode");
                                Assert.AreEqual(transactionReceipts[i][j].GasUsedTotal, deserialized.TransactionReceipts[i][j].GasUsedTotal, $"receipts[{i}][{j}].GasUsedTotal");
                                Assert.AreEqual(transactionReceipts[i][j].PostTransactionState, deserialized.TransactionReceipts[i][j].PostTransactionState, $"receipts[{i}][{j}].PostTransactionState");
                            }
                        }
                    }
                }
            }
        }

        [Test]
        public void Roundtrip()
        {            
            TransactionReceipt[][] data = {new[] {Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject, Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject}, new[] {Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject, Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject}};
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
            TransactionReceipt[][] data = {new[] {Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject, Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject}, null, new[] {null, Build.A.TransactionReceipt.WithAllFieldsFilled.TestObject}};
            Test(data);
        }
        
        [Test]
        public void Roundtrip_mainnet_sample()
        {
            byte[] bytes = Bytes.FromHexString("f9012ef9012bf90128a08ccc6709a5df7acef07f97c5681356b6c37cfac15b554aff68e986f57116df2e825208b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0");
            ReceiptsMessageSerializer serializer = new ReceiptsMessageSerializer();
            ReceiptsMessage message = serializer.Deserialize(bytes);
            byte[] serialized = serializer.Serialize(message);
            Assert.AreEqual(bytes,  serialized);
        }   
    }
}