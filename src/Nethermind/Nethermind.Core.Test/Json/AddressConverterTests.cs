// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class AddressConverterTests : ConverterTestBase<Address>
    {
        [Test]
        public void Null_value()
        {
            TestConverter(null!, (address, address1) => address == address1, new AddressConverter());
        }

        [Test]
        public void Zero_value()
        {
            TestConverter(Address.Zero, (address, address1) => address == address1, new AddressConverter());
        }

        [Test]
        public void Some_value()
        {
            TestConverter(TestItem.AddressA, (address, address1) => address == address1, new AddressConverter());
        }
    }
}
