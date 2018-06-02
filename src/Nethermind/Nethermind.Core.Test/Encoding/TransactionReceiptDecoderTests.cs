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

using Nethermind.Core.Encoding;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.Core.Test.Encoding
{
    [TestFixture]
    public class TransactionReceiptDecoderTests
    {
        [Test]
        public void Can_do_roundtrip()
        {
            TransactionReceipt receipt = Build.A.Receipt.TestObject;
            TransactionReceiptDecoder decoder = new TransactionReceiptDecoder();
            Rlp rlp = decoder.Encode(receipt);
            TransactionReceipt deserialized = decoder.Decode(rlp.Bytes.AsRlpContext());

            Assert.AreEqual(receipt.GasUsed, deserialized.GasUsed, "gas used");
            Assert.AreEqual(receipt.Bloom, deserialized.Bloom, "bloom");
            Assert.AreEqual(receipt.PostTransactionState, deserialized.PostTransactionState, "post transaction state");
            Assert.AreEqual(receipt.Recipient, deserialized.Recipient, "recipient");
            Assert.AreEqual(receipt.StatusCode, deserialized.StatusCode, "status");
        }
    }
}