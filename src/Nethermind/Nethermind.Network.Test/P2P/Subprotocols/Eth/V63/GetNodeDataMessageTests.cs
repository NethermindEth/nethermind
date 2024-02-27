// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class GetNodeDataMessageTests
    {
        [Test]
        public void Sets_values_from_constructor_argument()
        {
            ArrayPoolList<Hash256> keys = new(2) { TestItem.KeccakA, TestItem.KeccakB };
            using GetNodeDataMessage message = new(keys);
            Assert.That(message.Hashes, Is.SameAs(keys));
        }

        [Test]
        public void Throws_on_null_argument()
        {
            Assert.Throws<ArgumentNullException>(() => _ = new GetNodeDataMessage(null));
        }

        [Test]
        public void To_string()
        {
            using GetNodeDataMessage statusMessage = new(ArrayPoolList<Hash256>.Empty());
            _ = statusMessage.ToString();
        }
    }
}
