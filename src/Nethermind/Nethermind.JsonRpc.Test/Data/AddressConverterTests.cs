// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class AddressConverterTests : SerializationTestBase
    {
        [Test]
        public void Can_do_roundtrip() => TestRoundtrip(TestItem.AddressA);

        [Test]
        public void Serializes_as_prefixed_lowercase_hex() =>
            TestToJson(TestItem.AddressA, "\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\"");
    }
}
