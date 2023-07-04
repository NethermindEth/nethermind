// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Test.Sources;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json
{
    [TestFixture]
    public class TxTypeConverterTests : ConverterTestBase<TxType>
    {
        [TestCaseSource(typeof(TxTypeSource), nameof(TxTypeSource.Any))]
        public void Test_roundtrip(TxType arg)
        {
            TestConverter(arg, (before, after) => before.Equals(after), new TxTypeConverter());
        }
    }
}
