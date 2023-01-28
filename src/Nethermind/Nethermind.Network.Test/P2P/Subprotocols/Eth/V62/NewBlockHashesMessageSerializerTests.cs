// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V62.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V62
{
    [TestFixture, Parallelizable(ParallelScope.All)]
    public class NewBlockHashesMessageSerializerTests
    {
        [Test]
        public void Roundtrip()
        {
            NewBlockHashesMessage message = new((Keccak.Compute("1"), 1), (Keccak.Compute("2"), 2));
            var serializer = new NewBlockHashesMessageSerializer();
            SerializerTester.TestZero(serializer, message);
        }

        [Test]
        public void To_string()
        {
            NewBlockHashesMessage statusMessage = new();
            _ = statusMessage.ToString();
        }
    }
}
