// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Evm.Tracing.ParityStyle;
using Nethermind.JsonRpc.Test.Data;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Trace
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class ParityTraceResultSerializationTests : SerializationTestBase
    {
        [Test]
        public void Can_serialize()
        {
            ParityTraceResult result = new();
            result.GasUsed = 12345;
            result.Output = new byte[] { 6, 7, 8, 9, 0 };

            TestToJson(result, "{\"gasUsed\":\"0x3039\",\"output\":\"0x0607080900\"}");
        }

        [Test]
        public void Can_serialize_nulls()
        {
            ParityTraceResult result = new();

            TestToJson(result, "{\"gasUsed\":\"0x0\",\"output\":null}");
        }
    }
}
