// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using NUnit.Framework;
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
            Dictionary<Address, string> dictionary = new()
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
            Dictionary<AddressAsKey, string> dictionary = new()
            {
                {TestItem.AddressA, "A"},
                {TestItem.AddressB, "B"},
                {TestItem.AddressC, "C"}
            };

            TestRoundtrip(dictionary);
        }

        // One entry keeps the expectation free of dictionary ordering.
        [Test]
        public void Serializes_address_key_as_prefixed_hex() =>
            TestToJson(new Dictionary<Address, string> { { TestItem.AddressA, "A" } },
                "{\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":\"A\"}");

        [Test]
        public void Serializes_address_as_key_type_as_prefixed_hex() =>
            TestToJson(new Dictionary<AddressAsKey, string> { { TestItem.AddressA, "A" } },
                "{\"0xb7705ae4c6f81b66cdb323c65f4e8133690fc099\":\"A\"}");
    }
}
