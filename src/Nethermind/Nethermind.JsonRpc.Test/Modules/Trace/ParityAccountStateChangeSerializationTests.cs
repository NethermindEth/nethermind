// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Int256;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Test.Data;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ParityAccountStateChangeSerializationTests : SerializationTestBase
    {
        [Test]
        public void Can_serialize()
        {
            ParityAccountStateChange result = new();
            result.Balance = new ParityStateChange<UInt256?>(1, 2);
            result.Nonce = new ParityStateChange<UInt256?>(0, 1);
            result.Storage = new Dictionary<UInt256, ParityStateChange<UInt256>>();
            result.Storage[1] = new ParityStateChange<UInt256>((UInt256)1, (UInt256)2);

            TestToJson(result, "{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":\"0x2\"}},\"code\":\"=\",\"nonce\":{\"*\":{\"from\":\"0x0\",\"to\":\"0x1\"}},\"storage\":{\"0x0000000000000000000000000000000000000000000000000000000000000001\":{\"*\":{\"from\":\"0x0000000000000000000000000000000000000000000000000000000000000001\",\"to\":\"0x0000000000000000000000000000000000000000000000000000000000000002\"}}}}");
        }

        [Test]
        public void Can_serialize_null_to_1_change()
        {
            ParityAccountStateChange result = new();
            result.Balance = new ParityStateChange<UInt256?>(null, 1);

            TestToJson(result, "{\"balance\":{\"+\":\"0x1\"},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}}");
        }

        [Test]
        public void Can_serialize_1_to_null()
        {
            ParityAccountStateChange result = new();
            result.Balance = new ParityStateChange<UInt256?>(1, null);

            TestToJson(result, "{\"balance\":{\"*\":{\"from\":\"0x1\",\"to\":null}},\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}}");
        }

        [Test]
        public void Can_serialize_nulls()
        {
            ParityAccountStateChange result = new();

            TestToJson(result, "{\"balance\":\"=\",\"code\":\"=\",\"nonce\":\"=\",\"storage\":{}}");
        }
    }
}
