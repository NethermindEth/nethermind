// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Test.Data;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ParityTraceActionSerializationTests : SerializationTestBase
    {
        [Test]
        public void Can_serialize()
        {
            ParityTraceAction action = new();
            action.From = TestItem.AddressA;
            action.Gas = 12345;
            action.Input = new byte[] { 6, 7, 8, 9, 0 };
            action.To = TestItem.AddressB;
            action.Value = 24680;
            action.CallType = "call";
            action.TraceAddress = new int[] { 1, 3, 5, 7 };

            TestToJson(action, "{\"callType\":\"call\",\"from\":\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\",\"gas\":\"0x3039\",\"input\":\"0x0607080900\",\"to\":\"0x942921b14f1b1c385cd7e0cc2ef7abe5598c8358\",\"value\":\"0x6068\"}");
        }

        [Test]
        public void Can_serialize_nulls()
        {
            ParityTraceAction action = new();

            TestToJson(action, "{\"callType\":null,\"from\":null,\"gas\":\"0x0\",\"input\":null,\"to\":null,\"value\":\"0x0\"}");
        }
    }
}
