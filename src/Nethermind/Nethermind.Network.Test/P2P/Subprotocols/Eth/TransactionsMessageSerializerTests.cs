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
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Network.P2P.Subprotocols.Eth;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth
{
    [TestFixture]
    public class TransactionsMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            TransactionsMessageSerializer serializer = new TransactionsMessageSerializer();
            Transaction transaction = new Transaction();
            transaction.Data = new byte[] {1, 2, 3};
            transaction.GasLimit = 10;
            transaction.GasPrice = 100;
            transaction.Init = new byte[] {4, 5, 6};
            transaction.Nonce = 1000;
            transaction.Signature = new Signature(1, 2, 27);
            transaction.To = Address.Zero;
            transaction.Value = 10000;
            transaction.Hash = Transaction.CalculateHash(transaction);

            TransactionsMessage message = new TransactionsMessage(transaction, transaction);
            byte[] bytes = serializer.Serialize(message);
            byte[] expectedBytes = Bytes.FromHexString("f84ae48203e8640a940000000000000000000000000000000000000000822710830102031b0102e48203e8640a940000000000000000000000000000000000000000822710830102031b0102");

            Assert.True(Bytes.AreEqual(bytes, expectedBytes), "bytes");

            TransactionsMessage deserialized = serializer.Deserialize(bytes);
            Assert.AreEqual(message.Transactions.Length, deserialized.Transactions.Length, "length");
            // TODO: Chain IDs need to be handled properly
//            Assert.AreEqual(message.Transactions[0].ChainId, deserialized.Transactions[0].ChainId, $"{nameof(Transaction.ChainId)}");
            Assert.AreEqual(message.Transactions[0].Data, deserialized.Transactions[0].Data, $"{nameof(Transaction.Data)}");
            Assert.AreEqual(message.Transactions[0].GasLimit, deserialized.Transactions[0].GasLimit, $"{nameof(Transaction.GasLimit)}");
            Assert.AreEqual(message.Transactions[0].GasPrice, deserialized.Transactions[0].GasPrice, $"{nameof(Transaction.GasPrice)}");
            Assert.AreEqual(message.Transactions[0].Hash, deserialized.Transactions[0].Hash, $"{nameof(Transaction.Hash)}");
            // TODO: cannot test Init and Data at once with one transaction only
//            Assert.AreEqual(message.Transactions[0].Init, deserialized.Transactions[0].Init, $"{nameof(Transaction.Init)}");
            Assert.AreEqual(message.Transactions[0].Nonce, deserialized.Transactions[0].Nonce, $"{nameof(Transaction.Nonce)}");
            Assert.AreEqual(message.Transactions[0].Signature, deserialized.Transactions[0].Signature, $"{nameof(Transaction.Signature)}");
            Assert.AreEqual(message.Transactions[0].To, deserialized.Transactions[0].To, $"{nameof(Transaction.To)}");
            Assert.AreEqual(message.Transactions[0].Value, deserialized.Transactions[0].Value, $"{nameof(Transaction.Value)}");
            
            SerializerTester.Test(serializer, message);
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void Can_handle_empty()
        {
            TransactionsMessageSerializer serializer = new TransactionsMessageSerializer();
            TransactionsMessage message = new TransactionsMessage();
            
            SerializerTester.Test(serializer, message);
            SerializerTester.TestZero(serializer, message);
        }
    }
}