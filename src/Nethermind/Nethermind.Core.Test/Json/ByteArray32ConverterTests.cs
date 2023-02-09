// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class Bytes32ConverterTests : ConverterTestBase<byte[]>
    {
        [TestCase(null)]
        [TestCase(new byte[0])]
        [TestCase(new byte[] { 1 })]
        public void Empty_value(byte[] values)
        {
            TestConverter(
                values,
                (before, after) => Bytes.AreEqual(before.WithoutLeadingZeros(), after.WithoutLeadingZeros()),
                new Bytes32Converter());
        }
    }
}
