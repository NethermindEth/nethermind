// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class PublicKeyConverterTests : ConverterTestBase<PublicKey>
{
    static readonly PublicKeyConverter converter = new();

    [TestCaseSource(nameof(PublicKeyTestCases))]
    public void Test_roundtrip(PublicKey? value) => TestConverter(value!, static (key, publicKey) => key == publicKey, converter);

    [TestCaseSource(nameof(FullWidthCases))]
    public void Serializes_as_full_width_prefixed_hex(PublicKey value, string expectedJson) => TestConverter(
        value,
        expectedJson,
        converter,
        static (key, publicKey) => key == publicKey);

    static IEnumerable<TestCaseData> PublicKeyTestCases =
    [
        new TestCaseData(null).SetName("null"),
    ];

    static IEnumerable<TestCaseData> FullWidthCases =
    [
        // A public key is DATA per EIP-1474: two hex digits per byte, all 64 bytes kept.
        new TestCaseData(new PublicKey(new byte[64]), $"\"0x{new string('0', 128)}\"").SetName("full_width_zero"),
        new TestCaseData(TestItem.PublicKeyA, "\"0xa49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92\"").SetName("full_width_testItemA"),
    ];
}
