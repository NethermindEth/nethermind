// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Core.Test.Builders;
using Nethermind.Network.P2P.Subprotocols.Eth.V63.Messages;
using NUnit.Framework;

namespace Nethermind.Network.Test.P2P.Subprotocols.Eth.V63
{
    [Parallelizable(ParallelScope.All)]
    public class NodeDataMessageSerializerTests
    {
        private static void Test(IOwnedReadOnlyList<byte[]>? data, string? expected = null)
        {
            using NodeDataMessage message = new(data is not null ? new ByteArrayListAdapter(data) : null);

            NodeDataMessageSerializer serializer = new();
            SerializerTester.TestZero(serializer, message, expected);
        }

        [Test]
        public void Roundtrip()
        {
            using ArrayPoolList<byte[]> data = new(3) { TestItem.KeccakA.BytesToArray(), TestItem.KeccakB.BytesToArray(), TestItem.KeccakC.BytesToArray() };
            Test(data, EthSerializerGoldens.KeccakAbcListRlp);
        }

        [Test]
        public void Roundtrip_with_null_top_level() => Test(null, EthSerializerGoldens.EmptyListRlp);

        [Test]
        public void Roundtrip_with_nulls()
        {
            using ArrayPoolList<byte[]> data = new(3) { TestItem.KeccakA.BytesToArray(), Array.Empty<byte>(), TestItem.KeccakC.BytesToArray() };
            // The empty entry encodes as the empty byte string 0x80.
            Test(data, "f843a003783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b76080a0017e667f4b8c174291d1543c466717566e206df1bfd6f30271055ddafdb18f72");
        }
    }
}
