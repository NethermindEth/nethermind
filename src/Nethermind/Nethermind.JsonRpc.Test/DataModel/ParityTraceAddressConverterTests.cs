/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using Nethermind.JsonRpc.Trace;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.DataModel
{
    [TestFixture]
    public class ParityTraceAddressConverterTests : SerializationTestBase
    {
        [Test]
        public void Can_do_roundtrip()
        {
            Func<int[], int[], bool> comparer = (a, b) =>
            {
                if (a.Length != b.Length)
                {
                    return false;
                }

                for (int i = 0; i < a.Length; i++)
                {
                    if (a[i] != b[i])
                    {
                        return false;
                    }
                }

                return true;
            }; 
            
            TestConverter(new int[] {1, 2, 3, 1000, 10000}, comparer, new ParityTraceAddressConverter());
        }
    }
}