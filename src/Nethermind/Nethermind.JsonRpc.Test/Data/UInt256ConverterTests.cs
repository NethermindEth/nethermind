// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class UInt256ConverterTests : SerializationTestBase
    {
        [Test]
        public void Can_do_roundtrip() => TestRoundtrip((UInt256)123456789);

        [Test]
        public void Can_do_roundtrip_big() => TestRoundtrip(UInt256.Parse("1321312414124781461278412647816487146817246816418746187246187468714681"));

        [Test]
        public void Serializes_as_hex_quantity() => TestToJson((UInt256)123456789, "\"0x75bcd15\"");

        [Test]
        public void Serializes_big_value_as_hex_quantity() => TestToJson(
            UInt256.Parse("1321312414124781461278412647816487146817246816418746187246187468714681"),
            "\"0x31029c8dfe1bb525f0e013a68e2cdcfc562b27e9e16d9b0fb8044672b9\"");
    }
}
