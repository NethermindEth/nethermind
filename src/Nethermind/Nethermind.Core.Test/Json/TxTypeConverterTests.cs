//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
// 

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
