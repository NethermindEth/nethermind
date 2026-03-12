// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class AddressConverterTests : ConverterTestBase<Address>
{
    static readonly AddressConverter converter = new();

    [TestCaseSource(nameof(AddressTestCases))]
    public void Test_roundtrip(Address? value)
    {
        TestConverter(value!, static (address, address1) => address == address1, converter);
    }

    static object?[] AddressTestCases =
    [
        new object?[] { null },
        new object?[] { Address.Zero },
        new object?[] { TestItem.AddressA },
    ];
}
