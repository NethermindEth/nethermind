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
using Nethermind.Blockchain.Receipts;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.Network.P2P.Subprotocols.Eth.V63;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class ReceiptsMessageSerializerTests
    {
        private static void Test(TxReceipt[][] txReceipts)
        {
            ReceiptsMessage message = new ReceiptsMessage(txReceipts);
            ReceiptsMessageSerializer serializer = new ReceiptsMessageSerializer(RopstenSpecProvider.Instance);
            var serialized = serializer.Serialize(message);
            ReceiptsMessage deserialized = serializer.Deserialize(serialized);

            if (txReceipts == null)
            {
                Assert.AreEqual(0, deserialized.TxReceipts.Length);
            }
            else
            {
                Assert.AreEqual(txReceipts.Length, deserialized.TxReceipts.Length, "length");
                for (int i = 0; i < txReceipts.Length; i++)
                {
                    if (txReceipts[i] == null)
                    {
                        Assert.IsNull(deserialized.TxReceipts[i], $"receipts[{i}]");
                    }
                    else
                    {
                        for (int j = 0; j < txReceipts[i].Length; j++)
                        {
                            if (txReceipts[i][j] == null)
                            {
                                Assert.IsNull(deserialized.TxReceipts[i][j], $"receipts[{i}][{j}]");
                            }
                            else
                            {
                                Assert.AreEqual(txReceipts[i][j].TxType, deserialized.TxReceipts[i][j].TxType, $"receipts[{i}][{j}].TxType");
                                Assert.AreEqual(txReceipts[i][j].Bloom, deserialized.TxReceipts[i][j].Bloom, $"receipts[{i}][{j}].Bloom");
                                Assert.Null(deserialized.TxReceipts[i][j].Error, $"receipts[{i}][{j}].Error");
                                Assert.AreEqual(0, deserialized.TxReceipts[i][j].Index, $"receipts[{i}][{j}].Index");
                                Assert.AreEqual(txReceipts[i][j].Logs.Length, deserialized.TxReceipts[i][j].Logs.Length, $"receipts[{i}][{j}].Logs.Length");
                                Assert.Null(deserialized.TxReceipts[i][j].Recipient, $"receipts[{i}][{j}].Recipient");
                                Assert.Null(deserialized.TxReceipts[i][j].Sender, $"receipts[{i}][{j}].Sender");
                                Assert.Null(deserialized.TxReceipts[i][j].BlockHash, $"receipts[{i}][{j}].BlockHash");
                                Assert.AreEqual(0L, deserialized.TxReceipts[i][j].BlockNumber, $"receipts[{i}][{j}].BlockNumber");
                                Assert.Null(deserialized.TxReceipts[i][j].ContractAddress, $"receipts[{i}][{j}].ContractAddress");
                                Assert.AreEqual(0L, deserialized.TxReceipts[i][j].GasUsed, $"receipts[{i}][{j}].GasUsed");
                                Assert.AreEqual(txReceipts[i][j].BlockNumber < RopstenSpecProvider.ByzantiumBlockNumber ? 0 : txReceipts[i][j].StatusCode, deserialized.TxReceipts[i][j].StatusCode, $"receipts[{i}][{j}].StatusCode");
                                Assert.AreEqual(txReceipts[i][j].GasUsedTotal, deserialized.TxReceipts[i][j].GasUsedTotal, $"receipts[{i}][{j}].GasUsedTotal");
                                Assert.AreEqual(txReceipts[i][j].BlockNumber < RopstenSpecProvider.ByzantiumBlockNumber ? txReceipts[i][j].PostTransactionState : null, deserialized.TxReceipts[i][j].PostTransactionState, $"receipts[{i}][{j}].PostTransactionState");
                            }
                        }
                    }
                }
            }
        }
        
        [Test]
        public void Roundtrip()
        {            
            TxReceipt[][] data = {new[] {Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(0).TestObject}, new[] {Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject}};
            Test(data);
        }
        
        [Test]
        public void Roundtrip_with_IgnoreOutputs()
        {            
            TxReceipt[][] data = {new[] {Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(0).TestObject}, new[] {Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject}};
            foreach (TxReceipt[] receipts in data)
            {
                receipts.SetSkipStateAndStatusInRlp(true);
            }
            Test(data);
        }
        
        [Test]
        public void Roundtrip_with_eip658()
        {            
            TxReceipt[][] data = {new[] {Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject}, new[] {Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(RopstenSpecProvider.ConstantinopleBlockNumber).TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject}};
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
            TxReceipt[][] data = {new[] {Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject}, null, new[] {null, Build.A.Receipt.WithAllFieldsFilled.TestObject}};
            Test(data);
        }
        
        [Test]
        public void Deserialize_empty()
        {
            ReceiptsMessageSerializer serializer = new ReceiptsMessageSerializer(RopstenSpecProvider.Instance);
            serializer.Deserialize(new byte[0]).TxReceipts.Should().HaveCount(0);
        }
        
        [Test]
        public void Roundtrip_mainnet_sample()
        {
            byte[] bytes = Bytes.FromHexString("f9012ef9012bf90128a08ccc6709a5df7acef07f97c5681356b6c37cfac15b554aff68e986f57116df2e825208b9010000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000c0");
            ReceiptsMessageSerializer serializer = new ReceiptsMessageSerializer(RopstenSpecProvider.Instance);
            ReceiptsMessage message = serializer.Deserialize(bytes);
            byte[] serialized = serializer.Serialize(message);
            Assert.AreEqual(bytes,  serialized);
        }   
        
        [Test]
        public void Roundtrip_one_receipt_with_accessList()
        {            
            TxReceipt[][] data = {new[] {Build.A.Receipt.WithAllFieldsFilled.WithTxType(TxType.AccessList).TestObject }};
            Test(data);
        }
        
        [Test]
        public void Roundtrip_with_both_txTypes_of_receipt()
        {            
            TxReceipt[][] data = {new[] {Build.A.Receipt.WithAllFieldsFilled.TestObject, Build.A.Receipt.WithAllFieldsFilled.WithBlockNumber(0).WithTxType(TxType.AccessList).TestObject}, new[] {Build.A.Receipt.WithAllFieldsFilled.WithTxType(TxType.AccessList).TestObject, Build.A.Receipt.WithAllFieldsFilled.TestObject}};
            Test(data);
        }
    }
}
