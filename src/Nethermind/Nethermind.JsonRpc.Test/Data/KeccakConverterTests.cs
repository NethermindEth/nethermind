// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class KeccakConverterTests : SerializationTestBase
    {
        [Test]
        public void Can_do_roundtrip() => TestRoundtrip(TestItem.KeccakA);

        [Test]
        public void Serializes_as_prefixed_hex() =>
            TestToJson(TestItem.KeccakA, "\"0x03783fac2efed8fbc9ad443e592ee30e61d65f471140c10ca155e937b435b760\"");
    }
}
