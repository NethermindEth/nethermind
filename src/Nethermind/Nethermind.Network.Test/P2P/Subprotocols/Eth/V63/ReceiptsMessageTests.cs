// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class ReceiptsMessageTests
    {
        [Test]
        public void Accepts_nulls_inside()
        {
            TxReceipt[][] data = { new[] { new TxReceipt(), new TxReceipt() }, null };
            ReceiptsMessage message = new(data);
            Assert.AreSame(data, message.TxReceipts);
        }

        [Test]
        public void Accepts_nulls_top_level()
        {
            ReceiptsMessage message = new(null);
            Assert.AreEqual(0, message.TxReceipts.Length);
        }

        [Test]
        public void Sets_values_from_constructor_argument()
        {
            TxReceipt[][] data = { new[] { new TxReceipt(), new TxReceipt() }, new[] { new TxReceipt(), new TxReceipt() } };
            ReceiptsMessage message = new(data);
            Assert.AreSame(data, message.TxReceipts);
        }

        [Test]
        public void To_string()
        {
            ReceiptsMessage statusMessage = new(null);
            _ = statusMessage.ToString();
        }
    }
}
