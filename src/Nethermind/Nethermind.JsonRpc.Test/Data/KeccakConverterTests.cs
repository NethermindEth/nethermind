// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
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
        public void Can_do_roundtrip()
        {
            TestRoundtrip(TestItem.KeccakA);
        }
    }
}
