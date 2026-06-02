// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class PublicKeyConverterTests : ConverterTestBase<PublicKey>
{
    static readonly PublicKeyConverter converter = new();

    [TestCaseSource(nameof(PublicKeyTestCases))]
    public void Test_roundtrip(PublicKey? value) => TestConverter(value!, static (key, publicKey) => key == publicKey, converter);

    static IEnumerable<TestCaseData> PublicKeyTestCases =
    [
        new TestCaseData(null).SetName("null"),
        new TestCaseData(new PublicKey(new byte[64])).SetName("zero"),
    ];
}
