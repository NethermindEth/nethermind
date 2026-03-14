// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
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

    static IEnumerable<TestCaseData> AddressTestCases =
    [
        new TestCaseData(null).SetName("null"),
        new TestCaseData(Address.Zero).SetName("zero"),
        new TestCaseData(TestItem.AddressA).SetName("testItemA"),
    ];
}
