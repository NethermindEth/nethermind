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

    [Test]
    public void Serializes_as_prefixed_hex() => TestConverter(
        TestItem.PublicKeyA,
        "\"0xa49ac7010c2e0a444dfeeabadbafa4856ba4a2d732acb86d20c577b3b365fdaeb0a70ce47f890cf2f9fca562a7ed784f76eb870a2c75c0f2ab476a70ccb67e92\"",
        converter,
        static (key, publicKey) => key == publicKey);

    // Known deviation: a public key is fixed-width DATA (parity_getTransaction), but the
    // writer drops leading zeros. The reader pads the value back to 64 bytes.
    [Test]
    public void Serializes_zero_key_without_leading_zeros() => TestConverter(
        new PublicKey(new byte[64]),
        "\"0x0\"",
        converter,
        static (key, publicKey) => key == publicKey);

    static IEnumerable<TestCaseData> PublicKeyTestCases =
    [
        new TestCaseData(null).SetName("null"),
    ];
}
