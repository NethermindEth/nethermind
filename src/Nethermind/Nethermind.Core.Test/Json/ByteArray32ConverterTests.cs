﻿//  Copyright (c) 2018 Demerzel Solutions Limited
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
        [TestCase(new byte[] {1})]
        public void Empty_value(byte[] values)
        {
            TestConverter(
                values,
                (before, after) => Bytes.AreEqual(before.WithoutLeadingZeros(), after.WithoutLeadingZeros()),
                new Bytes32Converter());
        }
    }
}