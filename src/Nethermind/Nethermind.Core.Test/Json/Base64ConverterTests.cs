// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using NUnit.Framework;
using System.Linq;

namespace Nethermind.Core.Test.Json;

[TestFixture]
public class Base64ConverterTests : ConverterTestBase<byte[]?>
{
    [TestCase(null)]
    [TestCase(new byte[0])]
    [TestCase(new byte[] { 1 })]
    [TestCase(new byte[] { 0, 1 })]
    [TestCase(new byte[] { 0, 0, 1 })]
    [TestCase(new byte[] { 0, 0, 255 })]
    [TestCase(new byte[] { 0, 0, 1, 0 })]
    [TestCase(new byte[] { 0, 0, 1, 0, 0 })]
    [TestCase(new byte[] { 0, 0, 1, 0, 127 })]
    [TestCase(new byte[] { 0, 0, 1, 0, 255 })]
    public void ValueWithAndWithoutLeadingZeros_are_equal(byte[]? value) => TestConverter(
            value,
            static (before, after) => (before is null && after is null) || (before is not null && after is not null && before.SequenceEqual(after)),
            new Base64Converter());

    [TestCase(new byte[0], "\"\"")]
    [TestCase(new byte[] { 1 }, "\"AQ==\"")]
    [TestCase(new byte[] { 0, 0, 255 }, "\"AAD/\"")]
    public void Serializes_as_base64(byte[] value, string expectedJson) => TestConverter(
            value,
            expectedJson,
            new Base64Converter(),
            static (before, after) => (before is null && after is null) || (before is not null && after is not null && before.SequenceEqual(after)));
}
