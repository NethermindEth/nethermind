// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class TransactionsMessageSerializerTests
    {
        [Test]
        public void Roundtrip_init()
        {
            TransactionsMessageSerializer serializer = new();
            Transaction transaction = new();
            transaction.GasLimit = 10;
            transaction.GasPrice = 100;
            transaction.Data = new byte[] { 4, 5, 6 };
            transaction.Nonce = 1000;
            transaction.Signature = new Signature(1, 2, 27);
            transaction.To = null;
            transaction.Value = 10000;
            transaction.Hash = transaction.CalculateHash();
            transaction.SenderAddress = null;

            TransactionsMessage message = new(new[] { transaction, transaction });
            SerializerTester.TestZero(serializer, message, "e2d08203e8640a80822710830405061b0102d08203e8640a80822710830405061b0102");
        }

        [Test]
        public void Roundtrip_call()
        {
            TransactionsMessageSerializer serializer = new();
            Transaction transaction = new();
            transaction.Data = new byte[] { 1, 2, 3 };
            transaction.GasLimit = 10;
            transaction.GasPrice = 100;
            transaction.Nonce = 1000;
            transaction.Signature = new Signature(1, 2, 27);
            transaction.To = TestItem.AddressA;
            transaction.Value = 10000;
            transaction.Hash = transaction.CalculateHash();
            transaction.SenderAddress = null;

            TransactionsMessage message = new(new[] { transaction, transaction });
            SerializerTester.TestZero(serializer, message, "f84ae48203e8640a94b7705ae4c6f81b66cdb323c65f4e8133690fc099822710830102031b0102e48203e8640a94b7705ae4c6f81b66cdb323c65f4e8133690fc099822710830102031b0102");
        }

        [Test]
        public void Can_handle_empty()
        {
            TransactionsMessageSerializer serializer = new();
            TransactionsMessage message = new(new Transaction[] { });

            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void To_string_empty()
        {
            TransactionsMessage message = new(new Transaction[] { });
            TransactionsMessage message2 = new(null);

            _ = message.ToString();
            _ = message2.ToString();
        }
    }
}
