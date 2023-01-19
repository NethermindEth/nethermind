// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class TxTypeConverterTests : ConverterTestBase<TxType>
    {
        [TestCase(null)]
        [TestCase((TxType)0)]
        [TestCase((TxType)15)]
        [TestCase((TxType)16)]
        [TestCase((TxType)255)]
        [TestCase(TxType.Legacy)]
        [TestCase(TxType.AccessList)]
        public void Test_roundtrip(TxType arg)
        {
            TestConverter(arg, (before, after) => before.Equals(after), new TxTypeConverter());
        }
    }
}
