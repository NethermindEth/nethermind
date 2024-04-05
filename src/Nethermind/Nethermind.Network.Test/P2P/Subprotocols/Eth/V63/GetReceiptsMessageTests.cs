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
    public class GetReceiptsMessageTests
    {
        [Test]
        public void Sets_values_from_contructor_argument()
        {
            ArrayPoolList<Hash256> hashes = new(2) { TestItem.KeccakA, TestItem.KeccakB };
            using GetReceiptsMessage message = new(hashes);
            Assert.That(message.Hashes, Is.SameAs(hashes));
        }

        [Test]
        public void Throws_on_null_argument()
        {
            Assert.Throws<ArgumentNullException>(() => new GetReceiptsMessage(null));
        }

        [Test]
        public void To_string()
        {
            using GetReceiptsMessage statusMessage = new(ArrayPoolList<Hash256>.Empty());
            _ = statusMessage.ToString();
        }
    }
}
