// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;

using System;
using System.Collections.Generic;

namespace Nethermind.JsonRpc.Test.Data
{
    [Parallelizable(ParallelScope.Self)]
    [TestFixture]
    public class DictionaryConverterTests : SerializationTestBase
    {
        [Test]
        public void Can_do_roundtrip()
        {
            var dictionary = new Dictionary<Address, string>
            {
                {TestItem.AddressA, "A"},
                {TestItem.AddressB, "B"},
                {TestItem.AddressC, "C"}
            };

            TestRoundtrip(dictionary);
        }

        [Test]
        public void Can_do_roundtrip_as_key()
        {
            var dictionary = new Dictionary<AddressAsKey, string>
            {
                {TestItem.AddressA, "A"},
                {TestItem.AddressB, "B"},
                {TestItem.AddressC, "C"}
            };

            TestRoundtrip(dictionary);
        }
    }
}
